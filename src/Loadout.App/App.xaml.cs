using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Loadout.Core;
using Loadout.Persistence;

namespace Loadout.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        if (!SqliteLoads())
        {
            // Not StartupUri = null: the setter throws, and the main window is not opened once shutdown has begun.
            Shutdown(1);
            return;
        }

        base.OnStartup(e);
    }

    private static bool SqliteLoads()
    {
        try
        {
            _ = SqliteEngine.Version();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorLog.Write("SQLite", ex);

            // No main window yet: the notice is the only window, and closing it must not end the app with code 0.
            Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            QuestionDialog.ShowNotice(
                null,
                new Question(
                    "Loadout can't start",
                    "It needs the SQLite library that Windows includes (winsqlite3.dll), and it could not be loaded. Details are in "
                        + ErrorLog.FileName + ".",
                    "_Open error log folder",
                    "_Close",
                    QuestionKind.Error),
                () => Process.Start("explorer.exe", "/select,\"" + ErrorLog.PathOnDisk + "\""));
            return false;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the window open, write the details to the error file, and show one line in Logs.
        e.Handled = true;
        ErrorLog.Write("UI thread", e.Exception);
        if (MainWindow?.DataContext is MainViewModel model)
        {
            model.ReportUnexpected(e.Exception);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ErrorLog.Write("Background task", e.Exception);
        e.SetObserved();
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            ErrorLog.Write("Process ending", exception);
        }
    }
}
