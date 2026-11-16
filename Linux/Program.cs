using Avalonia;

namespace SimpleModManager.Linux;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!LinuxUpdater.Complete(args))
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
