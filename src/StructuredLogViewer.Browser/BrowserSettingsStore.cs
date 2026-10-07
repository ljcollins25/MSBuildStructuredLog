using System;
using System.Collections.Generic;
using StructuredLogViewer.Core;

namespace StructuredLogViewer.Browser
{
    /// <summary>
    /// Keeps the viewer's settings files in the page's localStorage (key = "binlog:" + file name), so
    /// settings, recent items and custom arguments survive reloads. There is a single thread, so no locking.
    /// </summary>
    public sealed class BrowserSettingsStore : ISettingsStore
    {
        private const string Prefix = "binlog:";

        private static string Key(string path) => Prefix + System.IO.Path.GetFileName(path);

        public bool Exists(string path) => JsInterop.StorageGet(Key(path)) != null;

        public string[] ReadAllLines(string path)
        {
            string text = JsInterop.StorageGet(Key(path)) ?? "";
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            // like File.ReadAllLines: a trailing newline does not add an empty last line
            return lines.Length > 0 && lines[lines.Length - 1].Length == 0 ? lines[..^1] : lines;
        }

        public void WriteAllLines(string path, IEnumerable<string> lines) =>
            JsInterop.StorageSet(Key(path), string.Join("\n", lines) + "\n");

        public void WriteAllText(string path, string text) => JsInterop.StorageSet(Key(path), text);

        public IDisposable Lock(string name) => NoLock.Instance;

        private sealed class NoLock : IDisposable
        {
            public static readonly NoLock Instance = new NoLock();
            public void Dispose() { }
        }
    }
}
