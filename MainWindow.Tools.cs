using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace SimpleModManager;

public partial class MainWindow
{
    private bool favoritesOnly;
    private string? collectionFilter;
    private Dictionary<string, PluginInspection> inventoryCache = new(StringComparer.OrdinalIgnoreCase);
    private int inventoryRevision;
    private string? inventoryPath;

    private async void UpdateInstalledMetadata()
    {
        var target = game;
        var revision = ++inventoryRevision;
        inventoryCache.Clear();
        inventoryPath = null;
        if (target is null || page != Page.Installed)
            return;
        try
        {
            var result = await Task.Run(() => ModWorkspace.Inventory(target, CancellationToken.None));
            if (revision != inventoryRevision || game != target || page != Page.Installed || !IsLoaded)
                return;
            SetMetadataCache(target, result);
            if (selectedMod is null && !TutorialOverlay.IsVisible)
                RenderCards();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (revision == inventoryRevision && IsLoaded)
                Status(ex.Message, error: true);
        }
    }

    private void SetMetadataCache(GameInstall target, PluginInspection[] items)
    {
        inventoryCache = items.ToDictionary(item => item.File, StringComparer.OrdinalIgnoreCase);
        inventoryPath = target.Path;
    }
    private bool IsFavorite(string id) => preferences.Favorites?.Contains(id) == true;

    private void RefreshFavorite(Mod mod)
    {
        var favorite = IsFavorite(mod.Id);
        DetailFavorite.Content = favorite ? "★  Favorited" : "☆  Favorite";
        AutomationProperties.SetName(DetailFavorite, favorite ? "Remove from favorites" : "Add to favorites");
    }

    private void FavoriteDetail(object sender, RoutedEventArgs e)
    {
        if (selectedMod is not { } mod) return;
        var favorites = (preferences.Favorites ?? []).ToHashSet();
        if (!favorites.Remove(mod.Id)) favorites.Add(mod.Id);
        preferences = preferences with { Favorites = favorites.ToArray() };
        SavePreferences();
        RefreshFavorite(mod);
        RenderCards();
        lastDetailButton = Cards.Children.OfType<Button>().FirstOrDefault(button => Equals(button.Tag, mod.Id));
    }

    private sealed class ToolsPanel(DockPanel view, StackPanel body, TextBlock status, Page parentPage)
    {
        public DockPanel View => view;
        public StackPanel Body => body;
        public TextBlock Status => status;
        public Page ParentPage => parentPage;
        public CancellationTokenSource Lifetime { get; } = new();
        public Func<Task>? OnOpen { get; set; }
        public Func<Task<bool>>? CanLeave { get; set; }
        public Action? CancelPrompt { get; set; }
        public void Report(string message) => Status.Text = message;
    }

    private readonly Stack<ToolsPanel> toolPages = new();

    private ToolsPanel NewToolsPanel(string title, string description)
    {
        var shell = new DockPanel { Margin = new Thickness(28, 25, 26, 20) };
        ApplyToolListStyles(shell);
        var intro = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        ToolButton(intro, "‹  Back", () => _ = GoBackFromTool());
        intro.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Heading") });
        if (description.Length > 0)
            intro.Children.Add(ToolText(description));
        DockPanel.SetDock(intro, Dock.Top);
        shell.Children.Add(intro);
        var status = ToolText("");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        DockPanel.SetDock(status, Dock.Bottom);
        shell.Children.Add(status);
        var body = new StackPanel();
        shell.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return new(shell, body, status, page);
    }

    private async void ShowTool(ToolsPanel panel)
    {
        if (operation is not null)
        {
            panel.Lifetime.Dispose();
            return;
        }
        toolPages.Push(panel);
        UpdateToolPage();
        if (panel.OnOpen is { } load)
            await load();
    }

