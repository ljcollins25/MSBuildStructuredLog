using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Microsoft.Build.Logging.StructuredLogger.Paging
{
    /// <summary>
    /// Keeps saved indexes in a folder, one file per source identity (path + length + timestamp for a file,
    /// URL + ETag + length for a server), so a log that was indexed once opens without the sequential pass.
    /// A changed file or resource has a different identity and is indexed again.
    /// </summary>
    public sealed class GzipIndexCache
    {
        private readonly string directory;

        public GzipIndexCache(string directory = null)
        {
            this.directory = directory ?? Path.Combine(Path.GetTempPath(), "StructuredLogViewer", "index");
        }

        public string Directory => directory;

        public string GetPath(IPositionedSource source)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(source.Identity));
            return Path.Combine(directory, BitConverter.ToString(hash, 0, 16).Replace("-", "") + ".blix");
        }

        /// <summary>The cached index for the source, or null when there is none (or it is damaged or stale).</summary>
        public GzipIndex TryLoad(IPositionedSource source)
        {
            var path = GetPath(source);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                using var stream = File.OpenRead(path);
                var index = GzipIndexSerializer.Read(stream, source.Identity);
                return index != null && index.CompressedLength == source.Length ? index : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        public void Save(IPositionedSource source, GzipIndex index)
        {
            System.IO.Directory.CreateDirectory(directory);
            var path = GetPath(source);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = File.Create(temp))
            {
                GzipIndexSerializer.Write(stream, index, source.Identity);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temp, path);
        }

        /// <summary>Loads the index from the cache, or builds it with one sequential pass over the source and saves it.</summary>
        public GzipIndex GetOrBuild(IPositionedSource source, long spacingBytes = GzipIndexingStream.DefaultSpacingBytes)
        {
            var index = TryLoad(source);
            if (index != null)
            {
                return index;
            }

            using (var stream = new GzipIndexingStream(new PositionedSourceStream(source), spacingBytes))
            {
                var buffer = new byte[1 << 16];
                while (stream.Read(buffer, 0, buffer.Length) > 0)
                {
                }

                index = stream.Index;
            }

            Save(source, index);
            return index;
        }
    }
}
