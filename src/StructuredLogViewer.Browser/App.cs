using StructuredLogViewer.Avalonia;

namespace StructuredLogViewer.Browser
{
    internal static class BrowserApp
    {
        /// <summary>Plugs the browser's single view into the shared App.</summary>
        public static void Configure()
        {
            App.CreateMainView = () => new BrowserShell();

            // no processes, no OS shell: the shared UI hides Open in external editor, Show in Explorer, VS Code
            PlatformCapabilities.CanLaunchProcesses = false;
            PlatformCapabilities.SaveTextAsync = (name, text) =>
            {
                JsInterop.DownloadText(name, text);
                return System.Threading.Tasks.Task.CompletedTask;
            };
        }
    }
}
