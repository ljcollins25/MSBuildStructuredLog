using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.Build.Logging.StructuredLogger;
using Xunit;

namespace StructuredLogger.Paging.Tests
{
    public class LazyArchiveTests
    {
        private static byte[] MakeZip(int count)
        {
            var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                for (int i = 0; i < count; i++)
                {
                    var entry = zip.CreateEntry($"C\\src\\file{i}.cs");
                    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    writer.Write("class C" + i + " {}");
                }
            }

            return memory.ToArray();
        }

        [Fact]
        public void NamesComeFromTheDirectoryWithoutReadingContent()
        {
            var files = Build.ReadSourceFiles(MakeZip(50));
            Assert.Equal(50, files.Count);
            Assert.All(files, f => Assert.False(f.IsLoaded));
            Assert.Equal("C:/src/file0.cs".Replace('/', '\\'), files[0].FullPath.Replace('/', '\\'));
        }

        [Fact]
        public void ContentIsReadOnDemandThroughTheResolver()
        {
            var files = Build.ReadSourceFiles(MakeZip(5));
            var resolver = new ArchiveFileResolver(files);
            Assert.Equal(5, resolver.Files.Count);
            Assert.Single(resolver.FindFileNames("file3"));
            var text = resolver.GetSourceFileText(files[3].FullPath);
            Assert.Equal("class C3 {}", text.Text);
            Assert.Equal(1, text.Lines.Count);
        }
    }
}
