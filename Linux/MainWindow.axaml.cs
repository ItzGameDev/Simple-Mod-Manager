using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;

namespace SimpleModManager.Linux;

public partial class MainWindow : Window
{
    private readonly PreferencesStore store;
    private Preferences preferences;
    private GameInstall? game;
    private Dictionary<string, ModState> states = [];
    private LoaderState loader = new(null, false);
    private CancellationTokenSource? operation;
    private CancellationTokenSource? pageRead;
    private readonly Stack<Action> history = new();
    private string page = "";
    private string query = "";
    private string category = "All mods";
    private bool favoritesOnly;
    private string? collection;
    private WrapPanel? cards;
    private readonly Dictionary<string, Bitmap> images = [];
    private bool closeAfterOperation;
    private readonly DispatcherTimer gradientTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private double gradientPhase;
    private LinearGradientBrush? backgroundGradient;
    private readonly ManagerAccess access = new();
    private readonly DispatcherTimer guestReminder = new() { Interval = ManagerAccess.ReminderInterval };
    private bool managerStarted;
    private Task<byte[]?>? shownAvatar;
    private Bitmap? avatarBitmap;
    private readonly Stopwatch gateClock = Stopwatch.StartNew();
    private readonly ImageBrush accountAvatar = new() { Stretch = Stretch.UniformToFill };
    private readonly RotateTransform gateOrbit = new();
    private bool openingManager;

    public MainWindow() : this(new PreferencesStore()) { }

