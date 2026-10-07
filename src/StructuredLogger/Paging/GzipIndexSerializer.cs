using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Microsoft.Build.Logging.StructuredLogger.Paging
{
    /// <summary>
    /// Binary format of a saved <see cref="GzipIndex"/>: magic "BLIX", format version, the source identity it was
    /// built for, sizes, spacing, then per seek point the bit offset, output offset and the window.
    /// The windows (32 KB of mostly text each) are deflated, which shrinks them to about a third.
    /// </summary>
    public static class GzipIndexSerializer
    {
        private const uint Magic = 0x58494C42; // "BLIX"
        public const int FormatVersion = 1;

        public static void Write(Stream stream, GzipIndex index, string sourceIdentity)
        {
            using var writer = new BinaryWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(sourceIdentity ?? "");
            writer.Write(index.CompressedLength);
            writer.Write(index.UncompressedLength);
            writer.Write(index.SpacingBytes);
            writer.Write(index.Points.Count);
            foreach (var point in index.Points)
            {
                writer.Write(point.InputBitOffset);
                writer.Write(point.OutputOffset);
                byte[] packed;
                using (var memory = new MemoryStream())
                {
                    using (var deflate = new DeflateStream(memory, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        deflate.Write(point.Window, 0, point.Window.Length);
                    }

                    packed = memory.ToArray();
                }

                writer.Write(point.Window.Length);
                writer.Write(packed.Length);
                writer.Write(packed);
            }
        }

        /// <summary>Reads an index; returns null when the data is not a current index for <paramref name="sourceIdentity"/>.</summary>
        public static GzipIndex Read(Stream stream, string sourceIdentity)
        {
            try
            {
                using var reader = new BinaryReader(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
                if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion || reader.ReadString() != (sourceIdentity ?? ""))
                {
                    return null;
                }

                long compressed = reader.ReadInt64();
                long uncompressed = reader.ReadInt64();
                long spacing = reader.ReadInt64();
                int count = reader.ReadInt32();
                if (count <= 0 || count > 10_000_000)
                {
                    return null;
                }

                var points = new List<SeekPoint>(count);
                for (int i = 0; i < count; i++)
                {
                    long bit = reader.ReadInt64();
                    long output = reader.ReadInt64();
                    int windowLength = reader.ReadInt32();
                    int packedLength = reader.ReadInt32();
                    if (windowLength < 0 || windowLength > (1 << 15) || packedLength < 0 || packedLength > (1 << 16))
                    {
                        return null;
                    }

                    var packed = reader.ReadBytes(packedLength);
                    var window = new byte[windowLength];
                    using (var deflate = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress))
                    {
                        int total = 0;
                        while (total < windowLength)
                        {
                            int n = deflate.Read(window, total, windowLength - total);
                            if (n <= 0)
                            {
                                return null;
                            }

                            total += n;
                        }
                    }

                    points.Add(new SeekPoint(bit, output, window));
                }

                return new GzipIndex(points, compressed, uncompressed, spacing);
            }
            catch (Exception e) when (e is EndOfStreamException || e is IOException || e is InvalidDataException)
            {
                return null;
            }
        }
    }
}
