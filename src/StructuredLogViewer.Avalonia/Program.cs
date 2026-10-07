using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.Avalonia
{
    class Program
    {
        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();

        [STAThread]
        public static int Main(string[] args)
        {
            ExceptionHandler.Initialize();

            AppDomain.MonitoringIsEnabled = true;

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    ErrorReporting.ReportException(e.ExceptionObject as Exception);
                }
                catch
                {
                }
            };

            Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;

            App.Initialized = app =>
            {
                MacOsEnvironmentExporter.InheritUserPath();
#if DEBUG
                app.AttachDeveloperTools();
#endif
                NativeDock.SetMenu(app, new NativeMenu
                {
                    new NativeMenuItem("New Instance") { Command = MacOsAppBundleRunner.NewInstanceCommand }
                });
            };
            App.CreateMainWindow = () => new MainWindow();

            var app = BuildAvaloniaApp();
            int result = app.StartWithClassicDesktopLifetime(args);

            // if there's a Save As operation in progress, wait for it to finish
            var mainWindow = (app.Instance?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow;
            mainWindow?.InProgressTask.Wait();

            return result;
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            ErrorReporting.ReportException(e.Exception);
            DialogService.ShowMessageBox(
                    "Unexpected exception. Sorry about that.\r\nPlease Ctrl+C to copy this text and file an issue at https://github.com/KirillOsenkov/MSBuildStructuredLog/issues/new\r\n\r\n" + e.Exception.ToString());
            e.Handled = true;
        }
    }
}
