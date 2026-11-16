using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SimpleModManager;

public partial class MainWindow
{
    private static bool CanQueue(Mod mod) => mod.HasPackage && !mod.Restricted && !mod.LocalFile &&
        (mod.Usage is not (UsageTag.Illegal or UsageTag.Restricted) || mod.Id == "walk-simulator");

    private void QueueDetail(object sender, RoutedEventArgs e)
    {
        if (game is not { } target || !loader.Installed)
        {
            HideDetails();
            Navigate(Page.Setup);
            return;
        }
        if (selectedMod is not { } mod || !CanQueue(mod) || operation is not null ||
            mod.SteamOnly && target.Store == Storefront.Meta) return;
        var queue = (preferences.Queue ?? []).ToList();
        if (!queue.Any(item => ForGame(item, target) && item.ModId == mod.Id && item.State != "Complete"))
            queue.Add(new(target.Path, mod.Id, "Queued"));
        preferences = preferences with { Queue = queue.ToArray() };
        SavePreferences();
        HideDetails();
        Status($"{mod.Name} is in the download queue.");
    }

    private static bool ForGame(DownloadRequest item, GameInstall target) =>
        item.GamePath.Equals(target.Path, StringComparison.OrdinalIgnoreCase);

    private void OpenQueue()
    {
        if (game is not { } target)
        {
            Navigate(Page.Setup);
            return;
        }
        var panel = NewToolsPanel("Download queue", "Queued packages install one at a time and use the catalog’s pinned hashes.");
        var rows = new ListBox { Height = 270 };
        panel.Body.Children.Add(rows);
        var detail = ToolText("");
        panel.Body.Children.Add(detail);
        rows.SelectionChanged += (_, _) =>
            detail.Text = ((rows.SelectedItem as ListBoxItem)?.Tag as DownloadRequest)?.Error ?? "";
        void Reload() => rows.ItemsSource = (preferences.Queue ?? []).Where(item => ForGame(item, target))
            .Select(item => new ListBoxItem
            {
                Tag = item,
                Content = $"{Catalog.Mods.FirstOrDefault(mod => mod.Id == item.ModId)?.Name ?? item.ModId} · {item.State}"
            }).ToArray();
        var controls = ToolsRow(panel.Body);
        ToolButton(controls, "Start queued downloads", async () =>
        {
            var jobs = (preferences.Queue ?? []).Where(item => ForGame(item, target) && item.State == "Queued").ToArray();
            if (jobs.Length == 0) { panel.Report("Queue a mod or retry a failed download first."); return; }
            if (!loader.Installed) { panel.Report("Set up BepInEx before starting downloads."); return; }
            CloseTool(panel);
            await RunOperation(async (report, token) =>
            {
                var failures = 0;
                for (var index = 0; index < jobs.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var job = jobs[index];
                    var position = index;
                    var title = Catalog.Mods.FirstOrDefault(mod => mod.Id == job.ModId)?.Name ?? job.ModId;
                    try
                    {
                        var mod = Catalog.Mods.FirstOrDefault(mod => mod.Id == job.ModId && CanQueue(mod)) ??
                            throw new InvalidOperationException("This package is no longer available.");
                        await InstallWithHistory(target, mod, update => report(new(
                            $"{position + 1}/{jobs.Length} · {update.Message}",
                            (position * 100 + update.Percent) / jobs.Length)), token);
                        UpdateQueue(job, "Complete", null);
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested && ex is IOException or InvalidDataException or UnauthorizedAccessException or
                        InvalidOperationException or System.Net.Http.HttpRequestException or OperationCanceledException)
                    {
                        failures++;
                        UpdateQueue(job, "Failed", $"{title}: {ex.Message}");
                    }
                }
                if (failures > 0)
                    throw new IOException($"{failures} queue item(s) failed. Open the queue to see details and retry.");
            }, "Download queue finished.", "Installing queued mods");
        }, true);
        ToolButton(controls, "Retry failed or cancelled", () =>
        {
            preferences = preferences with { Queue = (preferences.Queue ?? []).Select(item =>
                ForGame(item, target) && item.State is "Failed" or "Cancelled" ?
                    item with { State = "Queued", Error = null } : item).ToArray() };
            SavePreferences();
            Reload();
        });
        ToolButton(controls, "Cancel selected", () =>
        {
            if ((rows.SelectedItem as ListBoxItem)?.Tag is not DownloadRequest { State: "Queued" } item)
            { panel.Report("Select a queued download."); return; }
            UpdateQueue(item, "Cancelled", null);
            Reload();
        });
        ToolButton(controls, "Clear finished", () =>
        {
            preferences = preferences with { Queue = (preferences.Queue ?? []).Where(item =>
                !ForGame(item, target) || item.State == "Queued").ToArray() };
            SavePreferences();
            Reload();
        });
        Reload();
        ShowTool(panel);
    }

    private void UpdateQueue(DownloadRequest original, string state, string? error) => Dispatcher.Invoke(() =>
    {
        preferences = preferences with { Queue = (preferences.Queue ?? []).Select(item =>
            item == original ? item with { State = state, Error = error } : item).ToArray() };
        SavePreferences();
    });
}

