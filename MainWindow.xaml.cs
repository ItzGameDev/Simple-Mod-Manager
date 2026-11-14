using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Principal;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace SimpleModManager;

public partial class MainWindow : Window
{
    private enum Page { Library, Installed, ModTools, Setup, Settings }

    private readonly PreferencesStore store;
    private Preferences preferences;
    private GameInstall? game;
    private IReadOnlyList<GameInstall> detected = [];
    private Dictionary<string, ModState> modStates = [];
    private LoaderState loader = new(null, false);
    private Page page;
    private bool initialized;
    private CancellationTokenSource? operation;
    private bool closeAfterOperation;
    private bool minimizing;
    private WindowState previousWindowState;

    internal MainWindow(PreferencesStore store)
    {
        this.store = store;
        preferences = store.Read();
        ValidateSavedTheme();
        InitializeComponent();
        LoadSettings();
        UpdateTutorialButton();
        ApplyTheme(preferences.Theme, animate: false);
        initialized = true;
        Navigate(Page.Library);
        Loaded += FirstLoad;
        Closing += CancelWhileClosing;
        Closed += (_, _) => StopTutorialTracking();
    }

    private async void FirstLoad(object sender, RoutedEventArgs e)
    {
        Loaded -= FirstLoad;
        AlignSelections();
        Reveal(MainSurface, 10, duration: 265);
        UpdateHeroMotion();
        await Scan(restoreSaved: true);
        if (!closeAfterOperation && IsVisible && !preferences.TutorialSeen)
            ShowTutorial(this, new RoutedEventArgs());
    }

    private async Task Scan(bool restoreSaved)
    {
        if (operation is not null)
            return;
        try
        {
            detected = GameDiscovery.Find();
            var saved = restoreSaved && preferences.GamePath is { } path
                ? detected.FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                    ?? GameDiscovery.Open(path)
                : game is { } current ? GameDiscovery.Open(current.Path, current.Store) : null;
            game = saved ?? (detected.Count == 1 ? detected[0] : null);
            if (game is null && detected.Count > 1)
                Navigate(Page.Setup);
            Refresh();
            if (game is not null && !loader.Installed)
                await SetUpLoader();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status(ex.Message, error: true);
        }
    }

    private void Refresh()
    {
        loader = game is null ? new(null, false) : Installer.ReadLoader(game);
        modStates = game is null ? [] : Installer.ReadMods(game);
        RailGame.Text = game?.Label ?? "Gorilla Tag";
        var outdated = Version.TryParse(loader.Version, out var installedVersion) &&
            installedVersion < Version.Parse(Catalog.BepInExVersion);
        var (label, indicator) = game is null ? ("Game folder needed", Colors.IndianRed)
            : !loader.Installed ? ("BepInEx setup needed", Colors.IndianRed)
            : !loader.Enabled ? ("BepInEx disabled", Colors.Orange)
            : outdated ? ("BepInEx outdated", Colors.Orange)
            : ("BepInEx ready", Colors.LightGreen);
        RailLoader.Text = label;
        GameIndicator.Fill = new SolidColorBrush(indicator);
        SetupGame.Text = game is not null ? $"{game.Label} installation selected."
            : detected.Count > 1 ? "Choose which installation to use." : "No installation selected. Browse to Gorilla Tag on this PC.";
        GamePathBox.Visibility = game is null ? Visibility.Collapsed : Visibility.Visible;
        GamePathBox.Text = game?.Path ?? "";
        OpenGameButton.IsEnabled = game is not null;
        OpenPluginsButton.IsEnabled = game is not null && Directory.Exists(Path.Combine(game.Path, Installer.PluginsPath));
        InstallLoaderButton.IsEnabled = operation is null && game is not null && !loader.Installed;
        InstallLoaderButton.Visibility = loader.Installed ? Visibility.Collapsed : Visibility.Visible;
        SetupLoader.Text = loader.Installed
            ? $"BepInEx {loader.Version} is installed." +
                (outdated ? $" The manager ships with {Catalog.BepInExVersion}." : "") +
                (loader.Enabled ? "" : " Doorstop is currently disabled in its configuration.")
            : $"The manager installs the official BepInEx {Catalog.BepInExVersion} Windows x64 release.";

        DetectedGames.Children.Clear();
        foreach (var item in detected)
        {
            var chosen = item.Path.Equals(game?.Path, StringComparison.OrdinalIgnoreCase);
            var button = new Button
            {
                Content = chosen ? $"{item.Label}  ✓" : $"Use {item.Label}",
                ToolTip = item.Path, Margin = new Thickness(0, 0, 8, 6),
                IsEnabled = !chosen && operation is null
            };
            button.Click += async (_, _) => await SelectGame(item);
            DetectedGames.Children.Add(button);
        }
        RenderCards();
        UpdateInstalledMetadata();
        if (page == Page.ModTools)
            RenderModTools();
    }

