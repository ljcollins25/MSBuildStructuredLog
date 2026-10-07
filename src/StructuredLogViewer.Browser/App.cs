using StructuredLogViewer.Avalonia;

namespace StructuredLogViewer.Browser
{
    internal static class BrowserApp
    {
        /// <summary>Plugs the browser's single view into the shared App.</summary>
        public static void Configure()
        {
            App.CreateMainView = () => new BrowserShell();
        }
    }
}
