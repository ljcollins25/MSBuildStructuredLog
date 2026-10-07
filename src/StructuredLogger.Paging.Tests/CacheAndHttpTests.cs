using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Logging.StructuredLogger.Paging;
using Xunit;

namespace StructuredLogger.Paging.Tests
{
    public class CacheAndHttpTests
    {
        private static GzipIndex BuildIndex(IPositionedSource source, long spacing)
        {
            using var stream = new GzipIndexingStream(new PositionedSourceStream(source), spacing);
            var buffer = new byte[65536];
            while (stream.Read(buffer, 0, buffer.Length) > 0)
            {
            }

            return stream.Index;
        }

        [Fact]
        public void IndexRoundTripsThroughTheSerializer()
        {
            var content = TestData.MakeContent(3_000_000, seed: 5);
            var gzip = TestData.Gzip(content, CompressionLevel.Optimal);
            var index = BuildIndex(new MemoryPositionedSource(gzip), 20_000);
            Assert.True(index.Points.Count > 3);

            var memory = new MemoryStream();
            GzipIndexSerializer.Write(memory, index, "id-1");
            memory.Position = 0;
            var read = GzipIndexSerializer.Read(memory, "id-1");

            Assert.NotNull(read);
            Assert.Equal(index.Points.Count, read.Points.Count);
            Assert.Equal(index.UncompressedLength, read.UncompressedLength);
            Assert.Equal(index.CompressedLength, read.CompressedLength);
            for (int i = 0; i < index.Points.Count; i++)
            {
                Assert.Equal(index.Points[i].InputBitOffset, read.Points[i].InputBitOffset);
                Assert.Equal(index.Points[i].OutputOffset, read.Points[i].OutputOffset);
                Assert.Equal(index.Points[i].Window, read.Points[i].Window);
            }

            memory.Position = 0;
            Assert.Null(GzipIndexSerializer.Read(memory, "other-identity"));
            Assert.Null(GzipIndexSerializer.Read(new MemoryStream(new byte[] { 1, 2, 3 }), "id-1"));
        }

        [Fact]
        public void CacheIsKeyedByFileIdentityAndSavesTheIndexPass()
        {
            var dir = Path.Combine(Path.GetTempPath(), "paging-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var content = TestData.MakeContent(2_000_000, seed: 8);
                var gzip = TestData.Gzip(content, CompressionLevel.Optimal);
                var file = Path.Combine(dir, "log.gz");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(file, gzip);

                var cache = new GzipIndexCache(Path.Combine(dir, "cache"));
                using var source = new FilePositionedSource(file);
                Assert.Null(cache.TryLoad(source));
                var built = cache.GetOrBuild(source, 20_000);
                Assert.True(File.Exists(cache.GetPath(source)));
                var loaded = cache.TryLoad(source);
                Assert.NotNull(loaded);
                Assert.Equal(built.Points.Count, loaded.Points.Count);

                using (var reader = new GzipRandomAccessReader(source, loaded))
                {
                    var bytes = reader.ReadBytes(1_234_567, 5000);
                    Assert.Equal(content.AsSpan(1_234_567, 5000).ToArray(), bytes);
                }

                // a changed file has a different identity: the old index is not used for it
                source.Dispose();
                File.WriteAllBytes(file, TestData.Gzip(TestData.MakeContent(2_100_000, seed: 9), CompressionLevel.Optimal));
                using var changed = new FilePositionedSource(file);
                Assert.Null(cache.TryLoad(changed));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        /// <summary>A minimal HTTP/1.1 server with Range support, on a loopback port.</summary>
        private sealed class RangeServer : IDisposable
        {
            private readonly HttpListener listener = new HttpListener();
            private readonly byte[] data;
            public int Requests;
            public long BytesSent;
            public bool IgnoreRange;
            public string Prefix { get; }

            public RangeServer(byte[] data)
            {
                this.data = data;
                var port = new Random().Next(20000, 40000);
                while (true)
                {
                    try
                    {
                        Prefix = "http://127.0.0.1:" + port + "/";
                        listener.Prefixes.Clear();
                        listener.Prefixes.Add(Prefix);
                        listener.Start();
                        break;
                    }
                    catch (HttpListenerException)
                    {
                        port++;
                    }
                }

                _ = Task.Run(Loop);
            }

            private async Task Loop()
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync(); } catch { return; }
                    Interlocked.Increment(ref Requests);
                    var range = ctx.Request.Headers["Range"];
                    ctx.Response.AddHeader("ETag", "\"v1\"");
                    ctx.Response.AddHeader("Accept-Ranges", "bytes");
                    if (range == null || IgnoreRange || !range.StartsWith("bytes="))
                    {
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentLength64 = data.Length;
                        await ctx.Response.OutputStream.WriteAsync(data, 0, data.Length);
                    }
                    else
                    {
                        var parts = range.Substring(6).Split('-');
                        long from = long.Parse(parts[0]);
                        long to = parts[1].Length == 0 ? data.Length - 1 : Math.Min(long.Parse(parts[1]), data.Length - 1);
                        int n = (int)(to - from + 1);
                        ctx.Response.StatusCode = 206;
                        ctx.Response.AddHeader("Content-Range", "bytes " + from + "-" + to + "/" + data.Length);
                        ctx.Response.ContentLength64 = n;
                        await ctx.Response.OutputStream.WriteAsync(data, (int)from, n);
                        Interlocked.Add(ref BytesSent, n);
                    }

                    ctx.Response.Close();
                }
            }

            public void Dispose() => listener.Close();
        }

        [Fact]
        public async Task RandomReadsOverHttpRangeFetchOnlyWhatTheyNeed()
        {
            var content = TestData.MakeContent(8_000_000, seed: 21);
            var gzip = TestData.Gzip(content, CompressionLevel.Optimal);
            using var server = new RangeServer(gzip);
            using var client = new HttpClient();
            using var source = await HttpRangeSource.OpenAsync(client, new Uri(server.Prefix + "log.binlog"));
            Assert.Equal(gzip.Length, source.Length);
            Assert.Contains("v1", source.Identity);

            // index with one sequential pass over HTTP
            GzipIndex index;
            using (var stream = new GzipIndexingStream(new PositionedSourceStream(source), 30_000))
            {
                var buffer = new byte[65536];
                while (await stream.ReadAsync(buffer, 0, buffer.Length) > 0)
                {
                }

                index = stream.Index;
            }

            Assert.True(index.Points.Count > 5);
            long afterIndex = server.BytesSent;

            using var reader = new GzipRandomAccessReader(source, index, cacheBytes: 1_000_000);
            var random = new Random(3);
            for (int i = 0; i < 20; i++)
            {
                int length = random.Next(1, 4000);
                long offset = random.Next(0, content.Length - length);
                var bytes = await reader.ReadBytesAsync(offset, length);
                Assert.Equal(content.AsSpan((int)offset, length).ToArray(), bytes);
            }

            long fetched = server.BytesSent - afterIndex;
            Assert.True(fetched < gzip.Length, $"fetched {fetched} of {gzip.Length} compressed bytes for 20 small reads");
            Assert.True(source.RequestCount > 1);
        }

        [Fact]
        public async Task ServerThatIgnoresRangeIsReported()
        {
            using var server = new RangeServer(new byte[1000]) { IgnoreRange = true };
            using var client = new HttpClient();
            await Assert.ThrowsAsync<NotSupportedException>(() => HttpRangeSource.OpenAsync(client, new Uri(server.Prefix + "x")));
        }
    }
}
