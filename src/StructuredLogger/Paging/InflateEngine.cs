using System;
using System.Collections.Generic;
using System.IO;

namespace Microsoft.Build.Logging.StructuredLogger.Paging
{
    /// <summary>
    /// A resumable, managed implementation of RFC 1951 (deflate) with RFC 1952 (gzip) framing.
    /// It exists because System.IO.Compression.DeflateStream cannot report where in the compressed
    /// input a block starts, cannot start in the middle of a stream and cannot be primed with the
    /// 32 KB history window that a block in the middle of a stream refers back to.
    /// This engine can do all three, which is what random access ("zran" style) needs:
    /// <list type="bullet">
    /// <item>while decoding sequentially it records <see cref="SeekPoint"/>s at deflate block boundaries
    /// (compressed bit offset + decompressed offset + the last 32 KB of output),</item>
    /// <item>given a seek point it starts decoding there: positions the input at the bit
    /// (the equivalent of zlib's inflatePrime) and loads the window (inflateSetDictionary).</item>
    /// </list>
    /// The decoder is table driven (one flat lookup table per Huffman code) and works on a 64 bit
    /// bit buffer. It decodes up to 128 KB ahead of what the consumer has read.
    /// </summary>
    internal sealed class InflateEngine
    {
        public const int WindowSize = 32768;
        private const int Chunk = 1 << 17;
        private const int Limit = WindowSize + Chunk;
        private const int MaxMatch = 258;

        private enum State
        {
            GzipHeader,
            BlockHeader,
            Stored,
            Huffman,
            Trailer,
            Done,
        }

        private static readonly int[] LenBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
        private static readonly int[] LenExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        private static readonly int[] DistBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
        private static readonly int[] DistExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
        private static readonly byte[] CodeLengthOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        private static readonly int[] FixedLitTable;
        private static readonly int FixedLitBits;
        private static readonly int[] FixedDistTable;
        private static readonly int FixedDistBits;

        static InflateEngine()
        {
            var lens = new byte[288];
            for (int i = 0; i < 144; i++) lens[i] = 8;
            for (int i = 144; i < 256; i++) lens[i] = 9;
            for (int i = 256; i < 280; i++) lens[i] = 7;
            for (int i = 280; i < 288; i++) lens[i] = 8;
            FixedLitTable = new int[1 << 9];
            BuildTable(lens, 0, 288, FixedLitTable, out FixedLitBits, new int[16], new int[16]);

            var dlens = new byte[30];
            for (int i = 0; i < 30; i++) dlens[i] = 5;
            FixedDistTable = new int[1 << 5];
            BuildTable(dlens, 0, 30, FixedDistTable, out FixedDistBits, new int[16], new int[16]);
        }

        private readonly Stream input;
        private readonly byte[] inBuf = new byte[1 << 16];
        private int inPos;
        private int inEnd;

        /// <summary>Absolute offset (in the compressed file) of inBuf[0].</summary>
        private long inBufOffset;

        private ulong bitBuf;
        private int bitCnt;

        private readonly byte[] buf = new byte[Limit + MaxMatch];
        private int outPos = WindowSize;
        private int readPos = WindowSize;

        /// <summary>Index in buf of the oldest byte that is a valid back-reference target.</summary>
        private int validFrom = WindowSize;

        /// <summary>Decompressed offset of buf[i] is outBase + i.</summary>
        private long outBase = -WindowSize;

        private State state;
        private bool finalBlock;
        private bool gzip;
        private int storedRemaining;
        private int[] litTable;
        private int litMask;
        private int[] distTable;
        private int distMask;
        private readonly int[] dynLit = new int[1 << 15];
        private readonly int[] dynDist = new int[1 << 15];
        private readonly byte[] lengths = new byte[320];
        private readonly int[] clTable = new int[1 << 7];
        private readonly int[] countScratch = new int[16];
        private readonly int[] nextScratch = new int[16];

