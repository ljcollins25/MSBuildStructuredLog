using System;
using System.Collections.Generic;
using System.IO;

namespace StructuredLogViewer.Core
{
    /// <summary>
    /// Where SettingsService keeps its small text files (settings, recent items, custom arguments).
    /// The default is the file system; a head without one (the browser) plugs in its own.
    /// Names are full paths as built by SettingsService; a store may use just the file name as the key.
    /// </summary>
    public interface ISettingsStore
    {
        bool Exists(string path);
        string[] ReadAllLines(string path);
        void WriteAllLines(string path, IEnumerable<string> lines);
        void WriteAllText(string path, string text);

        /// <summary>Serializes access to one named file across processes (a no-op where there is a single one).</summary>
        IDisposable Lock(string name);
    }

    public sealed class FileSettingsStore : ISettingsStore
    {
        public bool Exists(string path) => File.Exists(path);

        public string[] ReadAllLines(string path) => File.ReadAllLines(path);

        public void WriteAllLines(string path, IEnumerable<string> lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, lines);
        }

        public void WriteAllText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        public IDisposable Lock(string name) => SingleGlobalInstance.Acquire(name);
    }
}
