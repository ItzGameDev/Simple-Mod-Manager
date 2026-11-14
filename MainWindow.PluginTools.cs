using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SimpleModManager;

public partial class MainWindow
{
    private void OpenInventory()
    {
        if (game is not { } target) return;
        var panel = NewToolsPanel("Plugin inventory",
            "Inspect plugin IDs, versions, and duplicates. Select files to enable or disable them.");
        var controls = ToolsRow(panel.Body);
        var search = ToolInput(controls, "Search plugin names, IDs, or filenames");
        search.ToolTip = "Search names, IDs, or filenames";
        var filter = new ComboBox { MinWidth = 140, Height = 34, Margin = new Thickness(0, 0, 7, 7),
            ItemsSource = new[] { "All plugins", "Enabled", "Disabled", "Local DLLs", "Duplicate IDs", "Read errors" },
            SelectedIndex = 0 };
        AutomationProperties.SetName(filter, "Plugin status filter");
        controls.Children.Add(filter);
        var rows = new ListBox { Height = 240, SelectionMode = SelectionMode.Extended };
        panel.Body.Children.Add(rows);
        var details = ToolText("");
        panel.Body.Children.Add(details);
        PluginInspection[] inventory = [];
        HashSet<string> duplicates = new(StringComparer.OrdinalIgnoreCase);

        void Draw()
        {
            var query = search.Text.Trim();
            rows.Items.Clear();
            foreach (var item in inventory)
            {
                var duplicate = item.Identities.Any(identity => duplicates.Contains(identity.Id));
                var visible = filter.SelectedIndex switch
                {
                    1 => !item.Disabled, 2 => item.Disabled, 3 => item.CatalogId is null, 4 => duplicate,
                    5 => item.Error is not null, _ => true
                };
                if (!visible || !(item.File.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.Identities.Any(identity => identity.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || identity.Id.Contains(query, StringComparison.OrdinalIgnoreCase)))) continue;
                var title = item.Identities.FirstOrDefault()?.Name ?? Path.GetFileName(ModWorkspace.ActiveName(item.File));
                var version = item.Identities.FirstOrDefault()?.Version ?? item.AssemblyVersion ?? "Unknown version";
                var label = $"{title}  ·  {version}  ·  {(item.Disabled ? "Disabled" : "Enabled")}";
                if (item.CatalogId is null) label += "  ·  Local DLL";
                if (duplicate) label += "  ·  Duplicate plugin ID";
                if (item.Error is not null) label += "  ·  Read error";
                rows.Items.Add(new ListBoxItem { Tag = item, Content = ToolText(label + "\n" + item.File) });
            }
            details.Text = "";
            var unreadable = inventory.Count(item => item.Error is not null);
            panel.Report($"{rows.Items.Count} of {inventory.Length} DLLs · {duplicates.Count} duplicate IDs" + (unreadable == 0 ? "" : $" · {unreadable} read errors"));
        }

        void SetInventory(PluginInspection[] items)
        {
            inventory = items;
            duplicates = inventory.SelectMany(item => item.Identities.Select(identity => (item.File, identity.Id)))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Select(item => item.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            SetMetadataCache(target, items);
            Draw();
        }

        Task Reload() => ReadTool(panel, "Reading plugin metadata…",
            token => Task.FromResult(ModWorkspace.Inventory(target, token)), SetInventory);

        search.TextChanged += (_, _) => Draw();
        filter.SelectionChanged += (_, _) => Draw();
        rows.SelectionChanged += (_, _) =>
        {
            if ((rows.SelectedItem as ListBoxItem)?.Tag is not PluginInspection item) return;
            var lines = new List<string>();
            lines.AddRange(item.Identities.Select(identity => $"{identity.Id} · {identity.Version}"));
            if (item.Error is { } message) lines.Add(message);
            details.Text = string.Join("\n", lines);
        };

        var actions = ToolsRow(panel.Body);
        ToolButton(actions, "Refresh inventory", async () => await Reload());
        ToolButton(actions, "Enable selected", async () => await ApplySelection(true));
        ToolButton(actions, "Disable selected", async () => await ApplySelection(false));

        async Task ApplySelection(bool enable)
        {
            var selected = rows.SelectedItems.Cast<ListBoxItem>().Select(row => (PluginInspection)row.Tag).ToArray();
            if (selected.Length == 0) { panel.Report("Select at least one plugin."); return; }
            var desired = selected.Select(item => ModWorkspace.ActiveName(item.File)).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(file => file, _ => enable, StringComparer.OrdinalIgnoreCase);
            await PreviewToggles(panel, target, desired, enable ? "Bulk enable" : "Bulk disable");
        }

        panel.OnOpen = Reload;
        ShowTool(panel);
    }

    private async Task PreviewToggles(ToolsPanel panel, GameInstall target, Dictionary<string, bool> desired, string action)
    {
        FileChange[] changes = [];
        await ReadTool(panel, "Checking changes…", async token =>
        {
            var scan = await PluginInspector.Scan(target, false, token);
            foreach (var item in scan.Plugins)
                if (item.Disabled && desired.GetValueOrDefault(ModWorkspace.ActiveName(item.File)) && item.Indicators.Count > 0)
                    throw new InvalidOperationException($"Resolve the indicator match for {item.File} before enabling it.");
            return ModWorkspace.PlanToggles(target, scan.Plugins, desired, token);
        }, result => changes = result);
        if (!toolPages.TryPeek(out var current) || !ReferenceEquals(current, panel)) return;
        if (changes.Length == 0)
        {
            if (panel.Status.Text.Length == 0) panel.Report("No plugin files need to change.");
            return;
        }
        var preview = NewToolsPanel(action, $"{changes.Length} plugin files will change.");
        foreach (var change in changes)
            preview.Body.Children.Add(ToolText($"{(change.After.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? "Disable" : "Enable")} · {change.Before}"));
        ToolButton(preview.Body, "Apply changes", async () =>
        {
            CloseTool(preview);
            CloseTool(panel);
            await RunOperation((_, token) =>
            {
                ModWorkspace.MovePlugins(target, changes, token);
                RecordHistory(target, action, changes);
                return Task.CompletedTask;
            }, $"{changes.Length} plugin states updated.");
        }, true);
        ShowTool(preview);
    }

    private void ScanForIsraelAuth()
    {
        if (game is not { } target) return;
        var panel = NewToolsPanel("Israel Auth scan", "Checks enabled and disabled plugin DLLs in your selected game folder.");
        var result = new StackPanel();
        panel.Body.Children.Add(result);
        Task ScanPlugins()
        {
            result.Children.Clear();
            return ReadTool(panel, "Scanning plugins…", token => PluginInspector.Scan(target, false, token), scan =>
            {
                var matches = scan.Plugins.Where(plugin => plugin.Indicators.Count > 0).ToArray();
                var errors = scan.Plugins.Where(plugin => plugin.Error is not null).ToArray();
                Section(result, matches.Length == 0
                    ? scan.Plugins.Length == 0 ? "No plugin DLLs found" : "No Israel Auth indicators found"
                    : $"{matches.Length} {(matches.Length == 1 ? "plugin matches" : "plugins match")} Israel Auth indicators");
                foreach (var plugin in matches)
                    result.Children.Add(ToolText(plugin.File));
                if (matches.Length > 0)
                    result.Children.Add(ToolText("A match can also come from a plugin that detects or references Israel Auth."));
                panel.Report($"{scan.Plugins.Length - errors.Length} of {scan.Plugins.Length} DLLs scanned");
                if (errors.Length > 0)
                {
                    Section(result, "Files that could not be fully checked");
                    foreach (var plugin in errors)
                        result.Children.Add(ToolText($"{plugin.File} · {plugin.Error}"));
                }
            });
        }
        ToolButton(panel.Body, "Scan again", async () => await ScanPlugins());
        panel.OnOpen = ScanPlugins;
        ShowTool(panel);
    }
    private void OpenProfiles()
    {
        if (game is not { } target) return;
        var panel = NewToolsPanel("Mod profiles", "Save the enabled plugins for this game folder. New plugins stay as they are unless included in a profile.");
        var controls = ToolsRow(panel.Body);
        var profiles = new ComboBox { MinWidth = 160, Height = 34, Margin = new Thickness(0, 0, 7, 7), DisplayMemberPath = "Name" };
        controls.Children.Add(profiles);
        var name = ToolInput(controls, "Profile name", width: 170);
        var content = ToolText("");
        var actions = ToolsRow(panel.Body);
        panel.Body.Children.Add(content);

        bool SameGame(ModPreset item) => item.GamePath.Equals(target.Path, StringComparison.OrdinalIgnoreCase);
        void Reload(string? select = null)
        {
            profiles.ItemsSource = (preferences.Profiles ?? []).Where(SameGame).OrderBy(item => item.Name).ToArray();
            profiles.SelectedItem = profiles.Items.Cast<ModPreset>().FirstOrDefault(item => item.Name == select);
        }
        profiles.SelectionChanged += (_, _) =>
        {
            if (profiles.SelectedItem is not ModPreset item) { content.Text = ""; return; }
            name.Text = item.Name;
            content.Text = string.Join("\n", item.EnabledFiles.Order(StringComparer.OrdinalIgnoreCase));
        };
        ToolButton(actions, "Save current state", async () =>
        {
            var key = name.Text.Trim();
            if (key.Length is < 1 or > 48) { panel.Report("Enter a profile name up to 48 characters."); return; }
            await ReadTool(panel, "Reading enabled plugins…", token => Task.FromResult(ModWorkspace.Inventory(target, token)), items =>
            {
                var profilesList = (preferences.Profiles ?? []).Where(item => !SameGame(item) || item.Name != key).ToList();
                profilesList.Add(new(key, target.Path, items.Where(item => !item.Disabled).Select(item => item.File).ToArray(),
                    items.Select(item => ModWorkspace.ActiveName(item.File)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
                preferences = preferences with { Profiles = profilesList.ToArray() };
                SavePreferences();
                Reload(key);
                panel.Report($"Saved {key}.");
            });
        }, true);
        ToolButton(actions, "Preview profile", async () =>
        {
            if (profiles.SelectedItem is not ModPreset preset) { panel.Report("Select a saved profile."); return; }
            var enabled = preset.EnabledFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var desired = (preset.KnownFiles ?? preset.EnabledFiles).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(file => file, enabled.Contains, StringComparer.OrdinalIgnoreCase);
            await PreviewToggles(panel, target, desired, $"Apply profile {preset.Name}");
        });
        ToolButton(actions, "Delete profile", async () =>
        {
            if (profiles.SelectedItem is not ModPreset preset || !await Confirm(panel, $"Delete profile \"{preset.Name}\"?")) return;
            preferences = preferences with { Profiles = preferences.Profiles!.Where(item => item != preset).ToArray() };
            SavePreferences();
            Reload();
            name.Clear();
        });
        Reload();
        ShowTool(panel);
    }

    private Task ToggleWithHistory(GameInstall target, string file, bool enable)
    {
        var change = Installer.SetUnlistedPluginEnabled(target, file, enable);
        RecordHistory(target, $"{(enable ? "Enable" : "Disable")} {file}", [change]);
        return Task.CompletedTask;
    }

    private Task ToggleWithHistory(GameInstall target, Mod mod, bool enable)
    {
        var change = Installer.SetModEnabled(target, mod, enable);
        RecordHistory(target, $"{(enable ? "Enable" : "Disable")} {mod.Name}", [change]);
        return Task.CompletedTask;
    }
    private void RecordHistory(GameInstall target, string action, FileChange[] changes)
    {
        if (changes.Length == 0) return;
        Dispatcher.Invoke(() =>
        {
            var entries = new[] { new HistoryEntry(DateTimeOffset.Now, target.Path, action, changes) }
                .Concat(preferences.History ?? []).ToArray();
            preferences = preferences with { History = entries };
            SavePreferences();
        });
    }

    private async Task InstallWithHistory(GameInstall target, Mod mod, Action<InstallProgress> progress,
        CancellationToken token, string? localFile = null)
    {
        var changes = await Installer.InstallMod(target, mod, progress, token, localFile);
        RecordHistory(target, $"Install {mod.Name}", changes);
    }

    private Task RemoveWithHistory(GameInstall target, Mod mod, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var change = Installer.RemoveMod(target, mod);
        RecordHistory(target, $"Remove {mod.Name}", [change]);
        return Task.CompletedTask;
    }

    private void OpenHistory()
    {
        if (game is not { } target) return;
        var panel = NewToolsPanel("Install history", "Undo a recorded change if its files still match.");
        var rows = new ListBox { Height = 295, ItemsSource = (preferences.History ?? [])
            .Where(item => item.GamePath.Equals(target.Path, StringComparison.OrdinalIgnoreCase))
            .Select(item => new ListBoxItem { Tag = item, Content = $"{item.Time:g} · {item.Action}" +
                (item.Undone ? " · Undone" : $" · {item.Changes.Length} files") }).ToArray() };
        panel.Body.Children.Add(rows);
        ToolButton(panel.Body, "Undo selected change", async () =>
        {
            if ((rows.SelectedItem as ListBoxItem)?.Tag is not HistoryEntry { Undone: false } item)
            { panel.Report("Select a change that can be undone."); return; }
            if (!await Confirm(panel, $"Undo \"{item.Action}\"?")) return;
            CloseTool(panel);

            await RunOperation(async (_, token) =>
            {
                if (item.Changes.All(change => change.Before.Length == 0))
                    ModWorkspace.UndoInstall(target, item.Changes, token);
                else if (item.Action.StartsWith("Remove ", StringComparison.Ordinal))
                    ModWorkspace.UndoRemoval(target, item.Changes.Single());
                else
                {
                    var current = await PluginInspector.Scan(target, false, token);
                    var desired = item.Changes.ToDictionary(change => ModWorkspace.ActiveName(change.After),
                        change => !change.Before.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
                    if (current.Plugins.Any(plugin => plugin.Indicators.Count > 0 &&
                        desired.GetValueOrDefault(ModWorkspace.ActiveName(plugin.File))))
                        throw new InvalidOperationException("A DLL matched Israel Auth indicators.");
                    var inverse = ModWorkspace.PlanToggles(target, current.Plugins, desired, token);
                    if (inverse.Length != item.Changes.Length || inverse.Any(change => !item.Changes.Any(original =>
                        original.After == change.Before && original.Before == change.After && original.Hash == change.Hash)))
                        throw new IOException("The files changed since the original action.");
                    ModWorkspace.MovePlugins(target, inverse, token);
                }
                Dispatcher.Invoke(() =>
                {
                    preferences = preferences with { History = preferences.History!.Select(entry =>
                        entry == item ? entry with { Undone = true } : entry).ToArray() };
                    SavePreferences();
                });
            }, $"Undid {item.Action.ToLowerInvariant()}.");
        }, true);
        ShowTool(panel);
    }
}