        private readonly List<SeekPoint> recordInto;
        private readonly long spacingBytes;
        private long lastPointByte = long.MinValue;
        private bool pointDue = true;
        private long memberOutputStart;

        private InflateEngine(Stream input, long inputOffset, List<SeekPoint> recordInto, long spacingBytes)
        {
            this.input = input;
            this.inBufOffset = inputOffset;
            this.recordInto = recordInto;
            this.spacingBytes = spacingBytes;
        }

        /// <summary>True when the input ended in the middle of the compressed data.</summary>
        public bool Truncated { get; private set; }

        /// <summary>True when the last deflate block (of the last gzip member) was decoded.</summary>
        public bool Finished => state == State.Done && !Truncated;

        /// <summary>Number of decompressed bytes handed to the consumer so far.</summary>
        public long OutputPosition => outBase + readPos;

        /// <summary>Number of decompressed bytes decoded so far (can be ahead of <see cref="OutputPosition"/>).</summary>
        public long DecodedPosition => outBase + outPos;

        /// <summary>Absolute compressed offset of the first byte that has not been loaded into the decoder.</summary>
        public long InputBytesLoaded => inBufOffset + inPos;

        /// <summary>
        /// Starts at the beginning of a gzip file (or of a concatenation of gzip members).
        /// </summary>
        /// <param name="input">Positioned at <paramref name="inputOffset"/>, the start of a gzip member.</param>
        /// <param name="inputOffset">Absolute offset of the first byte of <paramref name="input"/>.</param>
        /// <param name="points">If not null, seek points are appended here.</param>
        /// <param name="spacingBytes">Minimum distance in compressed bytes between seek points.</param>
        public static InflateEngine StartGzip(Stream input, long inputOffset, List<SeekPoint> points, long spacingBytes)
        {
            var engine = new InflateEngine(input, inputOffset, points, spacingBytes);
            engine.gzip = true;
            engine.state = State.GzipHeader;
            return engine;
        }

        /// <summary>
        /// Starts decoding at a deflate block boundary in the middle of a stream.
        /// </summary>
        /// <param name="input">Positioned at the byte that contains the first bit of the block header.</param>
        /// <param name="inputOffset">Absolute offset of that byte in the compressed file (point.InputBitOffset / 8).</param>
        /// <param name="point">The seek point to resume from.</param>
        public static InflateEngine StartAt(Stream input, long inputOffset, SeekPoint point)
        {
            var engine = new InflateEngine(input, inputOffset, null, 0);
            engine.state = State.BlockHeader;
            engine.pointDue = false;

            int skip = (int)(point.InputBitOffset & 7);
            if (skip != 0)
            {
                // the equivalent of inflatePrime: drop the bits of the first byte that belong to the previous block
                engine.SlowFill();
                if (engine.bitCnt < 8)
                {
                    throw new InvalidDataException("Seek point is past the end of the input.");
                }

                engine.bitBuf >>= skip;
                engine.bitCnt -= skip;
            }

            // the equivalent of inflateSetDictionary: the history before the first output byte
            var window = point.Window;
            if (window != null && window.Length > 0)
            {
                int n = Math.Min(window.Length, WindowSize);
                Buffer.BlockCopy(window, window.Length - n, engine.buf, WindowSize - n, n);
                engine.validFrom = WindowSize - n;
            }

            engine.outBase = point.OutputOffset - WindowSize;
            return engine;
        }

        /// <summary>Reads decompressed bytes. Returns 0 at the end of the data (or of the truncated data).</summary>
        public int Read(byte[] destination, int offset, int count)
        {
            if (count == 0)
            {
                return 0;
            }

            if (readPos == outPos)
            {
                if (state == State.Done)
                {
                    return 0;
                }

                if (outPos >= Limit)
                {
                    Compact();
                }

                Decode();
                if (readPos == outPos)
                {
                    return 0;
                }
            }

            int n = Math.Min(count, outPos - readPos);
            Buffer.BlockCopy(buf, readPos, destination, offset, n);
            readPos += n;
            return n;
        }

