using System.Configuration;
using System.Windows;
using System.Windows.Threading;
using Promptarium.Services;

namespace Promptarium;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppLogger.Info("Promptarium を起動しました。");
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("UIスレッドの未処理例外", e.Exception);
        System.Windows.MessageBox.Show($"予期しないエラーを診断ログへ記録しました。\n{AppPaths.DiagnosticsPath}", "Promptarium", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception) AppLogger.Error("アプリケーションの未処理例外", exception);
        else AppLogger.Info($"アプリケーションの未処理例外: {e.ExceptionObject}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Error("未監視タスク例外", e.Exception);
        e.SetObserved();
    }
}