    private void Navigate(Page destination)
    {
        if (toolPages.Count > 0)
        {
            _ = NavigateAwayFromTool(destination);
            return;
        }
        page = destination;
        LibraryNav.Tag = page == Page.Library ? "Active" : null;
        InstalledNav.Tag = page == Page.Installed ? "Active" : null;
        ModToolsNav.Tag = page == Page.ModTools ? "Active" : null;
        SetupNav.Tag = page == Page.Setup ? "Active" : null;
        SettingsNav.Tag = page == Page.Settings ? "Active" : null;
        MoveSelection(NavigationChoices, NavigationOutline, ActiveNavigationButton(), IsLoaded);
        SetPageVisibility();
        UpdateHeroMotion();
        LibraryTitle.Text = page == Page.Installed ? "Installed Mods" : "Mod Libary";
        LibraryIntro.Text = page == Page.Installed ? "Browse plugins in your selected game folder."
            : "Browse and install community mods for Gorilla Tag on PC.";
        CatalogHeading.Text = page == Page.Installed ? "In your game" : "Browse mods";
        CatalogTools.Visibility = Visibility.Visible;
        CategoryFilterFrame.Visibility = page == Page.Installed ? Visibility.Collapsed : Visibility.Visible;
        SearchHint.Text = page == Page.Installed ? "Search installed mods" : "Search mods";
        AutomationProperties.SetName(SearchBox, SearchHint.Text);
        CardScroll.ResetPosition();
        RenderCards();
        UpdateInstalledMetadata();
        if (page == Page.ModTools)
            RenderModTools();
        if (IsLoaded)
            Reveal(page switch { Page.ModTools => ModToolsPage, Page.Setup => SetupPage, Page.Settings => SettingsPage, _ => LibraryHero }, 7, duration: 205);
    }