    private void UpdateToolPage()
    {
        ToolPage.Content = toolPages.TryPeek(out var current) ? current.View : null;
        ToolPage.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
        SetPageVisibility();
        if (current is not null)
        {
            Reveal(current.View, 7, duration: 205);
            current.View.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
    }

    private async Task GoBackFromTool()
    {
        if (!toolPages.TryPeek(out var panel))
            return;
        if (panel.CancelPrompt is { } cancel)
        {
            cancel();
            return;
        }
        if (panel.CanLeave is null || await panel.CanLeave())
            CloseTool(panel);
    }

    private void CloseTool(ToolsPanel panel)
    {
        if (!toolPages.TryPeek(out var current) || !ReferenceEquals(panel, current))
            return;
        toolPages.Pop();
        panel.Lifetime.Cancel();
        panel.Lifetime.Dispose();
        UpdateToolPage();
        if (toolPages.Count == 0 && panel.ParentPage == Page.ModTools)
            RenderModTools();
    }

    private async Task NavigateAwayFromTool(Page destination)
    {
        if (!toolPages.TryPeek(out var current) || current.CancelPrompt is not null)
            return;
        if (current.CanLeave is not null && !await current.CanLeave())
            return;
        while (toolPages.TryPeek(out var panel))
            CloseTool(panel);
        Navigate(destination);
    }
    private void ApplyToolListStyles(FrameworkElement window)
    {
        var surface = (Brush)FindResource("Surface");
        var outline = (Brush)FindResource("Outline");
        var foreground = (Brush)FindResource("Text");
        window.Resources[SystemColors.WindowBrushKey] = isGlassTheme ? Brushes.Black : surface;
        window.Resources[SystemColors.WindowTextBrushKey] = foreground;
        window.Resources[SystemColors.HighlightBrushKey] = FindResource("Selection");
        window.Resources[SystemColors.HighlightTextBrushKey] = foreground;
        window.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = FindResource("Selection");
        window.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = foreground;
        var list = new Style(typeof(ListBox));
        list.Setters.Add(new Setter(Control.BackgroundProperty, surface));
        list.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        list.Setters.Add(new Setter(Control.BorderBrushProperty, outline));
        window.Resources[typeof(ListBox)] = list;
        var combo = new Style(typeof(ComboBox));
        combo.Setters.Add(new Setter(Control.BackgroundProperty, surface));
        combo.Setters.Add(new Setter(Control.BorderBrushProperty, outline));
        // The Windows combo box chrome retains its light face; its popup uses WindowBrushKey.
        combo.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        var comboItem = new Style(typeof(ComboBoxItem));
        comboItem.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        window.Resources[typeof(ComboBoxItem)] = comboItem;
        window.Resources[typeof(ComboBox)] = combo;
    }
    private TextBlock ToolText(string value) => new()
    {
        Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6),
        Style = (Style)FindResource("MutedText")
    };

