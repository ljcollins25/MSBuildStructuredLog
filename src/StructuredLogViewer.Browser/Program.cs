using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;

namespace StructuredLogViewer.Browser
{
    internal sealed class Program
    {
        private static Task Main(string[] args) => BuildAvaloniaApp().StartBrowserAppAsync("out");

        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>();
    }
}
