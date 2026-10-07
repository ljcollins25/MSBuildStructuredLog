using StructuredLogViewer.Avalonia;

namespace StructuredLogViewer.Browser
{
    /// <summary>
    /// Dark theme in the browser: follows prefers-color-scheme until the user has chosen one
    /// (ticking Dark Theme saves it; SettingsService.UseDarkThemeChosen), after which the saved choice wins.
    /// </summary>
    internal static class BrowserTheme
    {
        public static void Init() => SettingsService.SetDefaultUseDarkTheme(JsInterop.PrefersDark());

        /// <summary>The OS / browser scheme changed.</summary>
        public static void SchemeChanged(bool dark)
        {
            if (!SettingsService.UseDarkThemeChosen)
            {
                SettingsService.SetDefaultUseDarkTheme(dark);
                App.UpdateTheme();
            }
        }
    }
}