    private Button ToolButton(Panel parent, string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 7, 7),
            HorizontalAlignment = HorizontalAlignment.Left };
        if (primary) button.Style = (Style)FindResource("Primary");
        button.Click += (_, _) => action();
        parent.Children.Add(button);
        return button;
    }

    private static WrapPanel ToolsRow(Panel parent)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 5, 0, 4) };
        parent.Children.Add(row);
        return row;
    }

    private static TextBox ToolInput(Panel parent, string label, string value = "", double width = 240)
    {
        var box = new TextBox { Text = value, Width = width, Height = 36, Margin = new Thickness(0, 0, 7, 7),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, label);
        parent.Children.Add(box);
        return box;
    }

    private void Section(Panel parent, string title) => parent.Children.Add(new TextBlock
    {
        Text = title, Style = (Style)FindResource("SectionHeading"), Margin = new Thickness(0, 13, 0, 4)
    });

    private async Task<bool> Confirm(ToolsPanel panel, string message)
    {
        if (panel.CancelPrompt is not null || !toolPages.TryPeek(out var current) || !ReferenceEquals(current, panel))
            return false;
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        panel.CancelPrompt = () => answer.TrySetResult(false);
        using var cancellation = panel.Lifetime.Token.Register(() => answer.TrySetResult(false));
        var prompt = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        prompt.Children.Add(ToolText(message));
        var buttons = ToolsRow(prompt);
        ToolButton(buttons, "Confirm", () => answer.TrySetResult(true), true);
        var cancel = ToolButton(buttons, "Cancel", () => answer.TrySetResult(false));
        DockPanel.SetDock(prompt, Dock.Bottom);
        panel.View.Children.Insert(panel.View.Children.Count - 1, prompt);
        panel.Body.IsEnabled = false;
        cancel.Focus();
        try { return await answer.Task; }
        finally
        {
            panel.View.Children.Remove(prompt);
            panel.Body.IsEnabled = true;
            panel.CancelPrompt = null;
        }
    }
    private async Task ReadTool<T>(ToolsPanel panel, string task, Func<CancellationToken, Task<T>> read, Action<T> show)
    {
        var cancellation = panel.Lifetime.Token;
        var body = panel.Body;
        body.IsEnabled = false;
        panel.Report(task);
        try
        {
            var result = await Task.Run(() => read(cancellation), cancellation);
            if (!cancellation.IsCancellationRequested)
            {
                panel.Report("");
                show(result);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
            System.Net.Http.HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            panel.Report(ex.Message);
        }
        finally
        {
            body.IsEnabled = true;
        }
    }

    private void RenderModTools()
    {
        ModToolsBody.Children.Clear();

        var library = AddModToolsSection("Library", "Organize your mods and manage downloads.");
        ToolButton(library, favoritesOnly ? "Show all in library" : "Show favorites", () =>
        {
            favoritesOnly = !favoritesOnly;
            collectionFilter = null;
            ResetLibraryView();
        });
        ToolButton(library, "Collections", OpenCollections);
        var density = ToolButton(library, preferences.CompactLibrary ? "Use full cards" : "Use compact cards", () =>
        {
            preferences = preferences with { CompactLibrary = !preferences.CompactLibrary };
            SavePreferences();
        });
        density.Click += (_, _) => density.Content = preferences.CompactLibrary ? "Use full cards" : "Use compact cards";
        ToolButton(library, "Check releases", OpenReleaseChecks);
        var queue = preferences.Queue?.Count(item => item.State != "Complete") ?? 0;
        ToolButton(library, $"Download queue ({queue})", () =>
        {
            OpenQueue();
            if (page == Page.ModTools)
                RenderModTools();
        });
        if (collectionFilter is { } collection)
        {
            ToolButton(library, $"Clear collection: {collection}", () =>
            {
                collectionFilter = null;
                ResetLibraryView();
            });
        }

        var installed = AddModToolsSection("Installed plugins", game is null
            ? "Choose a game folder in Game setup to use these tools."
            : "Inspect the DLLs in your game folder and manage plugin states.");
        ToolButton(installed, "Inventory and bulk actions", OpenInventory).IsEnabled = game is not null;
        ToolButton(installed, "Scan for Israel Auth", ScanForIsraelAuth).IsEnabled = game is not null;
        ToolButton(installed, "Profiles", OpenProfiles).IsEnabled = game is not null;
        ToolButton(installed, "Install history and undo", OpenHistory).IsEnabled = game is not null;
        ToolButton(installed, "Game diagnostics", () => OpenGameTools(this, new RoutedEventArgs())).IsEnabled = game is not null;

        var help = AddModToolsSection("Help");
        ToolButton(help, "Help (F1)", ShowPageHelp);
        ToolButton(help, "What’s new", () =>
        {
            var notes = NewToolsPanel("What’s new", "Library, installed plugins, and game setup");
            notes.Body.Children.Add(ToolText("★ Favorites, collections, compact cards, dependency previews, and GitHub release notes."));
            notes.Body.Children.Add(ToolText("Installed plugin inventory, bulk switches, profiles, duplicate IDs, Israel Auth indicator scan, and undo."));
            notes.Body.Children.Add(ToolText("Game health, log viewer, config backups and editor, download queue, custom themes, and tutorial resume."));
            ShowTool(notes);
        });
    }

    private WrapPanel AddModToolsSection(string title, string? description = null)
    {
        var section = new StackPanel();
        ModToolsBody.Children.Add(new Border
        {
            Child = section, Padding = new Thickness(16, 13, 16, 8),
            Margin = new Thickness(0, 0, 0, 12), Style = (Style)FindResource("Panel")
        });
        section.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("SectionHeading") });
        if (description is not null)
            section.Children.Add(ToolText(description));
        return ToolsRow(section);
    }
    private void ResetLibraryView()
    {
        Navigate(Page.Library);
        SearchBox.Clear();
        SelectCategory("All mods");
        RenderCards();
    }

    private void OpenCollections()
    {
        var panel = NewToolsPanel("Collections", "Save a group of catalog mods and open it as a library view.");
        var selected = new ListBox { MaxHeight = 255, SelectionMode = SelectionMode.Multiple,
            DisplayMemberPath = "Name", ItemsSource = Catalog.Mods.Where(mod => !mod.DependencyOnly).ToArray() };
        var names = new ComboBox { MinWidth = 160, Margin = new Thickness(0, 0, 7, 7) };
        var controls = ToolsRow(panel.Body);
        controls.Children.Add(names);
        var name = ToolInput(controls, "Collection name", width: 180);
        void Reload(string? key = null)
        {
            names.ItemsSource = (preferences.Collections ?? []).Select(value => value.Name).ToArray();
            names.SelectedItem = key;
        }
        names.SelectionChanged += (_, _) =>
        {
            if (names.SelectedItem is not string key) return;
            name.Text = key;
            var item = preferences.Collections!.Single(value => value.Name == key);
            selected.UnselectAll();
            foreach (var mod in selected.Items.Cast<Mod>().Where(mod => item.ModIds.Contains(mod.Id)))
                selected.SelectedItems.Add(mod);
        };
        var actions = ToolsRow(panel.Body);
        ToolButton(actions, "Save collection", () =>
        {
            var title = name.Text.Trim();
            if (title.Length is < 1 or > 48 || selected.SelectedItems.Count == 0)
            { panel.Report("Enter a name up to 48 characters and select at least one mod."); return; }
            var items = (preferences.Collections ?? []).Where(item => item.Name != title).ToList();
            items.Add(new(title, selected.SelectedItems.Cast<Mod>().Select(mod => mod.Id).ToArray()));
            preferences = preferences with { Collections = items.OrderBy(item => item.Name).ToArray() };
            SavePreferences();
            Reload(title);
            panel.Report($"Saved {title}.");
        }, true);
        ToolButton(actions, "Open in library", () =>
        {
            if (names.SelectedItem is not string key) { panel.Report("Select a saved collection."); return; }
            collectionFilter = key;
            favoritesOnly = false;
            CloseTool(panel);
            ResetLibraryView();
        });
        ToolButton(actions, "Delete collection", async () =>
        {
            if (names.SelectedItem is not string key || !await Confirm(panel, $"Delete collection \"{key}\"?")) return;
            preferences = preferences with { Collections = preferences.Collections!.Where(item => item.Name != key).ToArray() };
            if (collectionFilter == key) { collectionFilter = null; RenderCards(); }
            SavePreferences();
            Reload();
            name.Clear();
            selected.UnselectAll();
        });
        panel.Body.Children.Add(selected);
        Reload();
        ShowTool(panel);
    }

    private void OpenReleaseChecks()
    {
        var panel = NewToolsPanel("Check releases",
            "Compare installed and catalog versions with the latest stable GitHub releases. Select a row for release notes.");
        var rows = new ListBox { Height = 212 };
        var notes = ToolText("");
        panel.Body.Children.Add(rows);
        panel.Body.Children.Add(notes);
        ReleaseCheck? current = null;
        rows.SelectionChanged += (_, _) =>
        {
            current = (rows.SelectedItem as ListBoxItem)?.Tag as ReleaseCheck;
            notes.Text = current?.Notes is { Length: > 0 } text ? text : "No release notes available.";
        };
        ToolButton(panel.Body, "Open release page", () => { if (current?.Page is { } url) OpenLink(url); });
        var target = game;
        panel.OnOpen = () => ReadTool(panel, "Checking GitHub release metadata…", token =>
            ReleaseChecks.Check(target is null ? [] : ModWorkspace.Inventory(target, token), token), releases =>
        {
            foreach (var release in releases)
                rows.Items.Add(new ListBoxItem { Tag = release, Content = ToolText(
                    $"{release.Mod.Name}  ·  Installed {release.Installed}  ·  Catalog {release.Mod.DisplayVersion}  ·  Published {release.Published}\n{release.Status}") });
            if (rows.Items.Count > 0) rows.SelectedIndex = 0;
        });
        ShowTool(panel);
    }

    private void ShowPageHelp()
    {
        var description = page switch
        {
            Page.Installed => "Select Mod tools in the sidebar to scan plugin DLLs, find duplicate IDs, preview profiles, or enable and disable several plugins at once.",
            Page.ModTools => "Open inventory to inspect DLLs and change plugin states, or use Profiles to save and preview a set of enabled mods.",
            Page.Setup => "Choose your Steam or Meta installation. Game tools can check BepInEx, read its log, and open the game and config folders.",
            Page.Settings => "Select a theme or make a custom palette. Open the tutorial here. If you leave it unfinished, the button resumes at your last step.",
            _ => "Use Ctrl+F to search and Enter to open a focused mod. Use the star in details to save a favorite. Select Mod tools in the sidebar for collections and the queue."
        };
        var panel = NewToolsPanel("Help", description + " Press Tab to move between controls and Esc to go back.");
        ShowTool(panel);
    }
}



