using System.Windows;

namespace SimpleModManager;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var state = new PreferencesStore();
        if (e.Args.Length == 2 && e.Args[0] == "--resume" && int.TryParse(e.Args[1], out var parentId))
        {
            try
            {
                using var parent = System.Diagnostics.Process.GetProcessById(parentId);
                parent.WaitForExit(10000);
            }
            catch (ArgumentException) { }
        }

        instance = new Mutex(false, @"Local\SimpleModManager");
        try { ownsInstance = instance.WaitOne(0); }
        catch (AbandonedMutexException) { ownsInstance = true; }
        if (!ownsInstance)
        {
            MessageBox.Show("Simple Mod Manager is already running.", "Simple Mod Manager");
            Shutdown();
            return;
        }

        MainWindow = new MainWindow(state);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsInstance)
            instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
