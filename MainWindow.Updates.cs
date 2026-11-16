using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SimpleModManager;

public partial class MainWindow
{
    private AppRelease? appUpdate;
    private bool checkingAppUpdate;
    private Button? updateInstallButton;

    private void LoadAppUpdateSettings()
    {
        AppUpdateSettings.Children.Add(new TextBlock { Text = "App updates", Style = (Style)FindResource("SectionHeading") });
        AppUpdateSettings.Children.Add(ToolText($"Installed version {AppUpdates.CurrentVersion}"));
        var automatic = new CheckBox { Content = "Check for app updates on startup", IsChecked = preferences.CheckForUpdates,
            Margin = new Thickness(0, 4, 0, 8) };
        AppUpdateSettings.Children.Add(automatic);
        var actions = ToolsRow(AppUpdateSettings);
        automatic.Click += (_, _) =>
        {
            preferences = preferences with { CheckForUpdates = automatic.IsChecked == true };
            SavePreferences();
        };
        ToolButton(actions, "Check for app update", async () => await CheckAppUpdate(manual: true));
        updateInstallButton = ToolButton(actions, "Download and update", async () => await InstallAppUpdate(), true);
        updateInstallButton.Visibility = Visibility.Collapsed;
    }

    private async Task CheckAppUpdate(bool manual)
    {
        if (checkingAppUpdate || operation is not null) return;
        if (string.IsNullOrWhiteSpace(preferences.UpdateRepository))
        {
            if (manual) Status("Save your GitHub repository in Settings to enable app updates.");
            return;
        }
        checkingAppUpdate = true;
        var settings = preferences;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var release = await AppUpdates.Check(settings, timeout.Token);
            if (preferences.UpdateRepository != settings.UpdateRepository || preferences.UpdateVersionUrl != settings.UpdateVersionUrl)
                return;
            appUpdate = release;
            if (updateInstallButton is not null)
                updateInstallButton.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
            AppUpdateButton.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
            if (release is not null) Status($"Simple Mod Manager {release.Version} is available. Choose Download update to install it.");
            else if (manual) Status("You have the latest published app version.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.Http.HttpRequestException
            or System.Text.Json.JsonException or OperationCanceledException or InvalidOperationException)
        {
            if (manual) Status("Could not check app updates. " + ex.Message, error: true);
        }
        finally { checkingAppUpdate = false; }
    }

    private async void DownloadAppUpdate(object sender, RoutedEventArgs e) => await InstallAppUpdate();

    private async Task InstallAppUpdate()
    {
        if (appUpdate is not { } release || operation is not null) return;
        string? file = null;
        await RunOperation(async (report, token) => file = await AppUpdates.Download(release, report, token),
            "App update verified. Starting setup.", "Downloading Simple Mod Manager");
        if (file is null) return;
        try
        {
            var start = new ProcessStartInfo(file) { UseShellExecute = true };
            start.ArgumentList.Add("/CLOSEAPPLICATIONS");
            Process.Start(start);
            Close();
        }
        catch (System.ComponentModel.Win32Exception ex) { Status("Could not open update setup. " + ex.Message, error: true); }
    }
}

