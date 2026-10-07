using System;
using System.IO;
using System.IO.Compression;
using Microsoft.Build.Logging.StructuredLogger.Paging;
using Xunit;

namespace StructuredLogger.Paging.Tests
{
    public class InflateEngineTests
    {
        private static byte[] ReadAll(Stream stream, int bufferSize = 4096)
        {
            var ms = new MemoryStream();
            var buffer = new byte[bufferSize];
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ms.Write(buffer, 0, n);
            }

            return ms.ToArray();
        }

        [Theory]
        [InlineData(0, CompressionLevel.Optimal)]
        [InlineData(1, CompressionLevel.Optimal)]
        [InlineData(1000, CompressionLevel.Optimal)]
        [InlineData(300_000, CompressionLevel.Fastest)]
        [InlineData(300_000, CompressionLevel.Optimal)]
        [InlineData(300_000, CompressionLevel.SmallestSize)]
        [InlineData(300_000, CompressionLevel.NoCompression)]
        [InlineData(5_000_000, CompressionLevel.Optimal)]
        public void SequentialDecompressionMatchesGZipStream(int size, CompressionLevel level)
        {
            var content = TestData.MakeContent(size);
            var gzip = TestData.Gzip(content, level);

            using var stream = new GzipIndexingStream(new MemoryStream(gzip), spacingBytes: 4096);
            var actual = ReadAll(stream, bufferSize: 7919);

            Assert.Equal(content, actual);
            Assert.NotNull(stream.Index);
            Assert.Equal(content.Length, stream.Index.UncompressedLength);
            Assert.Equal(gzip.Length, stream.Index.CompressedLength);
            Assert.False(stream.IsTruncated);
        }

        [Fact]
        public void ReadsInTinyAndHugeSlicesGiveTheSameResult()
        {
            var content = TestData.MakeContent(700_000, seed: 5);
            var gzip = TestData.Gzip(content);

            foreach (int bufferSize in new[] { 1, 3, 100_000, 1 << 20 })
            {
                using var stream = new GzipIndexingStream(new MemoryStream(gzip));
                if (bufferSize == 1)
                {
                    // one byte at a time for the first part only, it is slow
                    var one = new byte[1];
                    for (int i = 0; i < 20000; i++)
                    {
                        Assert.Equal(1, stream.Read(one, 0, 1));
                        Assert.Equal(content[i], one[0]);
                    }
                }
                else
                {
                    Assert.Equal(content, ReadAll(stream, bufferSize));
                }
            }
        }

        [Fact]
        public void ConcatenatedGzipMembersAreDecodedAsOneStream()
        {
            var a = TestData.MakeContent(200_000, 1);
            var b = TestData.MakeContent(300_000, 2);
            var c = Array.Empty<byte>();
            var all = new MemoryStream();
            foreach (var part in new[] { a, b, c, a })
            {
                var gz = TestData.Gzip(part);
                all.Write(gz, 0, gz.Length);
            }

            var expected = new MemoryStream();
            foreach (var part in new[] { a, b, c, a })
            {
                expected.Write(part, 0, part.Length);
            }

            using var stream = new GzipIndexingStream(new MemoryStream(all.ToArray()), 10_000);
            Assert.Equal(expected.ToArray(), ReadAll(stream));
            Assert.NotNull(stream.Index);

            // random access across the member boundaries
            using var reader = new GzipRandomAccessReader(new MemoryPositionedSource(all.ToArray()), stream.Index);
            var exp = expected.ToArray();
            foreach (long boundary in new long[] { a.Length, a.Length + b.Length, a.Length + b.Length + a.Length / 2 })
            {
                var got = reader.ReadBytes(boundary - 50, 100);
                Assert.Equal(new ArraySegment<byte>(exp, (int)boundary - 50, 100), new ArraySegment<byte>(got));
            }
        }

        [Fact]
        public void TruncatedInputIsReportedAndEverythingBeforeTheCutIsReturned()
        {
            var content = TestData.MakeContent(2_000_000, 3);
            var gzip = TestData.Gzip(content);
            var cut = new byte[gzip.Length * 2 / 3];
            Array.Copy(gzip, cut, cut.Length);

            using var stream = new GzipIndexingStream(new MemoryStream(cut));
            var actual = ReadAll(stream);

            Assert.True(stream.IsTruncated);
            Assert.Null(stream.Index);
            Assert.True(actual.Length > content.Length / 2);
            Assert.Equal(new ArraySegment<byte>(content, 0, actual.Length), new ArraySegment<byte>(actual));
        }

        [Fact]
        public void CorruptDataThrowsInvalidDataException()
        {
            var gzip = TestData.Gzip(TestData.MakeContent(100_000, 4));
            gzip[gzip.Length / 2] ^= 0xFF;
            gzip[gzip.Length / 2 + 1] ^= 0x55;

            using var stream = new GzipIndexingStream(new MemoryStream(gzip));
            Assert.Throws<InvalidDataException>(() => ReadAll(stream));
        }

        [Fact]
        public void NotGzipThrows()
        {
            using var stream = new GzipIndexingStream(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }));
            Assert.Throws<InvalidDataException>(() => ReadAll(stream));
        }

        [Fact]
        public void SeekPointsHaveTheRequestedSpacing()
        {
            var gzip = TestData.Gzip(TestData.MakeContent(3_000_000, 6));
            using var stream = new GzipIndexingStream(new MemoryStream(gzip), spacingBytes: 50_000);
            ReadAll(stream);
            var points = stream.Index.Points;

            Assert.Equal(0, points[0].OutputOffset);
            Assert.True(points.Count >= gzip.Length / 60_000, "points: " + points.Count);
            for (int i = 1; i < points.Count; i++)
            {
                Assert.True(points[i].OutputOffset > points[i - 1].OutputOffset);
                Assert.True(points[i].InputBitOffset > points[i - 1].InputBitOffset);
                Assert.True(points[i].Window.Length <= 32768);
                Assert.Equal((int)Math.Min(32768, points[i].OutputOffset), points[i].Window.Length);
            }
        }
    }

    internal sealed class MemoryPositionedSource : IPositionedSource
    {
        private readonly byte[] bytes;

        public MemoryPositionedSource(byte[] bytes) => this.bytes = bytes;

        public long Length => bytes.Length;

        public string Identity => "memory:" + bytes.Length;

        public int Reads;

        public int Read(long position, byte[] buffer, int offset, int count)
        {
            Reads++;
            if (position >= bytes.Length)
            {
                return 0;
            }

            int n = (int)Math.Min(count, bytes.Length - position);
            Buffer.BlockCopy(bytes, (int)position, buffer, offset, n);
            return n;
        }

        public System.Threading.Tasks.Task<int> ReadAsync(long position, byte[] buffer, int offset, int count, System.Threading.CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.FromResult(Read(position, buffer, offset, count));

        public void Dispose()
        {
        }
    }
}
