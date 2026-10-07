using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Build.Logging.StructuredLogger.Paging;
using Xunit;

namespace StructuredLogger.Paging.Tests
{
    public class RandomAccessTests
    {
        private static GzipIndex Build(byte[] gzip, long spacing)
        {
            using var stream = new GzipIndexingStream(new MemoryStream(gzip), spacing);
            var buffer = new byte[65536];
            while (stream.Read(buffer, 0, buffer.Length) > 0)
            {
            }

            return stream.Index;
        }

        [Theory]
        [InlineData(CompressionLevel.Optimal, 20_000, 5)]
        [InlineData(CompressionLevel.Fastest, 5_000, 1)] // the encoder may use very large blocks: few seek points are possible
        [InlineData(CompressionLevel.SmallestSize, 100_000, 5)]
        [InlineData(CompressionLevel.NoCompression, 30_000, 5)]
        public void RandomReadsAreByteIdenticalToTheSequentialOutput(CompressionLevel level, long spacing, int minPoints)
        {
            var content = TestData.MakeContent(6_000_000, seed: 11);
            var gzip = TestData.Gzip(content, level);
            var index = Build(gzip, spacing);
            Assert.True(index.Points.Count >= minPoints, "points: " + index.Points.Count);

            using var reader = new GzipRandomAccessReader(new MemoryPositionedSource(gzip), index, cacheBytes: 3_000_000);
            var random = new Random(42);
            for (int i = 0; i < 400; i++)
            {
                int length = random.Next(0, 3) == 0 ? random.Next(1, 300_000) : random.Next(1, 3000);
                long offset = random.Next(0, content.Length - length);
                var actual = reader.ReadBytes(offset, length);
                Assert.True(
                    new ArraySegment<byte>(content, (int)offset, length).SequenceEqual(actual),
                    $"mismatch at offset {offset} length {length}");
            }
        }

        [Fact]
        public void ReadsAcrossEverySeekPointAreIdentical()
        {
            var content = TestData.MakeContent(2_500_000, seed: 12);
            var gzip = TestData.Gzip(content);
            var index = Build(gzip, 10_000);

            using var reader = new GzipRandomAccessReader(new MemoryPositionedSource(gzip), index, cacheBytes: 0);
            foreach (var point in index.Points.Skip(1))
            {
                long start = Math.Max(0, point.OutputOffset - 7);
                var actual = reader.ReadBytes(start, 20);
                Assert.True(new ArraySegment<byte>(content, (int)start, 20).SequenceEqual(actual), "at " + point);

                // exactly at the point, and the last byte before it
                Assert.Equal(content[point.OutputOffset], reader.ReadBytes(point.OutputOffset, 1)[0]);
                Assert.Equal(content[point.OutputOffset - 1], reader.ReadBytes(point.OutputOffset - 1, 1)[0]);
            }
        }

        [Fact]
        public void ReadingTheWholeContentInBigPiecesIsIdentical()
        {
            var content = TestData.MakeContent(1_500_000, seed: 13);
            var gzip = TestData.Gzip(content);
            var index = Build(gzip, 30_000);

            using var reader = new GzipRandomAccessReader(new MemoryPositionedSource(gzip), index);
            var all = new byte[content.Length];
            Assert.Equal(content.Length, reader.Read(0, all, 0, all.Length));
            Assert.Equal(content, all);

            // reads at and past the end
            Assert.Equal(0, reader.Read(content.Length, all, 0, 10));
            Assert.Equal(5, reader.Read(content.Length - 5, all, 0, 10));
        }

        [Fact]
        public void TheCacheServesRepeatedReadsAndStaysWithinItsBudget()
        {
            var content = TestData.MakeContent(4_000_000, seed: 14);
            var gzip = TestData.Gzip(content);
            var index = Build(gzip, 50_000);

            var source = new MemoryPositionedSource(gzip);
            using var reader = new GzipRandomAccessReader(source, index, cacheBytes: 1_000_000);
            reader.ReadBytes(1_000_000, 100);
            long decoded = reader.SegmentsDecoded;
            Assert.Equal(1, decoded);
            reader.ReadBytes(1_000_050, 100);
            Assert.Equal(decoded, reader.SegmentsDecoded);
            Assert.True(reader.CacheHits >= 1);

            // sweep over the file: with the small budget old segments get evicted, results stay right
            for (long offset = 0; offset < content.Length - 100; offset += 150_000)
            {
                Assert.True(new ArraySegment<byte>(content, (int)offset, 100).SequenceEqual(reader.ReadBytes(offset, 100)));
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task AsyncReadsMatch()
        {
            var content = TestData.MakeContent(1_000_000, seed: 15);
            var gzip = TestData.Gzip(content);
            var index = Build(gzip, 20_000);
            using var reader = new GzipRandomAccessReader(new MemoryPositionedSource(gzip), index);

            var actual = await reader.ReadBytesAsync(123_456, 200_000);
            Assert.True(new ArraySegment<byte>(content, 123_456, 200_000).SequenceEqual(actual));
        }

        [Fact]
        public void TheFileSourceWorks()
        {
            var content = TestData.MakeContent(1_200_000, seed: 16);
            var gzip = TestData.Gzip(content);
            string path = TestData.WriteTemp(gzip);
            try
            {
                using var source = new FilePositionedSource(path);
                GzipIndex index;
                using (var stream = new GzipIndexingStream(new PositionedSourceStream(source), 40_000))
                {
                    var buffer = new byte[100_000];
                    while (stream.Read(buffer, 0, buffer.Length) > 0)
                    {
                    }

                    index = stream.Index;
                }

                using var reader = new GzipRandomAccessReader(source, index);
                Assert.True(new ArraySegment<byte>(content, 777_777, 4000).SequenceEqual(reader.ReadBytes(777_777, 4000)));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
