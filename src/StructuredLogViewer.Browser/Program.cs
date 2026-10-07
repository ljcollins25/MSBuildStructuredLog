using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;
using Avalonia.Media;
using StructuredLogViewer.Avalonia;

namespace StructuredLogViewer.Browser
{
    internal sealed class Program
    {
        private static Task Main(string[] args)
        {
            BrowserApp.Configure();
            StructuredLogViewer.SettingsService.Store = new BrowserSettingsStore();
            BrowserTheme.Init();
            return BuildAvaloniaApp().StartBrowserAppAsync("out");
        }

        // Browsers have no system fonts to enumerate: use the Selawik font embedded in the shared library (Segoe UI metrics).
        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "avares://StructuredLogViewer.Avalonia.Core/Fonts#Selawik",
                FontFallbacks = new[] { new FontFallback { FontFamily = new FontFamily("avares://StructuredLogViewer.Avalonia.Core/Fonts#Selawik") } },
            });
    }
}
