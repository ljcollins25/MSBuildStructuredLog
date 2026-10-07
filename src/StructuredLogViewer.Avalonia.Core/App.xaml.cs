using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace StructuredLogViewer.Avalonia
{
    public class App : Application
    {
        /// <summary>Set by the head (desktop or browser) before the app starts: runs after the XAML is loaded.</summary>
        public static Action<App> Initialized { get; set; }

        /// <summary>Set by a desktop head: creates the main window.</summary>
        public static Func<Window> CreateMainWindow { get; set; }

        /// <summary>Set by a single-view head (browser): creates the main view.</summary>
        public static Func<Control> CreateMainView { get; set; }

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
            UpdateTheme();
            Initialized?.Invoke(this);
        }

        public static void UpdateTheme()
        {
            Current.RequestedThemeVariant = SettingsService.UseDarkTheme
                ? ThemeVariant.Dark
                : ThemeVariant.Light;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && CreateMainWindow != null)
            {
                desktop.MainWindow = CreateMainWindow();
            }
            else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView && CreateMainView != null)
            {
                singleView.MainView = CreateMainView();
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
