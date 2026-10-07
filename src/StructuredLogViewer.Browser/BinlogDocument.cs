using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.Browser
{
    /// <summary>
    /// A loaded log and the operations the viewer needs on it. No UI types, so it can move to a worker
    /// or be reused by an MCP host. Stream in, Build out, progress callback.
    /// </summary>
    public class BinlogDocument
    {
        public const int MaxSearchResults = 300;

        public BinlogDocument(Build build)
        {
            Build = build;
        }

        public Build Build { get; }

        public SourceFileResolver SourceFiles { get; private set; }

        public PreprocessedFileManager Preprocessed { get; private set; }

        public IReadOnlyList<ArchiveFile> Files => Build.SourceFiles ?? Array.Empty<ArchiveFile>();

        /// <summary>Reads a .binlog (gzip) stream. The progress ratio is 0..1 of the input stream.</summary>
        public static BinlogDocument Load(Stream stream, Action<double> progressCallback = null)
        {
            Progress progress = null;
            if (progressCallback != null)
            {
                progress = new Progress();
                progress.Updated += update => progressCallback(update.Ratio);
            }

            var build = BinaryLog.ReadBuild(stream, progress, projectImportsArchive: null);
            return new BinlogDocument(build).Initialize();
        }

        private BinlogDocument Initialize()
        {
            var files = Files;
            SourceFiles = new SourceFileResolver(files);
            Preprocessed = new PreprocessedFileManager(Build, SourceFiles);
            return this;
        }

        public IReadOnlyList<SearchResult> Search(string query, int maxResults = MaxSearchResults, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return Array.Empty<SearchResult>();
            }

            var search = new Search(new[] { Build }, Build.StringTable.Instances, maxResults, markResultsInTree: false);
            return search.FindNodes(query, cancellationToken).ToList();
        }

        /// <summary>The text shown in the details pane for a node.</summary>
        public string GetDetails(BaseNode node)
        {
            if (node == null)
            {
                return "";
            }

            var text = node.GetFullText();
            var path = (node as IHasSourceFile)?.SourceFilePath;
            if (!string.IsNullOrEmpty(path))
            {
                text += Environment.NewLine + Environment.NewLine + "Source: " + path;
            }

            return text;
        }

        /// <summary>The source file a node points to, when it is in the embedded archive.</summary>
        public string FindSourceFile(BaseNode node)
        {
            var path = (node as IHasSourceFile)?.SourceFilePath;
            return !string.IsNullOrEmpty(path) && SourceFiles.HasFile(path) ? path : null;
        }

        public string GetFileText(string path) => SourceFiles.GetSourceFileText(path)?.Text;

        public string GetPreprocessedText(IPreprocessable node)
        {
            if (node == null || !Preprocessed.CanPreprocess(node))
            {
                return null;
            }

            return Preprocessed.GetPreprocessedText(node.RootFilePath, PreprocessedFileManager.GetNodeEvaluationKey(node));
        }

        public IEnumerable<string> FindFiles(string substring)
        {
            if (string.IsNullOrEmpty(substring))
            {
                return Files.Select(f => f.FullPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
            }

            return Files
                .Where(f => f.FullPath.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(f => f.FullPath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Find in files: the lines of the archive files that contain the text.</summary>
        public IEnumerable<(string File, int Line, string Text)> FindInFiles(string text, int max = 500)
        {
            int count = 0;
            if (string.IsNullOrEmpty(text))
            {
                yield break;
            }

            foreach (var file in Files)
            {
                var source = SourceFiles.GetSourceFileText(file.FullPath);
                if (source == null)
                {
                    continue;
                }

                foreach (var position in source.Find(text))
                {
                    int line = source.GetLineNumberFromPosition(position);
                    yield return (file.FullPath, line + 1, source.GetLineText(line)?.Trim());
                    if (++count >= max)
                    {
                        yield break;
                    }
                }
            }
        }
    }
}