        private void Compact()
        {
            // keep the last WindowSize bytes as history
            int keepFrom = outPos - WindowSize;
            Buffer.BlockCopy(buf, keepFrom, buf, 0, WindowSize);
            outBase += keepFrom;
            outPos = WindowSize;
            readPos = WindowSize;
            validFrom = 0;
        }

        private void Decode()
        {
            try
            {
                while (outPos < Limit && state != State.Done)
                {
                    switch (state)
                    {
                        case State.GzipHeader:
                            ReadGzipHeader();
                            break;
                        case State.BlockHeader:
                            ReadBlockHeader();
                            break;
                        case State.Stored:
                            CopyStored();
                            break;
                        case State.Huffman:
                            DecodeHuffman();
                            break;
                        case State.Trailer:
                            ReadGzipTrailer();
                            break;
                    }
                }
            }
            catch (EndOfInputException)
            {
                Truncated = true;
                state = State.Done;
            }
        }

        private sealed class EndOfInputException : Exception
        {
        }

        // ---- input ----

        private bool FillInput()
        {
            inBufOffset += inEnd;
            inPos = 0;
            inEnd = 0;
            int n = input.Read(inBuf, 0, inBuf.Length);
            if (n <= 0)
            {
                return false;
            }

            inEnd = n;
            return true;
        }

        /// <summary>Loads bytes one at a time until the bit buffer holds more than 56 bits or the input ends.</summary>
        private void SlowFill()
        {
            while (bitCnt <= 56)
            {
                if (inPos == inEnd && !FillInput())
                {
                    return;
                }

                bitBuf |= (ulong)inBuf[inPos++] << bitCnt;
                bitCnt += 8;
            }
        }

        private int Bits(int n)
        {
            if (bitCnt < n)
            {
                SlowFill();
                if (bitCnt < n)
                {
                    throw new EndOfInputException();
                }
            }

            int v = (int)(bitBuf & ((1UL << n) - 1));
            bitBuf >>= n;
            bitCnt -= n;
            return v;
        }

        /// <summary>Reads a byte; the bit buffer must be byte aligned. Returns -1 at the end of the input.</summary>
        private int ReadAlignedByte()
        {
            if (bitCnt >= 8)
            {
                int b = (int)(bitBuf & 0xFF);
                bitBuf >>= 8;
                bitCnt -= 8;
                return b;
            }

            bitBuf = 0;
            bitCnt = 0;
            if (inPos == inEnd && !FillInput())
            {
                return -1;
            }

            return inBuf[inPos++];
        }

        private int NeedAlignedByte()
        {
            int b = ReadAlignedByte();
            if (b < 0)
            {
                throw new EndOfInputException();
            }

            return b;
        }

        private void AlignToByte()
        {
            int drop = bitCnt & 7;
            bitBuf >>= drop;
            bitCnt -= drop;
        }

        /// <summary>Absolute position, in bits, of the next bit the decoder will consume.</summary>
        private long BitPosition => (inBufOffset + inPos) * 8 - bitCnt;

        // ---- gzip framing ----

        private void ReadGzipHeader()
        {
            // a clean end of the file between members is not an error
            int id1 = ReadAlignedByte();
            if (id1 < 0)
            {
                state = State.Done;
                return;
            }

            int id2 = NeedAlignedByte();
            if (id1 != 0x1f || id2 != 0x8b)
            {
                throw new InvalidDataException("Not a gzip stream (bad magic number).");
            }

            if (NeedAlignedByte() != 8)
            {
                throw new InvalidDataException("Unsupported gzip compression method.");
            }

            int flags = NeedAlignedByte();
            for (int i = 0; i < 6; i++)
            {
                NeedAlignedByte(); // mtime, xfl, os
            }

            if ((flags & 4) != 0)
            {
                int xlen = NeedAlignedByte() | (NeedAlignedByte() << 8);
                for (int i = 0; i < xlen; i++)
                {
                    NeedAlignedByte();
                }
            }

            if ((flags & 8) != 0)
            {
                while (NeedAlignedByte() != 0)
                {
                }
            }

            if ((flags & 16) != 0)
            {
                while (NeedAlignedByte() != 0)
                {
                }
            }

            if ((flags & 2) != 0)
            {
                NeedAlignedByte();
                NeedAlignedByte();
            }

            memberOutputStart = outBase + outPos;
            state = State.BlockHeader;
            pointDue = true; // always have a point at the start of a member (empty window)
        }