    internal MainWindow(PreferencesStore settings)
    {
        store = settings;
        preferences = store.Read();
        InitializeComponent();
        AccountAvatarImage.Fill = accountAvatar;
        GateOrbitRing.RenderTransform = gateOrbit;
        ApplyTheme();
        gradientTimer.Tick += (_, _) =>
        {
            if (!preferences.Motion || WindowState == WindowState.Minimized) return;
            if (AccountGate.IsVisible)
                gateOrbit.Angle = gateClock.Elapsed.TotalSeconds * 22.5 % 360;
            if (backgroundGradient is null) return;
            gradientPhase += .002;
            backgroundGradient.StartPoint = new RelativePoint(.1 + .22 * Math.Sin(gradientPhase), 0, RelativeUnit.Relative);
            backgroundGradient.EndPoint = new RelativePoint(.8 + .18 * Math.Cos(gradientPhase), 1, RelativeUnit.Relative);
        };
        gradientTimer.Start();
        guestReminder.Tick += (_, _) => Status("Log in to get more downloads!");
        SizeChanged += (_, _) => ResizeCards();
        PageScroll.SizeChanged += (_, _) => ResizeCards();
        Closing += (_, e) =>
        {
            if (operation is null) return;
            e.Cancel = true;
            closeAfterOperation = true;
            operation.Cancel();
        };
        Closed += (_, _) =>
        {
            pageRead?.Cancel();
            gradientTimer.Stop();
            guestReminder.Stop();
            access.Discord.Cancel();
            accountAvatar.Source = null;
            avatarBitmap?.Dispose();
            foreach (var image in images.Values) image.Dispose();
        };
        Opened += (_, _) =>
        {
            var screen = Screens.ScreenFromWindow(this);
            if (screen is not null)
            {
                var width = screen.WorkingArea.Width / screen.Scaling;
                var height = screen.WorkingArea.Height / screen.Scaling;
                MinWidth = Math.Min(620, width);
                MinHeight = Math.Min(460, height);
                Width = Math.Min(Width, Math.Max(MinWidth, width * .92));
                Height = Math.Min(Height, Math.Max(MinHeight, height * .92));
            }
            GateSignInButton.Focus();
            _ = RevealAccountControl(GateCard, 430);
        };
        KeyDown += (_, e) =>
        {
            if (!access.CanEnter) return;
            if (e.Key == Key.Escape) { Back(); e.Handled = true; }
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control) && page is "library" or "installed")
            {
                PageBody.Children.OfType<TextBox>().FirstOrDefault()?.Focus();
                e.Handled = true;
            }
        };
        Go("library", RenderLibrary, true);
    }

    private async Task StartManager()
    {
        if (managerStarted || !access.CanEnter || !IsVisible) return;
        managerStarted = true;
        await Discover();
        if (preferences.CheckForUpdates && preferences.UpdateRepository is not null)
            await CheckAppUpdate(false);
    }

    private async void ToggleDiscordSignIn(object? sender, RoutedEventArgs e) => await ChangeAccount();

    private async Task ChangeAccount()
    {
        if (operation is not null || openingManager) return;
        await access.ToggleSignIn(RefreshAccount);
        await StartManager();
    }

    private async void EnterAsGuest(object? sender, RoutedEventArgs e)
    {
        access.EnterAsGuest();
        RefreshAccount();
        await StartManager();
    }

    private void RefreshAccount()
    {
        var discord = access.Discord;
        var wasLocked = !ManagerContent.IsVisible;
        GateStatus.Text = discord.Status == "Signed out" ? "Sign in to get unlimited mod downloads." : discord.Status;
        GateSignInButton.Content = discord.ButtonLabel;
        if (discordStatus is not null) discordStatus.Text = AccountStatus;
        if (discordSignInButton is not null) discordSignInButton.Content = discord.ButtonLabel;
        AccountWelcome.Text = access.Welcome;
        AccountPortrait.IsVisible = discord.DisplayName is not null;
        if (shownAvatar != discord.Avatar)
        {
            shownAvatar = discord.Avatar;
            accountAvatar.Source = null;
            avatarBitmap?.Dispose();
            avatarBitmap = null;
            if (shownAvatar is { } avatar)
                _ = ShowAccountAvatar(avatar);
        }
        GuestAccountButton.IsVisible = access.IsGuest;
        ManagerContent.IsVisible = access.CanEnter;
        GateProgress.Opacity = discord.IsPending ? 1 : 0;
        GateProgress.IsIndeterminate = discord.IsPending && preferences.Motion;
        GateProgress.Value = discord.IsPending ? 100 : 0;
        if (wasLocked != !access.CanEnter)
        {
            if (access.CanEnter)
                _ = OpenManagerVisually();
            else
            {
                AccountGate.IsVisible = true;
                AccountGate.IsHitTestVisible = true;
                _ = RevealAccountControl(GateCard, 420);
                GateSignInButton.Focus();
            }
        }
        if (access.IsGuest && IsVisible) guestReminder.Start();
        else guestReminder.Stop();
    }

    private async Task OpenManagerVisually()
    {
        AccountGate.IsHitTestVisible = false;
        if (!preferences.Motion)
        {
            AccountGate.IsVisible = false;
            return;
        }

        openingManager = true;
        try
        {
            await Task.WhenAll(
                AnimateAccountOpacity(ManagerContent, 0.25, 1, 720),
                AnimateAccountOpacity(AccountGate, 1, 0, 490));
        }
        finally
        {
            openingManager = false;
        }
    }

    private async Task ShowAccountAvatar(Task<byte[]?> avatar)
    {
        var bytes = await avatar;
        if (bytes is null || shownAvatar != avatar || !IsVisible)
            return;
        try
        {
            using var stream = new MemoryStream(bytes);
            avatarBitmap = Bitmap.DecodeToWidth(stream, 48);
            accountAvatar.Source = avatarBitmap;
            await RevealAccountControl(AccountPortrait, 250);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // The portrait already shows a fallback glyph.
        }
    }

    private Task RevealAccountControl(Control control, int duration)
    {
        if (!preferences.Motion)
            return Task.CompletedTask;
        return AnimateAccountOpacity(control, 0.25, 1, duration);
    }

    private async Task AnimateAccountOpacity(Control control, double from, double to, int duration)
    {
        if (to == 0) control.Opacity = 0;
        try
        {
            await new Animation
            {
                Duration = TimeSpan.FromMilliseconds(duration),
                Easing = new SineEaseInOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, from) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, to) } }
                }
            }.RunAsync(control);
            if (to == 0) control.IsVisible = false;
        }
        finally
        {
            if (to == 0) control.Opacity = 1;
        }
    }

    private string AccountStatus => access.IsGuest && access.Discord.Status == "Signed out"
        ? "Guest mode · 5 downloads per hour. Log in to get more downloads!" : access.Discord.Status;

    private static bool ReferenceOnly(Mod mod) => mod.Restricted || mod.Id is "ii-reborn" or "hamburbur" or "malachi-reborn";
    private static bool Queueable(Mod mod) => !ReferenceOnly(mod) && mod.HasPackage && !mod.LocalFile;
    private bool Favorite(Mod mod) => preferences.Favorites?.Contains(mod.Id) == true;
    private ModState State(Mod mod) => states.GetValueOrDefault(mod.Id);

    private void Save()
    {
        try { store.Write(preferences); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status("Could not save settings. " + ex.Message); }
    }

    private async Task Discover()
    {
        await Read("Finding Steam installations…", token => Task.Run(() => GameDiscovery.Find(), token), found =>
        {
            game = preferences.GamePath is { } path ? GameDiscovery.Open(path) : null;
            if (game is null && found.Count == 1) game = found[0];
            Refresh();
            Go(game is null ? "setup" : "library", game is null ? RenderSetup : RenderLibrary, true);
        });
    }

    private void Refresh()
    {
        loader = game is null ? new(null, false) : Installer.ReadLoader(game);
        states = game is null ? [] : Installer.ReadMods(game);
        GameLabel.Text = game?.Label ?? "Steam / Proton";
        var outdated = Version.TryParse(loader.Version, out var version) && version < Version.Parse(Catalog.BepInExVersion);
        LoaderLabel.Text = !loader.Installed ? "BepInEx setup needed"
            : !loader.Enabled ? "BepInEx disabled" : outdated ? "BepInEx outdated" : "BepInEx ready";
        LoaderLabel.Foreground = !loader.Installed ? Brushes.Salmon
            : !loader.Enabled || outdated ? Brushes.Orange : Brushes.LightGreen;
    }

    private void Go(string name, Action render, bool root = false)
    {
        if (operation is not null) { Status("Wait for the current operation or cancel it."); return; }
        pageRead?.Cancel();
        pageRead?.Dispose();
        pageRead = null;
        if (root) history.Clear();
        page = name;
        PageBody.Children.Clear();
        cards = null;
        PageScroll.Offset = default;
        if (!root) Button(PageBody, "‹ Back", Back);
        render();
        foreach (var (button, key) in new[] { (LibraryNav, "library"), (UnofficialNav, "unofficial"), (InstalledNav, "installed"),
            (ToolsNav, "tools"), (SetupNav, "setup"), (SettingsNav, "settings") })
            button.Classes.Set("active", key == name);
    }

    private void Child(string name, Action render, Action parent)
    {
        if (operation is not null) return;
        history.Push(parent);
        Go(name, render);
    }

    private void Back()
    {
        if (operation is not null) return;
        if (history.TryPop(out var previous)) previous();
        else Go("tools", RenderTools, true);
    }

    private void Heading(string title, string? subtitle = null)
    {
        PageBody.Children.Add(new TextBlock { Text = title, FontSize = 29, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (subtitle is not null) PageBody.Children.Add(Text(subtitle));
    }

    private static TextBlock Text(string value, bool muted = true) => new()
    {
        Text = value, TextWrapping = TextWrapping.Wrap,
        Foreground = muted ? Brush.Parse("#ADB4BF") : Brush.Parse("#F1F3F5"), Margin = new Thickness(0, 2)
    };

    private static WrapPanel Row(Panel parent)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        parent.Children.Add(row);
        return row;
    }

    private Button Button(Panel parent, string title, Action action, bool primary = false)
    {
        var button = new Button { Content = title, Margin = new Thickness(0, 3, 8, 5) };
        if (primary) button.Classes.Add("primary");
        button.Click += (_, _) => action();
        parent.Children.Add(button);
        return button;
    }

    private static TextBox Input(Panel parent, string hint, string value = "")
    {
        var input = new TextBox { Watermark = hint, Text = value, Margin = new Thickness(0, 4), MinHeight = 36 };
        parent.Children.Add(input);
        return input;
    }

    private void Status(string value) => StatusLabel.Text = value;

    private async Task Read<T>(string title, Func<CancellationToken, Task<T>> work, Action<T> show)
    {
        pageRead?.Cancel();
        pageRead?.Dispose();
        var cancellation = new CancellationTokenSource();
        pageRead = cancellation;
        var token = cancellation.Token;
        Status(title);
        try
        {
            var result = await Task.Run(() => work(token), token);
            if (!token.IsCancellationRequested) { Status(""); show(result); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or System.Net.Http.HttpRequestException or JsonException or OperationCanceledException or ArgumentException)
        { if (!token.IsCancellationRequested) Status(ex.Message); }
    }

    private async Task Run(string title, Func<Action<InstallProgress>, CancellationToken, Task> work)
    {
        if (operation is not null) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        pageRead?.Cancel();
        PageBody.IsEnabled = false;
        OperationPanel.IsVisible = true;
        OperationLabel.Text = title;
        OperationBar.Value = 0;
        IProgress<InstallProgress> progress = new Progress<InstallProgress>(update =>
        {
            if (operation != cancellation) return;
            OperationLabel.Text = $"{update.Message} · {update.Percent}%";
            OperationBar.Value = update.Percent;
        });
        try
        {
            await Task.Run(() => work(progress.Report, cancellation.Token), cancellation.Token);
            Status(title + " complete.");
        }
        catch (OperationCanceledException) { Status("Operation cancelled. You can retry."); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or System.Net.Http.HttpRequestException or ArgumentException) { Status(ex.Message); }
        finally
        {
            operation = null;
            PageBody.IsEnabled = true;
            OperationPanel.IsVisible = false;
            try { Refresh(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status(ex.Message); }
            if (closeAfterOperation) Close();
            else if (page is "library" or "installed") RenderCards();
        }
    }

    private void Record(GameInstall target, string title, FileChange[] changes)
    {
        if (changes.Length == 0) return;
        Dispatcher.UIThread.Invoke(() =>
        {
            preferences = preferences with { History = [new(DateTimeOffset.Now, target.Path, title, changes), .. preferences.History ?? []] };
            Save();
        });
    }

    private void RenderLibrary()
    {
        Heading(page == "installed" ? "Installed Mods" : collection ?? (favoritesOnly ? "Favorites" : "Official Mods"),
            page == "installed" ? "Plugins in your selected game folder." : "Browse community mods for Gorilla Tag.");
        var search = Input(PageBody, "Search mods", query);
        search.TextChanged += (_, _) => { query = search.Text ?? ""; RenderCards(); };
        if (page != "installed")
        {
            var categories = Row(PageBody);
            foreach (var item in new[] { "All mods", "Mod Menus", "Cosmetics", "Utilities", "Capture", "Libraries" })
                Button(categories, item, () => { category = item; RenderCards(); });
        }
        cards = new WrapPanel();
        cards.SizeChanged += (_, _) => ResizeCards();
        PageBody.Children.Add(cards);
        RenderCards();
    }

    private void RenderCards()
    {
        if (cards is null) return;
        cards.Children.Clear();
        var installed = page == "installed";
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var ids = preferences.Collections?.FirstOrDefault(item => item.Name == collection)?.ModIds;
        foreach (var mod in Catalog.Mods.Where(mod => installed ? State(mod) != ModState.Available : !mod.DependencyOnly &&
            (category == "All mods" || mod.Category == category) && (!favoritesOnly || Favorite(mod)) && (ids is null || ids.Contains(mod.Id))))
        {
            if (!terms.All(term => $"{mod.Name} {mod.Author} {mod.Description} {mod.Category} {mod.Usage}".Contains(term, StringComparison.OrdinalIgnoreCase)))
                continue;
            var body = new StackPanel { Spacing = 7 };
            body.Children.Add(Artwork(mod, preferences.CompactLibrary ? 55 : 96));
            var identity = new DockPanel { LastChildFill = true };
            if (mod.Icon is { } icon && LoadImage(icon) is { } image)
            {
                var picture = new Image { Source = image, Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
                DockPanel.SetDock(picture, Dock.Left);
                identity.Children.Add(picture);
            }
            identity.Children.Add(new TextBlock { Text = (Favorite(mod) ? "★ " : "") + mod.Name, FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(identity);
            body.Children.Add(Text($"{mod.Category} · {mod.Usage}"));
            if (!preferences.CompactLibrary && mod.Description.Length > 0) body.Children.Add(Text(mod.Description));
            body.Children.Add(Text(State(mod) == ModState.Available ? mod.DisplayVersion : StateText(State(mod)), false));
            var card = new Button { Content = body, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top, VerticalAlignment = VerticalAlignment.Top,
                Height = preferences.CompactLibrary ? 220 : 280,
                Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(14) };
            card.Classes.Add("modcard");
            card.Click += (_, _) => Child("details", () => RenderDetails(mod), () => Go(installed ? "installed" : "library", RenderLibrary, true));
            cards.Children.Add(card);
        }
        if (installed && game is { } target)
            foreach (var file in Installer.UnlistedPlugins(target))
            {
                if (!terms.All(term => file.Contains(term, StringComparison.OrdinalIgnoreCase))) continue;
                var body = new StackPanel { Spacing = 6 };
                var disabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                body.Children.Add(Text(Path.GetFileNameWithoutExtension(ModWorkspace.ActiveName(file)), false));
                body.Children.Add(Text(file));
                body.Children.Add(Text(disabled ? "Disabled · Local DLL" : "Installed · Local DLL"));
                Button(body, disabled ? "Enable mod" : "Disable mod", async () =>
                    await Run("Update local plugin", (_, _) =>
                    {
                        Record(target, "Toggle " + file, [Installer.SetUnlistedPluginEnabled(target, file, disabled)]);
                        return Task.CompletedTask;
                    }));
                cards.Children.Add(new Border { Child = body, Background = Brush.Parse("#1C222B"),
                    CornerRadius = new CornerRadius(14), Padding = new Thickness(14), Margin = new Thickness(0, 0, 12, 12) });
            }
        if (cards.Children.Count == 0) cards.Children.Add(Text("No mods match this view."));
        ResizeCards();
    }

    private void ResizeCards()
    {
        if (cards is null) return;
        var available = Math.Max(240, cards.Bounds.Width > 0 ? cards.Bounds.Width : PageScroll.Bounds.Width - 64);
        var columns = Math.Clamp((int)(available / 250), 1, 3);
        foreach (var card in cards.Children) card.Width = Math.Floor(available / columns) - 12;
    }

    private Border Artwork(Mod mod, double height)
    {
        var frame = new Border { Height = height, CornerRadius = new CornerRadius(10), ClipToBounds = true,
            Background = new LinearGradientBrush { GradientStops = new() { new(Color.Parse("#22272F"), 0), new(Color.Parse(mod.Color), 1) } } };
        if (mod.Banner is { } banner && !banner.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            try
            {
                if (LoadImage(banner) is { } image)
                    frame.Child = new Image { Source = image, Stretch = Stretch.UniformToFill };
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or ArgumentException) { }
        return frame;
    }

    private Bitmap? LoadImage(string name)
    {
        if (images.TryGetValue(name, out var cached)) return cached;
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://SimpleModManager.Linux/Assets/{name}"));
            var bitmap = new Bitmap(stream);
            images.Add(name, bitmap);
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { return null; }
    }

    private static string StateText(ModState state) => state switch
    {
        ModState.Installed or ModState.External => "Installed",
        ModState.Disabled or ModState.ExternalDisabled => "Disabled",
        ModState.DifferentVersion => "Different version", ModState.Conflict => "Multiple copies", _ => "Available"
    };

    private void RenderDetails(Mod mod)
    {
        Heading(mod.Name, $"{mod.Author} · {mod.DisplayVersion} · {mod.Category} · {mod.Usage}");
        PageBody.Children.Add(Artwork(mod, 150));
        if (mod.Description.Length > 0) PageBody.Children.Add(Text(mod.Description, false));
        PageBody.Children.Add(Text("Linux / Proton compatibility has not been verified for individual mods."));
        if (mod.Dependency is { } id)
        {
            var dependency = Catalog.Mods.Single(item => item.Id == id);
            PageBody.Children.Add(Text($"Requires {dependency.Name} {mod.MinimumDependencyVersion ?? dependency.Version} or newer. Installed automatically when missing."));
        }
        var actions = Row(PageBody);
        Button(actions, Favorite(mod) ? "★ Favorited" : "☆ Favorite", () =>
        {
            var favorites = (preferences.Favorites ?? []).ToHashSet();
            if (!favorites.Remove(mod.Id)) favorites.Add(mod.Id);
            preferences = preferences with { Favorites = favorites.ToArray() };
            Save();
            Go("details", () => RenderDetails(mod));
        });
        if (mod.Source is { } source) Button(actions, "View source ↗", () => OpenLink(source));
        if (game is not { } target)
        {
            Button(actions, "Game Setup", () => Go("setup", RenderSetup, true), true);
            return;
        }
        var state = State(mod);
        if (state == ModState.Available)
        {
            Button(actions, ReferenceOnly(mod) ? "Unavailable" : "Install mod", async () =>
            {
                if (ReferenceOnly(mod)) return;
                var path = mod.LocalFile ? await PickFile("Select " + mod.Name + " DLL", ["*.dll"]) : null;
                if (mod.LocalFile && path is null) return;
                await Run("Install " + mod.Name, async (report, token) =>
                {
                    await Installer.InstallLoader(target, report, token);
                    Record(target, "Install " + mod.Name, await access.InstallMod(target, mod, report, token, path));
                });
                Go("details", () => RenderDetails(mod));
            }, true).IsEnabled = !ReferenceOnly(mod);
            if (Queueable(mod)) Button(actions, "Queue", () =>
            {
                var queue = preferences.Queue ?? [];
                if (!queue.Any(item => item.GamePath == target.Path && item.ModId == mod.Id && item.State != "Complete"))
                    preferences = preferences with { Queue = [.. queue, new(target.Path, mod.Id, "Queued")] };
                Save();
                Status(mod.Name + " queued.");
            });
        }
        else
        {
            if (Installer.IsEnabled(state) || Installer.IsDisabled(state) && !ReferenceOnly(mod))
                Button(actions, Installer.IsDisabled(state) ? "Enable mod" : "Disable mod", async () =>
                {
                    await Run("Update " + mod.Name, (_, _) =>
                    {
                        Record(target, "Toggle " + mod.Name, [Installer.SetModEnabled(target, mod, Installer.IsDisabled(state))]);
                        return Task.CompletedTask;
                    });
                    Go("details", () => RenderDetails(mod));
                });
            if (state is (ModState.Installed or ModState.Disabled) && !ReferenceOnly(mod))
                ConfirmAction(actions, "Remove mod", "Remove the managed copy of this mod?", async () =>
                {
                    await Run("Remove " + mod.Name, (_, _) =>
                    {
                        Record(target, "Remove " + mod.Name, [Installer.RemoveMod(target, mod)]);
                        return Task.CompletedTask;
                    });
                    Go("details", () => RenderDetails(mod));
                });
        }
    }

    private void ConfirmAction(Panel parent, string label, string question, Func<Task> action)
    {
        Button(parent, label, () =>
        {
            var prompt = new StackPanel { Spacing = 8 };
            prompt.Children.Add(Text(question));
            var buttons = Row(prompt);
            Button(buttons, "Confirm", async () => { parent.Children.Remove(prompt); await action(); }, true);
            Button(buttons, "Cancel", () => parent.Children.Remove(prompt));
            parent.Children.Add(prompt);
        });
    }

    private async Task<string?> PickFile(string title, string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }] });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private void OpenLink(string link)
    {
        try
        {
            if (!OperatingSystem.IsLinux()) Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
            else
            {
                var info = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                info.ArgumentList.Add(link);
                Process.Start(info);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Status(ex.Message); }
    }

    private void UnofficialClicked(object? sender, RoutedEventArgs e) =>
        Go("unofficial", () => Heading("Unofficial Mods", "Coming soon.."), true);

    private void LibraryClicked(object? sender, RoutedEventArgs e) => Go("library", RenderLibrary, true);
    private void InstalledClicked(object? sender, RoutedEventArgs e) => Go("installed", RenderLibrary, true);
    private void ToolsClicked(object? sender, RoutedEventArgs e) => Go("tools", RenderTools, true);
    private void SetupClicked(object? sender, RoutedEventArgs e) => Go("setup", RenderSetup, true);
    private void SettingsClicked(object? sender, RoutedEventArgs e) => Go("settings", RenderSettings, true);
    private void DiscordClicked(object? sender, RoutedEventArgs e) => OpenLink(Catalog.Discord);
    private void CancelClicked(object? sender, RoutedEventArgs e) => operation?.Cancel();
}

