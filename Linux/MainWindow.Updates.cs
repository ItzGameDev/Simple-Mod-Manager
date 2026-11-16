using System.IO;
using Avalonia.Controls;

namespace SimpleModManager.Linux;

public partial class MainWindow
{
    private AppRelease? appUpdate;

    private void RenderUpdateSettings()
    {
        PageBody.Children.Add(Text("App updates", false));
        PageBody.Children.Add(Text($"Installed version {AppUpdates.CurrentVersion}"));
        var automatic = new ToggleSwitch { Content = "Check for updates on startup", IsChecked = preferences.CheckForUpdates };
        PageBody.Children.Add(automatic);
        automatic.IsCheckedChanged += (_, _) =>
        {
            preferences = preferences with { CheckForUpdates = automatic.IsChecked == true };
            Save();
        };
        Button(PageBody, "Check for app update", async () => await CheckAppUpdate(true));
    }

    private async Task CheckAppUpdate(bool manual)
    {
        if (operation is not null || string.IsNullOrWhiteSpace(preferences.UpdateRepository))
        {
            if (manual) Status("Save the release repository first.");
            return;
        }
        await Read("Checking app updates…", token => AppUpdates.Check(preferences, token), release =>
        {
            appUpdate = release;
            if (release is null) { if (manual) Status("You have the latest published app version."); return; }
            Status($"Simple Mod Manager {release.Version} is available.");
            Button(PageBody, "Download and install app update", async () =>
            {
                string? archive = null;
                await Run("Download app update", async (progress, token) => archive = await AppUpdates.Download(release, progress, token));
                if (archive is null) return;
                if (!OperatingSystem.IsLinux()) { Status("Run the Linux package to apply Linux app updates."); return; }
                await Run("Prepare app update", (_, token) =>
                {
                    LinuxUpdater.Start(archive, token);
                    return Task.CompletedTask;
                });
                if (LinuxUpdater.Started) Close();
            }, true);
        });
    }
}