        private void ReadGzipTrailer()
        {
            AlignToByte();
            uint crc = 0;
            for (int i = 0; i < 4; i++)
            {
                crc |= (uint)NeedAlignedByte() << (8 * i);
            }

            uint isize = 0;
            for (int i = 0; i < 4; i++)
            {
                isize |= (uint)NeedAlignedByte() << (8 * i);
            }

            // The CRC is not verified here (it would cost a pass over all the output and a seek read
            // can not verify it anyway); the length check catches most truncation and corruption.
            if (isize != (uint)(outBase + outPos - memberOutputStart))
            {
                throw new InvalidDataException("The gzip trailer length does not match the decompressed length.");
            }

            // a window does not carry over to a following member
            validFrom = outPos;
            state = State.GzipHeader;
        }

        // ---- deflate ----

        private void RecordSeekPoint()
        {
            long bit = BitPosition;
            int from = Math.Max(validFrom, outPos - WindowSize);
            var window = new byte[outPos - from];
            Buffer.BlockCopy(buf, from, window, 0, window.Length);
            recordInto.Add(new SeekPoint(bit, outBase + outPos, window));
            lastPointByte = bit >> 3;
            pointDue = false;
        }

        private void ReadBlockHeader()
        {
            if (recordInto != null && (pointDue || (BitPosition >> 3) - lastPointByte >= spacingBytes))
            {
                RecordSeekPoint();
            }

            finalBlock = Bits(1) == 1;
            int type = Bits(2);
            switch (type)
            {
                case 0:
                    AlignToByte();
                    int len = Bits(16);
                    int nlen = Bits(16);
                    if ((len ^ 0xFFFF) != nlen)
                    {
                        throw new InvalidDataException("Invalid stored block lengths.");
                    }

                    storedRemaining = len;
                    state = State.Stored;
                    if (len == 0)
                    {
                        EndOfBlock();
                    }

                    break;
                case 1:
                    litTable = FixedLitTable;
                    litMask = (1 << FixedLitBits) - 1;
                    distTable = FixedDistTable;
                    distMask = (1 << FixedDistBits) - 1;
                    state = State.Huffman;
                    break;
                case 2:
                    ReadDynamicTables();
                    state = State.Huffman;
                    break;
                default:
                    throw new InvalidDataException("Invalid deflate block type.");
            }
        }

        private void EndOfBlock()
        {
            if (finalBlock)
            {
                if (gzip)
                {
                    state = State.Trailer;
                }
                else
                {
                    state = State.Done;
                }
            }
            else
            {
                state = State.BlockHeader;
            }
        }

