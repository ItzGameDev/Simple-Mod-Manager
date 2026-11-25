using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace SimpleModManager.Linux;

public partial class MainWindow
{
    private void RenderQueue()
    {
        Heading("Download queue");
        if (game is not { } target) { PageBody.Children.Add(Text("Choose your game in Game Setup first.")); return; }
        var jobs = (preferences.Queue ?? []).Where(item => item.GamePath == target.Path).ToArray();
        foreach (var job in jobs)
        {
            var row = Row(PageBody);
            row.Children.Add(Text($"{Catalog.Mods.FirstOrDefault(mod => mod.Id == job.ModId)?.Name ?? job.ModId} · {job.State}"));
            if (job.Error is { } message) PageBody.Children.Add(Text(message));
            if (job.State is "Failed" or "Cancelled")
                Button(row, "Retry", () => { UpdateQueue(job, "Queued"); Go("queue", RenderQueue); });
            if (job.State == "Queued")
                Button(row, "Cancel", () => { UpdateQueue(job, "Cancelled"); Go("queue", RenderQueue); });
        }
        Button(PageBody, "Start downloads", async () =>
        {
            var queued = jobs.Where(item => item.State == "Queued").ToArray();
            if (queued.Length == 0) { Status("No queued downloads."); return; }
            await Run("Download queue", async (report, token) =>
            {
                var failed = 0;
                foreach (var job in queued)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var mod = Catalog.Mods.FirstOrDefault(mod => mod.Id == job.ModId && Queueable(mod)) ??
                            throw new InvalidOperationException("This package is unavailable.");
                        await Installer.InstallLoader(target, report, token);
                        Record(target, "Install " + mod.Name, await access.InstallMod(target, mod, report, token));
                        Dispatcher.UIThread.Invoke(() => UpdateQueue(job, "Complete"));
                    }
                    catch (GuestDownloadLimitException) { throw; }
                    catch (Exception ex) when (!token.IsCancellationRequested &&
                        ex is IOException or InvalidDataException or System.Net.Http.HttpRequestException or InvalidOperationException or OperationCanceledException)
                    {
                        failed++;
                        Dispatcher.UIThread.Invoke(() => UpdateQueue(job, "Failed", ex.Message));
                    }
                }
                if (failed > 0) throw new IOException($"{failed} download(s) failed. Retry them from the queue.");
            });
            Go("queue", RenderQueue);
        }, true);
    }

    private void UpdateQueue(DownloadRequest original, string state, string? error = null)
    {
        preferences = preferences with { Queue = (preferences.Queue ?? []).Select(item =>
            item == original ? item with { State = state, Error = error } : item).ToArray() };
        Save();
    }

    private void RenderHistory()
    {
        Heading("Install history", "Undo a recorded action while its files still match.");
        if (game is not { } target) return;
        foreach (var item in (preferences.History ?? []).Where(item => item.GamePath == target.Path))
        {
            var row = Row(PageBody);
            row.Children.Add(Text($"{item.Time:g} · {item.Action}{(item.Undone ? " · Undone" : "")}"));
            if (item.Undone) continue;
            ConfirmAction(row, "Undo", "Undo this file change?", async () =>
            {
                await Run("Undo change", async (_, token) =>
                {
                    if (item.Changes.All(change => change.Before.Length == 0))
                        ModWorkspace.UndoInstall(target, item.Changes, token);
                    else if (item.Action.StartsWith("Remove ", StringComparison.Ordinal))
                        ModWorkspace.UndoRemoval(target, item.Changes.Single());
                    else
                    {
                        var scan = await PluginInspector.Scan(target, false, token);
                        var desired = item.Changes.ToDictionary(change => ModWorkspace.ActiveName(change.After),
                            change => !change.Before.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase));
                        if (scan.Plugins.Any(plugin => plugin.Indicators.Count > 0 &&
                            desired.GetValueOrDefault(ModWorkspace.ActiveName(plugin.File))))
                            throw new InvalidOperationException("A plugin matches Israel Auth indicators.");
                        var inverse = ModWorkspace.PlanToggles(target, scan.Plugins, desired, token);
                        if (inverse.Length != item.Changes.Length || inverse.Any(change => !item.Changes.Any(original =>
                            change.Before == original.After && change.After == original.Before && change.Hash == original.Hash)))
                            throw new IOException("The plugins have changed since this action.");
                        ModWorkspace.MovePlugins(target, inverse, token);
                    }
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        preferences = preferences with { History = preferences.History!.Select(entry => entry == item
                            ? entry with { Undone = true } : entry).ToArray() };
                        Save();
                    });
                });
                Go("history", RenderHistory);
            });
        }
    }

    private async void RenderConfigs()
    {
        Heading("Config files", "Back up or edit BepInEx configs while the game is closed.");
        if (game is not { } target) return;
        var actions = Row(PageBody);
        Button(actions, "Back up configs", async () =>
        {
            if (await SaveFile("Config backup", "BepInEx-configs.zip") is not { } path) return;
            await Run("Back up configs", (_, _) => { ModWorkspace.BackupConfigs(target, path); return Task.CompletedTask; });
        });
        Button(actions, "Restore configs", async () =>
        {
            if (await PickFile("Config ZIP", ["*.zip"]) is not { } path) return;
            ConfirmAction(PageBody, "Restore selected backup", "Replace matching config files from this backup?", async () =>
                await Run("Restore configs", (_, _) => { ModWorkspace.RestoreConfigs(target, path); return Task.CompletedTask; }));
        });
        await Read("Finding configs…", _ => Task.FromResult(ModWorkspace.Configs(target)), files =>
        {
            foreach (var file in files)
                Button(PageBody, file, () => Child("config", () => EditConfig(target, file), () => Go("configs", RenderConfigs)));
            if (files.Length == 0) PageBody.Children.Add(Text("No config files yet."));
        });
    }

    private async void EditConfig(GameInstall target, string file)
    {
        Heading(file);
        await Read("Reading config…", _ => Task.FromResult(ModWorkspace.ReadConfig(target, file)), original =>
        {
            var editor = new TextBox { Text = original, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
                MinHeight = 250, MaxHeight = 450, HorizontalAlignment = HorizontalAlignment.Stretch };
            ScrollViewer.SetVerticalScrollBarVisibility(editor, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(editor, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            PageBody.Children.Add(editor);
            var actions = Row(PageBody);
            Button(actions, "Save changes", async () =>
            {
                var edited = editor.Text ?? "";
                await Run("Save config", (_, _) =>
                {
                    ModWorkspace.WriteConfig(target, file, original, edited);
                    return Task.CompletedTask;
                });
                // Reload from disk so a failed save never becomes the editor's baseline.
                Go("config", () => EditConfig(target, file));
            }, true);
            ConfirmAction(actions, "Reset", "Back up this config and regenerate defaults on the next game launch?", async () =>
            {
                await Run("Reset config", (_, _) =>
                {
                    ModWorkspace.WriteConfig(target, file, original, null);
                    return Task.CompletedTask;
                });
                Go("configs", RenderConfigs);
            });
            PageBody.Children.Add(Text("Saving and resetting create an automatic backup in the app data folder."));
        });
    }

    private async void RenderLog()
    {
        Heading("BepInEx log");
        if (game is not { } target) return;
        await Read("Reading log…", async token =>
        {
            var file = Installer.CheckedPath(target.Path, Path.Combine("BepInEx", "LogOutput.log"));
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            input.Position = Math.Max(0, input.Length - 256 * 1024);
            using var reader = new StreamReader(input);
            if (input.Position != 0) await reader.ReadLineAsync(token);
            var buffer = new char[256 * 1024];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), token);
            return new string(buffer, 0, count);
        }, content =>
        {
            var search = Input(PageBody, "Filter log lines");
            var output = new TextBox { Text = content, IsReadOnly = true, AcceptsReturn = true, Height = 360 };
            ScrollViewer.SetVerticalScrollBarVisibility(output, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            PageBody.Children.Add(output);
            search.TextChanged += (_, _) => output.Text = string.Join("\n", content.Split('\n')
                .Where(line => line.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)));
            Button(PageBody, "Refresh log", () => Go("log", RenderLog));
        });
    }
}
