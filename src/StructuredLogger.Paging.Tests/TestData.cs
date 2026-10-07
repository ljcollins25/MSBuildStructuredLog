using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace StructuredLogger.Paging.Tests
{
    internal static class TestData
    {
        /// <summary>Deterministic data that is partly repetitive text (long matches, short matches) and partly random bytes.</summary>
        public static byte[] MakeContent(int size, int seed = 1)
        {
            var random = new Random(seed);
            var result = new byte[size];
            var words = new[] { "Project", "Target", "CoreCompile", "Csc", "Item", "Metadata", "Property", "Microsoft.Build", "src/libraries/System.Private.CoreLib/", ".csproj", "\r\n", "  ", "=" };
            int pos = 0;
            while (pos < size)
            {
                int kind = random.Next(10);
                if (kind < 6)
                {
                    var sb = new StringBuilder();
                    int count = random.Next(1, 40);
                    for (int i = 0; i < count; i++)
                    {
                        sb.Append(words[random.Next(words.Length)]).Append(random.Next(100));
                    }

                    var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                    int n = Math.Min(bytes.Length, size - pos);
                    Buffer.BlockCopy(bytes, 0, result, pos, n);
                    pos += n;
                }
                else if (kind < 8)
                {
                    // random (incompressible) bytes
                    int n = Math.Min(random.Next(1, 3000), size - pos);
                    var chunk = new byte[n];
                    random.NextBytes(chunk);
                    Buffer.BlockCopy(chunk, 0, result, pos, n);
                    pos += n;
                }
                else if (kind < 9 && pos > 100)
                {
                    // a copy of earlier data, from anywhere in the last 40 KB, to make long distance matches
                    int from = Math.Max(0, pos - random.Next(1, 40000));
                    int n = Math.Min(random.Next(10, 600), size - pos);
                    n = Math.Min(n, pos - from);
                    Buffer.BlockCopy(result, from, result, pos, n);
                    pos += n;
                }
                else
                {
                    // a run of a single byte (overlapping match with distance 1)
                    int n = Math.Min(random.Next(1, 700), size - pos);
                    byte b = (byte)random.Next(256);
                    for (int i = 0; i < n; i++)
                    {
                        result[pos++] = b;
                    }
                }
            }

            return result;
        }

        public static byte[] Gzip(byte[] content, CompressionLevel level = CompressionLevel.Optimal)
        {
            var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, level, leaveOpen: true))
            {
                gz.Write(content, 0, content.Length);
            }

            return ms.ToArray();
        }

        public static string WriteTemp(byte[] bytes, string extension = ".gz")
        {
            string path = Path.Combine(Path.GetTempPath(), "paged-test-" + Guid.NewGuid().ToString("N") + extension);
            File.WriteAllBytes(path, bytes);
            return path;
        }
    }
}