        private void ReadDynamicTables()
        {
            int hlit = Bits(5) + 257;
            int hdist = Bits(5) + 1;
            int hclen = Bits(4) + 4;
            if (hlit > 286 || hdist > 30)
            {
                throw new InvalidDataException("Invalid dynamic block header.");
            }

            Array.Clear(lengths, 0, 19);
            for (int i = 0; i < hclen; i++)
            {
                lengths[CodeLengthOrder[i]] = (byte)Bits(3);
            }

            BuildTable(lengths, 0, 19, clTable, out int clBits, countScratch, nextScratch);
            int clMask = (1 << clBits) - 1;

            int total = hlit + hdist;
            var lens = new byte[total];
            int idx = 0;
            while (idx < total)
            {
                if (bitCnt < 15)
                {
                    SlowFill();
                }

                int e = clTable[(int)bitBuf & clMask];
                int l = e & 15;
                if (l == 0)
                {
                    throw new InvalidDataException("Invalid code length code.");
                }

                if (l > bitCnt)
                {
                    throw new EndOfInputException();
                }

                bitBuf >>= l;
                bitCnt -= l;
                int sym = e >> 4;
                if (sym < 16)
                {
                    lens[idx++] = (byte)sym;
                }
                else
                {
                    int repeat;
                    byte value = 0;
                    if (sym == 16)
                    {
                        if (idx == 0)
                        {
                            throw new InvalidDataException("Invalid code length repeat.");
                        }

                        value = lens[idx - 1];
                        repeat = 3 + Bits(2);
                    }
                    else if (sym == 17)
                    {
                        repeat = 3 + Bits(3);
                    }
                    else
                    {
                        repeat = 11 + Bits(7);
                    }

                    if (idx + repeat > total)
                    {
                        throw new InvalidDataException("Invalid code length repeat.");
                    }

                    while (repeat-- > 0)
                    {
                        lens[idx++] = value;
                    }
                }
            }

            if (lens[256] == 0)
            {
                throw new InvalidDataException("Missing end-of-block code.");
            }

            BuildTable(lens, 0, hlit, dynLit, out int litBits, countScratch, nextScratch);
            BuildTable(lens, hlit, hdist, dynDist, out int distBits, countScratch, nextScratch);
            litTable = dynLit;
            litMask = (1 << litBits) - 1;
            distTable = dynDist;
            distMask = (1 << distBits) - 1;
        }

        /// <summary>
        /// Builds a flat decoding table for a canonical Huffman code: indexed by the next maxLength bits
        /// of the input (LSB first), the entry is (symbol << 4) | codeLength, 0 for an unused bit pattern.
        /// </summary>
        private static void BuildTable(byte[] lens, int offset, int count, int[] table, out int bits, int[] codeCount, int[] nextCode)
        {
            Array.Clear(codeCount, 0, 16);
            for (int i = 0; i < count; i++)
            {
                codeCount[lens[offset + i]]++;
            }

            codeCount[0] = 0;
            int maxLen = 15;
            while (maxLen > 0 && codeCount[maxLen] == 0)
            {
                maxLen--;
            }

            if (maxLen == 0)
            {
                // no codes at all (e.g. a block with no matches has no distance codes)
                bits = 1;
                table[0] = 0;
                table[1] = 0;
                return;
            }

            int left = 1;
            for (int l = 1; l <= 15; l++)
            {
                left <<= 1;
                left -= codeCount[l];
                if (left < 0)
                {
                    throw new InvalidDataException("Over-subscribed Huffman code.");
                }
            }

            int code = 0;
            for (int l = 1; l <= 15; l++)
            {
                code = (code + codeCount[l - 1]) << 1;
                nextCode[l] = code;
            }

            int size = 1 << maxLen;
            Array.Clear(table, 0, size);
            for (int sym = 0; sym < count; sym++)
            {
                int l = lens[offset + sym];
                if (l == 0)
                {
                    continue;
                }

                int c = nextCode[l]++;
                int rev = 0;
                for (int i = 0; i < l; i++)
                {
                    rev = (rev << 1) | ((c >> i) & 1);
                }

                int entry = (sym << 4) | l;
                int step = 1 << l;
                for (int k = rev; k < size; k += step)
                {
                    table[k] = entry;
                }
            }

            bits = maxLen;
        }

