using System.Windows;
using System.Windows.Threading;

namespace Maid;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);

        // Before anything reads the environment (MAID_DATA_DIR, WebView2's variables...).
        var (args, handedOver) = Elevation.TakeHandOver(e.Args);
        var store = new StateStore();
        store.Load();

        // The sessions get Maid's token, so admin consoles take an elevated Maid. When that cannot be
        // had, Maid runs without and the page says so.
        if (store.State.Settings.RunAsAdmin && !Elevation.IsElevated && !handedOver && Elevation.TryRestartElevated(args))
        {
            Shutdown();
            return;
        }
        ClaudeMdFile.BaseFile = System.IO.Path.Combine(store.DataDirectory, "agents-md.json");
        // Held while Maid runs: the installer (installer\Maid.iss, AppMutex) asks for Maid to be closed before it updates or
        // removes files a running Maid would still load.
        _running = new Mutex(false, RunningMutexName);
        new MainWindow(store).Show();
    }

    /// <summary>The name of the mutex a running Maid holds; installer\Maid.iss's AppMutex must match it.</summary>
    internal const string RunningMutexName = "Maid.Running";
    private static Mutex? _running;

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A failure in one message handler must not take every running session down with it.
        MessageBox.Show(e.Exception.ToString(), "Maid — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
