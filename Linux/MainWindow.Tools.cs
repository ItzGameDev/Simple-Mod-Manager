using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace SimpleModManager.Linux;

public partial class MainWindow
{
    private TextBlock? discordStatus;
    private Button? discordSignInButton;

    private static readonly Dictionary<string, (string Accent, string Tint)> Themes = new()
    {
        ["Simple"] = ("#DBF4A9", "#29382C"), ["Liquid glass"] = ("#EFEFEF", "#090909"),
        ["Slate"] = ("#F3D29D", "#313A43"), ["Midnight"] = ("#A4BBFF", "#252C5C"),
        ["Aurora"] = ("#96ECCD", "#283E62"), ["Ember"] = ("#FFAD84", "#54332C"),
        ["Glacier"] = ("#A4EBFF", "#25475A"), ["Lavender"] = ("#DDC2FF", "#453657"),
        ["Rose"] = ("#FFC0D6", "#54314C"), ["Forest"] = ("#B4E5A5", "#304C35"),
        ["Ocean"] = ("#8CCEFF", "#264B67"), ["Copper"] = ("#F5C69B", "#563C34"),
        ["Violet"] = ("#C5A1FF", "#3B285D"), ["Teal"] = ("#80DDD9", "#23514E"),
        ["Crimson"] = ("#FFA9B3", "#582934"), ["Mono"] = ("#E0E5ED", "#3B424A")
    };