        private void CopyStored()
        {
            while (storedRemaining > 0 && outPos < Limit)
            {
                if (bitCnt >= 8)
                {
                    buf[outPos++] = (byte)bitBuf;
                    bitBuf >>= 8;
                    bitCnt -= 8;
                    storedRemaining--;
                    continue;
                }

                bitBuf = 0;
                bitCnt = 0;
                if (inPos == inEnd && !FillInput())
                {
                    throw new EndOfInputException();
                }

                int n = Math.Min(Math.Min(storedRemaining, inEnd - inPos), Limit - outPos);
                Buffer.BlockCopy(inBuf, inPos, buf, outPos, n);
                inPos += n;
                outPos += n;
                storedRemaining -= n;
            }

            if (storedRemaining == 0)
            {
                EndOfBlock();
            }
        }

        private void DecodeHuffman()
        {
            ulong bb = bitBuf;
            int bc = bitCnt;
            int ip = inPos;
            int op = outPos;
            byte[] ib = inBuf;
            byte[] ob = buf;
            int[] lt = litTable;
            int[] dt = distTable;
            int lmask = litMask;
            int dmask = distMask;
            int vf = validFrom;

            try
            {
                while (op < Limit)
                {
                    if (bc < 48)
                    {
                        if (inEnd - ip >= 8)
                        {
                            bb |= BitConverter.ToUInt64(ib, ip) << bc;
                            ip += (63 - bc) >> 3;
                            bc |= 56;
                        }
                        else
                        {
                            bitBuf = bb;
                            bitCnt = bc;
                            inPos = ip;
                            SlowFill();
                            bb = bitBuf;
                            bc = bitCnt;
                            ip = inPos;
                        }
                    }

                    int e = lt[(int)bb & lmask];
                    int len = e & 15;
                    if (len == 0)
                    {
                        throw new InvalidDataException("Invalid literal/length code.");
                    }

                    if (len > bc)
                    {
                        throw new EndOfInputException();
                    }

                    bb >>= len;
                    bc -= len;
                    int sym = e >> 4;
                    if (sym < 256)
                    {
                        ob[op++] = (byte)sym;
                        continue;
                    }

                    if (sym == 256)
                    {
                        // write the state back before changing state
                        outPos = op;
                        bitBuf = bb;
                        bitCnt = bc;
                        inPos = ip;
                        EndOfBlock();
                        return;
                    }

                    sym -= 257;
                    if (sym >= 29)
                    {
                        throw new InvalidDataException("Invalid length symbol.");
                    }

                    int eb = LenExtra[sym];
                    int length = LenBase[sym] + ((int)bb & ((1 << eb) - 1));
                    bb >>= eb;
                    bc -= eb;

                    int de = dt[(int)bb & dmask];
                    int dl = de & 15;
                    if (dl == 0)
                    {
                        throw new InvalidDataException("Invalid distance code.");
                    }

                    bb >>= dl;
                    bc -= dl;
                    int ds = de >> 4;
                    if (ds >= 30)
                    {
                        throw new InvalidDataException("Invalid distance symbol.");
                    }

                    int deb = DistExtra[ds];
                    int dist = DistBase[ds] + ((int)bb & ((1 << deb) - 1));
                    bb >>= deb;
                    bc -= deb;
                    if (bc < 0)
                    {
                        throw new EndOfInputException();
                    }

                    int src = op - dist;
                    if (src < vf)
                    {
                        throw new InvalidDataException("Invalid distance too far back.");
                    }

                    if (dist >= length && length > 16)
                    {
                        Buffer.BlockCopy(ob, src, ob, op, length);
                        op += length;
                    }
                    else
                    {
                        for (int i = 0; i < length; i++)
                        {
                            ob[op++] = ob[src++];
                        }
                    }
                }

                outPos = op;
                bitBuf = bb;
                bitCnt = bc;
                inPos = ip;
            }
            catch (EndOfInputException)
            {
                // symbols decoded so far are kept (op is the end of the last complete symbol only if
                // the failing symbol wrote nothing, which holds: literals and matches are written last)
                outPos = op;
                throw;
            }
        }
    }
}