    private void SetPageVisibility()
    {
        var root = toolPages.Count == 0;
        LibraryPage.Visibility = root && page is (Page.Library or Page.Installed) ? Visibility.Visible : Visibility.Collapsed;
        ModToolsPage.Visibility = root && page == Page.ModTools ? Visibility.Visible : Visibility.Collapsed;
        SetupPage.Visibility = root && page == Page.Setup ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = root && page == Page.Settings ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task SelectGame(GameInstall selection)
    {
        if (operation is not null)
            return;
        try
        {
            game = selection;
            preferences = preferences with { GamePath = selection.Path };
            SavePreferences();
            Refresh();
            if (!loader.Installed)
                await SetUpLoader();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status(ex.Message, error: true); }
    }

    private async Task SetUpLoader()
    {
        if (game is { } target && !loader.Installed)
            await RunOperation((report, token) => Installer.InstallLoader(target, report, token),
                "BepInEx installed. Launch Gorilla Tag once to initialize it.", "Installing BepInEx");
    }

    private async Task RunOperation(Func<Action<InstallProgress>, CancellationToken, Task> work, string complete,
        string? title = null)
    {
        if (operation is not null)
            return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        InstallLoaderButton.IsEnabled = false;
        StatusPanel.Visibility = Visibility.Collapsed;
        if (title is not null)
            ShowDownload(title);
        IProgress<InstallProgress> progress = new Progress<InstallProgress>(update =>
        {
            if (operation == cancellation && !cancellation.IsCancellationRequested && title is not null)
            {
                if (DownloadOverlay.Visibility == Visibility.Visible && !downloadDismissing)
                    UpdateDownload(update);
                else
                    Status($"{update.Message} · {update.Percent}%");
            }
        });
        try
        {
            await Task.Run(() => work(progress.Report, cancellation.Token), cancellation.Token);
            if (title is not null)
            {
                CancelDownloadButton.IsEnabled = false;
                UpdateDownload(new("Installation complete", 100));
                if (MotionEnabled)
                    await Task.Delay(340);
            }
            Status(complete);
        }
        catch (UnauthorizedAccessException)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            Status(administrator ? "Windows denied access to the game folder." :
                "Windows denied access to the game folder. Run as administrator, then retry.", error: true, admin: !administrator);
        }
        catch (OperationCanceledException)
        {
            Status("The operation was cancelled or timed out. You can retry.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or InvalidOperationException)
        {
            Status(ex.Message, error: true);
        }
        finally
        {
            await HideDownload();
            operation = null;
            if (closeAfterOperation)
                Close();
            else
            {
                try { Refresh(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status(ex.Message, error: true); }
            }
        }
    }

    private void Status(string message, bool error = false, bool admin = false)
    {
        StatusPanel.Visibility = Visibility.Visible;
        StatusText.Text = message;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "Text");
        AdminButton.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RestartElevated(object sender, RoutedEventArgs e)
    {
        if (game is null || Environment.ProcessPath is not { } executable)
            return;
        preferences = preferences with { GamePath = game.Path };
        if (!SavePreferences())
            return;
        try
        {
            Process.Start(new ProcessStartInfo(executable, $"--resume {Environment.ProcessId}") { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch (Win32Exception ex) { Status(ex.Message, error: true, admin: true); }
    }

    private bool SavePreferences()
    {
        try { store.Write(preferences); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status($"Could not save preferences. {ex.Message}", error: true);
            return false;
        }
    }

    private async void BrowseGame(object sender, RoutedEventArgs e)
    {
        if (operation is not null)
            return;
        var picker = new OpenFolderDialog { Title = "Select the Gorilla Tag installation folder" };
        if (game is { } current)
            picker.InitialDirectory = current.Path;
        if (picker.ShowDialog(this) != true)
            return;
        if (GameDiscovery.Open(picker.FolderName) is { } selection)
            await SelectGame(selection);
        else
            Status("Choose the folder containing the 64-bit Gorilla Tag.exe and its Unity game data.", error: true);
    }

    private void OpenLink(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Win32Exception ex) { Status(ex.Message, error: true); }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && selectedMod is not null)
        {
            HideDetails();
            e.Handled = true;
        }
        else if (DownloadOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
                DismissDownloadPanel();
            if (e.Key is Key.Escape or Key.F)
                e.Handled = true;
        }
        else if (TutorialOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
                FinishTutorial();
            if (e.Key is Key.Escape or Key.F)
                e.Handled = true;
        }
        else if (toolPages.Count > 0 && e.Key == Key.Escape)
        {
            _ = GoBackFromTool();
            e.Handled = true;
        }
        else if (e.Key == Key.F1 && selectedMod is null)
        {
            ShowPageHelp();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && selectedMod is null)
        {
            if (page is not (Page.Library or Page.Installed))
                Navigate(Page.Library);
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void CancelWhileClosing(object? sender, CancelEventArgs e)
    {
        if (operation is not null)
        {
            e.Cancel = true;
            closeAfterOperation = true;
            operation.Cancel();
        }
    }

    private void ShowLibrary(object sender, RoutedEventArgs e) => Navigate(Page.Library);
    private void ShowInstalled(object sender, RoutedEventArgs e) => Navigate(Page.Installed);
    private void ShowSetup(object sender, RoutedEventArgs e)
    {
        if (TutorialOverlay.IsVisible && tutorialPage == 0)
            NextTutorial(sender, e);
        else
            Navigate(Page.Setup);
    }
    private void ShowModTools(object sender, RoutedEventArgs e) => Navigate(Page.ModTools);
    private void ShowSettings(object sender, RoutedEventArgs e) => Navigate(Page.Settings);
    private void DismissStatus(object sender, RoutedEventArgs e) => StatusPanel.Visibility = Visibility.Collapsed;
    private async void ScanAgain(object sender, RoutedEventArgs e) => await Scan(restoreSaved: false);
    private async void InstallLoaderClicked(object sender, RoutedEventArgs e) => await SetUpLoader();
    private void OpenGameFolder(object sender, RoutedEventArgs e) { if (game is { } target) OpenLink(target.Path); }
    private void OpenPluginsFolder(object sender, RoutedEventArgs e) { if (game is { } target) OpenLink(Path.Combine(target.Path, Installer.PluginsPath)); }
    private void OpenBepInEx(object sender, RoutedEventArgs e) => OpenLink(Catalog.BepInExSource);
    private void OpenDiscord(object sender, RoutedEventArgs e) => OpenLink(Catalog.Discord);
    private void OpenModPolicy(object sender, RoutedEventArgs e) => OpenLink("https://www.gorillatagvr.com/fan-content-mod-policy");
    private async void Minimize(object sender, RoutedEventArgs e)
    {
        if (minimizing)
            return;
        minimizing = true;
        if (MotionEnabled)
        {
            MainSurface.BeginAnimation(OpacityProperty, Ease(MainSurface.Opacity, .55, 145));
            await Task.Delay(120);
        }
        if (IsVisible)
            SystemCommands.MinimizeWindow(this);
        MainSurface.BeginAnimation(OpacityProperty, null);
        minimizing = false;
    }
    private void Maximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (previousWindowState == WindowState.Minimized && WindowState != WindowState.Minimized)
            Reveal(MainSurface, 8, duration: 235);
        previousWindowState = WindowState;
        if (initialized)
            UpdateHeroMotion();
        MaximizeGlyph.Data = Geometry.Parse(WindowState == WindowState.Maximized
            ? "M 2,2 V 0 H 10 V 8 H 8 M 0,2 H 8 V 10 H 0 Z" : "M 0,0 H 9 V 9 H 0 Z");
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        AutomationProperties.SetName(MaximizeButton, (string)MaximizeButton.ToolTip);
    }
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}