    private void ApplyTheme()
    {
        var colors = preferences.Theme == "Custom" && preferences.CustomTheme is { } custom
            ? (custom.Accent, custom.Tint) : Themes.GetValueOrDefault(preferences.Theme, Themes["Simple"]);
        try
        {
            var tint = Color.Parse(colors.Item2);
            Resources["Accent"] = Brush.Parse(colors.Item1);
            Resources["Selection"] = new SolidColorBrush(tint);
            Resources["Rail"] = Brush.Parse(preferences.Theme == "Liquid glass" ? "#090909" : "#171B22");
            backgroundGradient = new LinearGradientBrush
            {
                GradientStops = new() { new(Color.Parse("#101318"), 0), new(tint, 1) },
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative)
            };
            Resources["Backdrop"] = backgroundGradient;
        }
        catch (FormatException)
        {
            preferences = preferences with { Theme = "Simple", CustomTheme = null };
            ApplyTheme();
        }
    }

    private void RenderSetup()
    {
        Heading("Game Setup", "Choose the Windows Steam installation used by Proton.");
        var path = Input(PageBody, "Gorilla Tag installation folder", game?.Path ?? "");
        var controls = Row(PageBody);
        Button(controls, "Use this folder", () => SelectGame(path.Text ?? ""), true);
        Button(controls, "Browse", async () =>
        {
            var selected = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                { Title = "Choose Gorilla Tag", AllowMultiple = false });
            if (selected.FirstOrDefault()?.TryGetLocalPath() is { } folder) SelectGame(folder);
        });
        Button(controls, "Detect Steam libraries", async () => await Read("Finding installations…",
            token => Task.Run(() => GameDiscovery.Find(), token), found =>
            {
                foreach (var item in found)
                    Button(PageBody, item.Path, () => SelectGame(item.Path));
                if (found.Count == 0) Status("No Gorilla Tag installation found. Browse to its folder.");
            }));
        PageBody.Children.Add(Text("BepInEx under Proton needs a winhttp DLL override. In Steam → Gorilla Tag → Properties → Launch Options, add the setting below. Merge it with any existing options."));
        var command = Input(PageBody, "Proton launch options", "WINEDLLOVERRIDES=\"winhttp=n,b\" %command%");
        command.IsReadOnly = true;
        Button(PageBody, "Copy Proton launch setting", async () =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(command.Text);
            Status("Copied Proton launch setting.");
        });
        if (game is not { } target) return;
        PageBody.Children.Add(Text(LoaderLabel.Text ?? ""));
        var gameActions = Row(PageBody);
        Button(gameActions, "Set up BepInEx", async () =>
        {
            await Run("BepInEx setup", (report, token) => Installer.InstallLoader(target, report, token));
            Go("setup", RenderSetup, true);
        }, true);
        Button(gameActions, "Launch Steam game", () => OpenLink("steam://rungameid/1533390"));
        Button(gameActions, "Open game folder", () => OpenLink(target.Path));
        Button(gameActions, "Open plugins", () => OpenFolder(Path.Combine(target.Path, Installer.PluginsPath)));
        Button(gameActions, "Config files", () => ToolPage("configs", RenderConfigs));
        Button(gameActions, "BepInEx log", () => ToolPage("log", RenderLog));
    }

    private void SelectGame(string folder)
    {
        if (GameDiscovery.Open(folder) is not { } selected) { Status("This folder is missing the Windows x64 Gorilla Tag executable or game data."); return; }
        game = selected;
        preferences = preferences with { GamePath = selected.Path };
        Save();
        Refresh();
        Go("setup", RenderSetup, true);
    }

    private void OpenFolder(string path)
    {
        if (Directory.Exists(path)) OpenLink(path);
        else Status("This folder has not been created yet.");
    }

    private void RenderSettings()
    {
        Heading("Settings", "Choose a theme and animation preferences.");
        PageBody.Children.Add(Text("Discord account", muted: false));
        discordStatus = Text(AccountStatus);
        PageBody.Children.Add(discordStatus);
        discordSignInButton = Button(Row(PageBody), access.Discord.ButtonLabel, async () => await ChangeAccount());
        var themes = Row(PageBody);
        foreach (var (name, colors) in Themes)
        {
            var button = Button(themes, name, () =>
            {
                preferences = preferences with { Theme = name };
                Save();
                ApplyTheme();
            });
            button.Background = new LinearGradientBrush { GradientStops = new()
                { new(Color.Parse(colors.Tint), 0), new(Color.Parse(colors.Accent), 1) } };
        }
        var motion = new ToggleSwitch { Content = "Interface motion", IsChecked = preferences.Motion };
        motion.IsCheckedChanged += (_, _) =>
        {
            preferences = preferences with { Motion = motion.IsChecked == true };
            Save();
        };
        PageBody.Children.Add(motion);
        var compact = new ToggleSwitch { Content = "Compact mod cards", IsChecked = preferences.CompactLibrary };
        compact.IsCheckedChanged += (_, _) =>
        {
            preferences = preferences with { CompactLibrary = compact.IsChecked == true };
            Save();
        };
        PageBody.Children.Add(compact);
        var files = Row(PageBody);
        Button(files, "Import custom theme", async () =>
        {
            if (await PickFile("Theme JSON", ["*.json"]) is not { } path) return;
            await Read("Reading theme…", _ => Task.FromResult(ModWorkspace.ImportTheme(path)), theme =>
            {
                Color.Parse(theme.Accent);
                Color.Parse(theme.Tint);
                preferences = preferences with { CustomTheme = theme, Theme = "Custom" };
                Save();
                ApplyTheme();
            });
        });
        Button(files, "Export theme", async () =>
        {
            var theme = preferences.CustomTheme ?? new CustomPalette("#DBF4A9", "#29382C", 16, .92);
            if (await SaveFile("Theme JSON", "SimpleModManager-theme.json") is not { } path) return;
            await Read("Exporting theme…", _ => { ModWorkspace.ExportTheme(path, theme); return Task.FromResult(true); }, _ => Status("Theme saved."));
        });
        PageBody.Children.Add(Text("Liquid glass uses a dark theme on Linux. Desktop blur depends on your Linux window manager."));
        Button(PageBody, preferences.TutorialInProgress == true ? "Resume tutorial" : "Open tutorial", () => RenderTutorial(preferences.TutorialInProgress == true ? preferences.TutorialPosition : 0));
        RenderUpdateSettings();
    }

    private void RenderTutorial(int step)
    {
        var lessons = new[]
        {
            ("Game Setup", "Choose your Steam installation and install BepInEx. Copy the Proton winhttp launch setting into Steam."),
            ("Official Mods", "Search or choose a category. Open a card to install it and use ☆ Favorite to save it."),
            ("Installed Mods", "Close Gorilla Tag before enabling, disabling, or removing DLLs. Local plugins appear here too."),
            ("Mod Tools", "Profiles save enabled plugins. Inventory shows duplicate IDs. The Israel Auth scan checks plugin strings."),
            ("Settings", "Choose your theme here. Settings and mod profiles are saved in your Linux application data folder.")
        };
        step = Math.Clamp(step, 0, lessons.Length - 1);
        preferences = preferences with { TutorialPosition = step, TutorialInProgress = true, TutorialSeen = true };
        Save();
        Go("tutorial", () =>
        {
            Heading(lessons[step].Item1, $"Tutorial · {step + 1} of {lessons.Length}");
            PageBody.Children.Add(Text(lessons[step].Item2, false));
            var buttons = Row(PageBody);
            if (step > 0) Button(buttons, "Back", () => RenderTutorial(step - 1));
            Button(buttons, step == lessons.Length - 1 ? "Done" : "Next", () =>
            {
                if (step < lessons.Length - 1) RenderTutorial(step + 1);
                else
                {
                    preferences = preferences with { TutorialPosition = 0, TutorialInProgress = false };
                    Save();
                    Go("settings", RenderSettings, true);
                }
            }, true);
        });
    }

    private void ToolPage(string name, Action render) => Child(name, render, () => Go("tools", RenderTools, true));

    private void RenderTools()
    {
        Heading("Mod Tools");
        var library = Row(PageBody);
        Button(library, favoritesOnly ? "All mods" : "Favorites", () =>
        {
            favoritesOnly = !favoritesOnly;
            collection = null;
            Go("library", RenderLibrary, true);
        });
        Button(library, "Collections", () => ToolPage("collections", RenderCollections));
        Button(library, "Download queue", () => ToolPage("queue", RenderQueue));
        Button(library, "Check releases", () => ToolPage("releases", RenderReleases));
        var installed = Row(PageBody);
        Button(installed, "Inventory and bulk actions", () => ToolPage("inventory", RenderInventory)).IsEnabled = game is not null;
        Button(installed, "Scan for Israel Auth", () => ToolPage("scan", RenderScan)).IsEnabled = game is not null;
        Button(installed, "Profiles", () => ToolPage("profiles", RenderProfiles)).IsEnabled = game is not null;
        Button(installed, "Install history and undo", () => ToolPage("history", RenderHistory)).IsEnabled = game is not null;
        Button(installed, "Config files", () => ToolPage("configs", RenderConfigs)).IsEnabled = game is not null;
        Button(installed, "BepInEx log", () => ToolPage("log", RenderLog)).IsEnabled = game is not null;
        Button(installed, "Health check", () =>
        {
            Refresh();
            Status($"{GameLabel.Text} · {LoaderLabel.Text} · {states.Count(pair => pair.Value != ModState.Available)} catalog mods installed");
        });
    }

    private async void RenderScan()
    {
        Heading("Israel Auth scan", "Scanning enabled and disabled DLLs in your selected game folder.");
        if (game is not { } target) return;
        await Read("Scanning plugins…", token => PluginInspector.Scan(target, false, token), scan =>
        {
            var matches = scan.Plugins.Where(plugin => plugin.Indicators.Count > 0).ToArray();
            PageBody.Children.Add(Text(matches.Length == 0 ? "No Israel Auth indicators found."
                : $"{matches.Length} plugin(s) match Israel Auth indicators.", false));
            foreach (var plugin in matches) PageBody.Children.Add(Text(plugin.File));
            if (matches.Length > 0) PageBody.Children.Add(Text("A match can also come from a mod that detects or references Israel Auth."));
            foreach (var plugin in scan.Plugins.Where(plugin => plugin.Error is not null))
                PageBody.Children.Add(Text($"Could not fully scan {plugin.File}. {plugin.Error}"));
            Button(PageBody, "Scan again", () => Go("scan", RenderScan));
        });
    }

    private async void RenderInventory()
    {
        Heading("Plugin inventory");
        if (game is not { } target) return;
        var search = Input(PageBody, "Search names or plugin IDs");
        var actions = Row(PageBody);
        var rows = new StackPanel { Spacing = 8 };
        PageBody.Children.Add(rows);
        var selected = new Dictionary<string, CheckBox>();
        PluginInspection[] plugins = [];
        void Draw()
        {
            rows.Children.Clear();
            selected.Clear();
            var duplicateIds = plugins.SelectMany(plugin => plugin.Identities.Select(identity => (plugin.File, identity.Id)))
                .GroupBy(item => item.Id).Where(group => group.Select(item => item.File).Distinct().Count() > 1).Select(group => group.Key).ToHashSet();
            foreach (var plugin in plugins)
            {
                var text = $"{plugin.File} · {(plugin.Disabled ? "Disabled" : "Enabled")} · {plugin.AssemblyVersion}\n" +
                    string.Join(", ", plugin.Identities.Select(identity => $"{identity.Name} · {identity.Id} · {identity.Version}"));
                if (!text.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                if (plugin.Identities.Any(identity => duplicateIds.Contains(identity.Id))) text += " · Duplicate plugin ID";
                var box = new CheckBox { Content = Text(text), IsEnabled = plugin.Error is null };
                rows.Children.Add(box);
                selected[plugin.File] = box;
            }
        }
        search.TextChanged += (_, _) => Draw();
        async Task Toggle(bool enable)
        {
            var desired = selected.Where(item => item.Value.IsChecked == true).ToDictionary(item => ModWorkspace.ActiveName(item.Key), _ => enable);
            await Preview(target, desired, "Bulk plugin change");
        }
        Button(actions, "Enable selected", async () => await Toggle(true));
        Button(actions, "Disable selected", async () => await Toggle(false));
        await Read("Reading plugin metadata…", token => Task.FromResult(ModWorkspace.Inventory(target, token)), found => { plugins = found; Draw(); });
    }

    private async Task Preview(GameInstall target, Dictionary<string, bool> desired, string title)
    {
        await Read("Checking profile changes…", async token =>
        {
            var scan = await PluginInspector.Scan(target, false, token);
            if (scan.Plugins.Any(plugin => plugin.Indicators.Count > 0 && desired.GetValueOrDefault(ModWorkspace.ActiveName(plugin.File))))
                throw new InvalidOperationException("A selected plugin matches Israel Auth indicators.");
            return ModWorkspace.PlanToggles(target, scan.Plugins, desired, token);
        }, changes =>
        {
            if (changes.Length == 0) { Status("No changes needed."); return; }
            ToolPage("preview", () =>
            {
                Heading(title, $"{changes.Length} plugin files will change.");
                foreach (var change in changes) PageBody.Children.Add(Text(change.After));
                Button(PageBody, "Apply changes", async () =>
                {
                    await Run(title, (_, token) =>
                    {
                        ModWorkspace.MovePlugins(target, changes, token);
                        Record(target, title, changes);
                        return Task.CompletedTask;
                    });
                    Go("tools", RenderTools, true);
                }, true);
            });
        });
    }

    private void RenderProfiles()
    {
        Heading("Mod profiles", "Save enabled plugins for your current game folder.");
        if (game is not { } target) return;
        var name = Input(PageBody, "Profile name");
        Button(PageBody, "Save current profile", async () =>
        {
            var key = name.Text?.Trim() ?? "";
            if (key.Length is < 1 or > 48) { Status("Enter a name up to 48 characters."); return; }
            await Read("Reading plugin states…", token => Task.FromResult(ModWorkspace.Inventory(target, token)), plugins =>
            {
                preferences = preferences with { Profiles = [.. (preferences.Profiles ?? []).Where(item => item.Name != key || item.GamePath != target.Path),
                    new(key, target.Path, plugins.Where(item => !item.Disabled).Select(item => item.File).ToArray(),
                        plugins.Select(item => ModWorkspace.ActiveName(item.File)).Distinct().ToArray())] };
                Save();
                Go("profiles", RenderProfiles);
            });
        }, true);
        foreach (var preset in (preferences.Profiles ?? []).Where(item => item.GamePath == target.Path))
        {
            var row = Row(PageBody);
            Button(row, "Preview " + preset.Name, async () =>
            {
                var enabled = preset.EnabledFiles.ToHashSet();
                await Preview(target, (preset.KnownFiles ?? preset.EnabledFiles).ToDictionary(file => file, enabled.Contains), "Apply " + preset.Name);
            });
            ConfirmAction(row, "Delete", "Delete this profile?", () =>
            {
                preferences = preferences with { Profiles = preferences.Profiles!.Where(item => item != preset).ToArray() };
                Save();
                Go("profiles", RenderProfiles);
                return Task.CompletedTask;
            });
        }
    }

    private void RenderCollections()
    {
        Heading("Collections");
        var name = Input(PageBody, "Collection name");
        var choices = new Dictionary<string, CheckBox>();
        foreach (var mod in Catalog.Mods.Where(item => !item.DependencyOnly))
        {
            var box = new CheckBox { Content = mod.Name };
            choices.Add(mod.Id, box);
            PageBody.Children.Add(box);
        }
        Button(PageBody, "Save collection", () =>
        {
            var key = name.Text?.Trim() ?? "";
            var ids = choices.Where(item => item.Value.IsChecked == true).Select(item => item.Key).ToArray();
            if (key.Length is < 1 or > 48 || ids.Length == 0) { Status("Enter a collection name and select mods."); return; }
            preferences = preferences with { Collections = [.. (preferences.Collections ?? []).Where(item => item.Name != key), new(key, ids)] };
            Save();
            Go("collections", RenderCollections);
        }, true);
        foreach (var item in preferences.Collections ?? [])
        {
            var row = Row(PageBody);
            Button(row, item.Name, () => { collection = item.Name; favoritesOnly = false; category = "All mods"; Go("library", RenderLibrary, true); });
            ConfirmAction(row, "Delete", "Delete this collection?", () =>
            {
                preferences = preferences with { Collections = preferences.Collections!.Where(value => value != item).ToArray() };
                Save();
                Go("collections", RenderCollections);
                return Task.CompletedTask;
            });
        }
        Button(PageBody, "Clear collection filter", () => { collection = null; Go("library", RenderLibrary, true); });
    }

    private async void RenderReleases()
    {
        Heading("Check releases", "Compare installed versions with publisher release notes.");
        var target = game;
        await Read("Checking releases…", token => ReleaseChecks.Check(target is null ? [] : ModWorkspace.Inventory(target, token), token), releases =>
        {
            foreach (var release in releases)
            {
                PageBody.Children.Add(Text($"{release.Mod.Name} · Installed {release.Installed} · Published {release.Published} · {release.Status}", false));
                PageBody.Children.Add(new Expander { Header = "Release notes", Content = Text(release.Notes), HorizontalAlignment = HorizontalAlignment.Stretch });
            }
        });
    }

    private async Task<string?> SaveFile(string title, string suggested)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggested });
        return file?.TryGetLocalPath();
    }
}
