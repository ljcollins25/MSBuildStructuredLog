using System;
using System.Threading.Tasks;

namespace StructuredLogViewer.Avalonia
{
    /// <summary>
    /// What the host can do. Desktop leaves the defaults; a head without them (the browser) overrides
    /// them at startup, and the shared UI hides the matching commands instead of failing.
    /// </summary>
    public static class PlatformCapabilities
    {
        /// <summary>Can start other programs and show files in the OS shell: Open in external editor, Show in Explorer, VS Code, MSBuild builds.</summary>
        public static bool CanLaunchProcesses { get; set; } = true;

        /// <summary>
        /// Saves text as a file. Null means use the StorageProvider save picker (desktop and browsers that support it);
        /// a head can plug in something else (the browser head triggers a download).
        /// </summary>
        public static Func<string, string, Task> SaveTextAsync { get; set; }
    }
}
