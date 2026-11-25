using System.Windows;
using System.IO;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace SimpleModManager;

public partial class MainWindow
{
    private string category = "All mods";
    private Mod? selectedMod;
    private Button? lastDetailButton;

    private void RenderCards()
    {
        if (!initialized || page is not (Page.Library or Page.Installed))
            return;
        var terms = SearchBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Compact).Where(term => term.Length > 0).ToArray();
        var collectionIds = (preferences.Collections ?? []).FirstOrDefault(item => item.Name == collectionFilter)?.ModIds;
        var visible = Catalog.Mods.Where(mod => page == Page.Installed
            ? StateOf(mod) != ModState.Available && terms.All(term => MatchesSearch(mod, term))
            : !mod.DependencyOnly && (!favoritesOnly || IsFavorite(mod.Id)) &&
              (collectionIds is null || collectionIds.Contains(mod.Id)) && (category == "All mods" || category == mod.Category) &&
              terms.All(term => MatchesSearch(mod, term))).ToArray();

        Cards.Children.Clear();
        for (var index = 0; index < visible.Length; index++)
        {
            var card = CreateCard(visible[index]);
            Cards.Children.Add(card);
            if (IsLoaded)
                Reveal(card, 8, Math.Min(index * 23, 115), 210);
        }
        var count = visible.Length;
        if (page == Page.Installed && game is { } target)
        {
            foreach (var file in Installer.UnlistedPlugins(target))
            {
                var disabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                var dll = disabled ? file[..^".disabled".Length] : file;
                var inspected = target.Path == inventoryPath ? inventoryCache.GetValueOrDefault(file) : null;
                var identity = inspected?.Identities.FirstOrDefault();
                var name = identity?.Name ?? Path.GetFileNameWithoutExtension(dll);
                if (!terms.All(term => Compact(file + name + identity?.Id).Contains(term, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var (color, artwork) = LocalPluginArtwork(name);
                var art = new Mod("", name, "", "", "", "", "", "", "", color, artwork);
                var content = new StackPanel();
                var card = new Border { Style = (Style)FindResource("Panel"), Margin = new Thickness(6, 0, 6, 13),
                    Child = content, ToolTip = file };
                var banner = CardArt.Banner(art, false);
                banner.Height = 103;
                banner.SizeChanged += (_, _) => banner.Clip = new RectangleGeometry(
                    new Rect(0, 0, banner.ActualWidth, banner.ActualHeight + 16), 15, 15);
                content.Children.Add(banner);
                var body = new StackPanel { Margin = new Thickness(13, 10, 13, 10) };
                var title = new DockPanel();
                var icon = CardArt.Icon(art, 18);
                DockPanel.SetDock(icon, Dock.Left);
                title.Children.Add(icon);
                title.Children.Add(new TextBlock { Text = name, Margin = new Thickness(8, 0, 0, 0),
                    FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                body.Children.Add(title);
                body.Children.Add(new TextBlock { Text = disabled ? "Disabled · Local DLL" : "Installed · Local DLL",
                    Margin = new Thickness(0, 5, 0, 4), Style = (Style)FindResource("MutedText") });
                body.Children.Add(new TextBlock { Text = identity is null ? inspected?.AssemblyVersion is { } version ? $"{version} · {file}" : file : $"{identity.Version} · {identity.Id}", TextTrimming = TextTrimming.CharacterEllipsis,
                    Style = (Style)FindResource("MutedText") });
                var toggle = new Button { Content = disabled ? "Enable mod" : "Disable mod",
                    HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 9, 0, 0) };
                AutomationProperties.SetName(toggle, $"{toggle.Content}: {name}");
                toggle.Click += async (_, _) =>
                {
                    if (operation is not null || game != target)
                        return;
                    await RunOperation((_, _) =>
                    {
                        return ToggleWithHistory(target, file, disabled);
                    }, $"{name} {(disabled ? "enabled" : "disabled")}.");
                };
                body.Children.Add(toggle);
                content.Children.Add(body);
                Cards.Children.Add(card);
                count++;
            }
        }
        if (page == Page.Library)
            CatalogHeading.Text = favoritesOnly ? "Favorites" : collectionFilter ?? "Browse mods";
        CatalogCount.Text = count == 1 ? "1 mod" : $"{count} mods";
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = page == Page.Installed
            ? game is null ? "Choose a game folder in Game setup to see installed mods." : terms.Length == 0 ? "No plugin DLLs are installed in this game folder." : "No installed mods match your search."
            : "No mods match your search.";
    }

    private static (string Color, string Artwork) LocalPluginArtwork(string name)
    {
        var key = name.ToLowerInvariant();
        if (key.Contains("camera") || key.Contains("capture") || key.Contains("spectator"))
            return ("#A6C3FF", "lens");
        if (key.Contains("shirt") || key.Contains("cosmetic") || key.Contains("wardrobe"))
            return ("#F4B5CC", "shirt");
        if (key.Contains("map") || key.Contains("world") || key.Contains("terrain"))
            return ("#A7DEAF", "nodes");
        return ("#D3BAF7", "cube");
    }

    private static string Compact(string text) => string.Concat(text.Where(char.IsLetterOrDigit));

    private static bool MatchesSearch(Mod mod, string term)
    {
        if (term.Equals("legal", StringComparison.OrdinalIgnoreCase) || term.Equals("verified", StringComparison.OrdinalIgnoreCase) || term.Equals("illegal", StringComparison.OrdinalIgnoreCase)
            || term.Equals("restricted", StringComparison.OrdinalIgnoreCase))
            return mod.Usage.ToString().Equals(term, StringComparison.OrdinalIgnoreCase);
        return new[] { mod.Name, mod.Description, mod.Author, mod.Category, mod.DisplayVersion }
            .Any(value => Compact(value).Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private Button CreateCard(Mod mod)
    {
        var content = new StackPanel();
        var card = new Button
        {
            Style = (Style)FindResource("ModCard"), Margin = new Thickness(6, 0, 6, 13), Content = content, Tag = mod.Id
        };
        AutomationProperties.SetName(card, $"Open {mod.Name}");
        card.Click += (_, _) => ShowDetails(mod, card);
        if (MotionEnabled)
        {
            var lift = new TranslateTransform();
            content.RenderTransform = lift;
            card.MouseEnter += (_, _) => LiftCard(lift, -2);
            card.MouseLeave += (_, _) => LiftCard(lift, 0);
        }
        var art = CardArt.Banner(mod, preferences.AnimatedBanners);
        var label = Pill(mod.Category, Color.FromRgb(224, 228, 230));
        label.HorizontalAlignment = HorizontalAlignment.Left;
        art.Children.Add(label);
        var badgeColor = mod.Usage switch
        {
            UsageTag.Legal or UsageTag.Verified => Color.FromRgb(210, 245, 138),
            UsageTag.Illegal or UsageTag.Restricted => Color.FromRgb(255, 179, 165),
            _ => Color.FromRgb(218, 221, 228)
        };
        var usage = Pill(mod.Usage.ToString(), badgeColor);
        usage.HorizontalAlignment = HorizontalAlignment.Right;
        usage.ToolTip = UsageDescription(mod);
        art.Children.Add(usage);
        if (IsFavorite(mod.Id))
        {
            var favorite = Pill("★", Color.FromRgb(255, 219, 133));
            favorite.HorizontalAlignment = HorizontalAlignment.Right;
            favorite.VerticalAlignment = VerticalAlignment.Top;
            favorite.Margin = new Thickness(0, 7, 69, 0);
            art.Children.Add(favorite);
        }
        var artFrame = new Border { Height = preferences.CompactLibrary ? 58 : 103, Child = art };
        artFrame.SizeChanged += (_, _) => artFrame.Clip = new RectangleGeometry(
            new Rect(0, 0, artFrame.ActualWidth, artFrame.ActualHeight + 16), 15, 15);
        content.Children.Add(artFrame);

        var body = new StackPanel { Margin = new Thickness(13, 10, 13, 10) };
        content.Children.Add(body);
        var identity = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var accent = CardArt.Accent(mod);
        var icon = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 9, 0),
            Background = new SolidColorBrush(CardArt.WithAlpha(accent, 23)),
            BorderBrush = new SolidColorBrush(CardArt.WithAlpha(accent, 65)), BorderThickness = new Thickness(1),
            Child = CardArt.Icon(mod, 17)
        };
        DockPanel.SetDock(icon, Dock.Left);
        identity.Children.Add(icon);
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = mod.Name, FontSize = 15, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = mod.Name });
        name.Children.Add(new TextBlock { Text = mod.Author, Style = (Style)FindResource("MutedText"),
            FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        identity.Children.Add(name);
        body.Children.Add(identity);
        if (!preferences.CompactLibrary)
            body.Children.Add(new TextBlock
        {
            Text = mod.Description, Style = (Style)FindResource("MutedText"), Height = 34,
            TextWrapping = TextWrapping.Wrap, ClipToBounds = true, FontWeight = FontWeights.Normal
        });

        var actions = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var arrow = new TextBlock
        {
            Text = "→", FontSize = 20, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, -2, 0, 0)
        };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        DockPanel.SetDock(arrow, Dock.Right);
        actions.Children.Add(arrow);
        var state = StateOf(mod);
        var version = new TextBlock
        {
            Text = state switch
            {
                ModState.Installed or ModState.External => "Installed",
                ModState.Disabled or ModState.ExternalDisabled => "Disabled",
                ModState.DifferentVersion => "Different version",
                ModState.Conflict => "Multiple copies",
                _ => mod.Preview ? "Preview" : mod.DisplayVersion
            },
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        };
        version.SetResourceReference(TextBlock.ForegroundProperty,
            state is ModState.Installed or ModState.External ? "Accent" : "Muted");
        actions.Children.Add(version);
        body.Children.Add(actions);
        return card;
    }

    private static void LiftCard(TranslateTransform transform, double target)
    {
        var current = transform.Y;
        transform.Y = target;
        transform.BeginAnimation(TranslateTransform.YProperty, Ease(current, target, 155));
    }

    private static Border Pill(string text, Color color) => new()
    {
        CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 3, 7, 3),
        Background = new SolidColorBrush(Color.FromArgb(237, 22, 24, 27)),
        BorderBrush = new SolidColorBrush(CardArt.WithAlpha(color, 62)), BorderThickness = new Thickness(1),
        Margin = new Thickness(9), VerticalAlignment = VerticalAlignment.Top,
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color) }
    };

    private static string UsageDescription(Mod mod) => mod.Usage switch
    {
        UsageTag.Legal when mod.Category == "Mod Menus" => "Catalog usage label. Use this mod in modded rooms.",
        UsageTag.Legal => "Community catalog usage label.",
        UsageTag.Illegal => "Disallowed by game rules.",
        UsageTag.Restricted => "The release contains tools that can disrupt other players’ sessions.",
        UsageTag.Utility => "A dependency for other mods. Follow each mod’s room requirements.",
        _ => "Usage classification has not been verified."
    };

    private ModState StateOf(Mod mod) => modStates.GetValueOrDefault(mod.Id, ModState.Available);

    private void ShowDetails(Mod mod, Button source)
    {
        selectedMod = mod;
        lastDetailButton = source;
        DetailArt.Child = CardArt.Banner(mod, preferences.AnimatedBanners);
        DetailName.Text = mod.Name;
        RefreshFavorite(mod);
        QueueModButton.Visibility = CanQueue(mod) && StateOf(mod) == ModState.Available ? Visibility.Visible : Visibility.Collapsed;
        DetailMeta.Text = $"{mod.Author}  ·  {mod.DisplayVersion}  ·  {mod.Category}  ·  {mod.Usage}";
        DetailDescription.Text = mod.Description;
        ModSourceButton.Visibility = mod.Source is null ? Visibility.Collapsed : Visibility.Visible;
        var state = StateOf(mod);
        var notes = new List<string>();
        if (mod.Usage == UsageTag.Legal && mod.Category == "Mod Menus")
            notes.Add("Use in modded rooms.");
        if (mod.Dependency is { } dependency)
            {
            var required = Catalog.Mods.Single(item => item.Id == dependency);
            var stateText = StateOf(required) switch
            {
                ModState.Available => "will be installed automatically",
                ModState.Disabled or ModState.ExternalDisabled => "disabled",
                ModState.Conflict => "multiple copies detected",
                _ => "present"
            };
            notes.Add($"Requires {required.Name} {mod.MinimumDependencyVersion ?? required.DisplayVersion} or newer ({stateText}).");
        }
        notes.Add(mod.SteamOnly
            ? "Tested on Steam. Requires the Steam version."
            : "Tested on Steam. Meta Horizon PC support is unknown.");
        if (mod.Preview)
            notes.Add("This is a prerelease build.");
        if (mod.LocalFile && state == ModState.Available)
            notes.Add("Select the supplied DLL to install it.");
        if (state is ModState.External or ModState.ExternalDisabled or ModState.DifferentVersion)
            notes.Add("A copy is already installed in the game’s plugins folder.");
        if (state == ModState.Conflict)
            notes.Add("Multiple copies are present. Keep one copy before using the controls here.");
        if (mod.Usage == UsageTag.Restricted)
            notes.Add("This release includes player-disruption tools. It is listed for reference.");
        else if (mod.Restricted)
            notes.Add("This mod is unavailable for installation.");
        DetailNote.Text = string.Join(" ", notes);
        DetailNote.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var disabled = Installer.IsDisabled(state);
        var canToggle = Installer.IsEnabled(state) || disabled && !mod.Restricted;
        var canRemove = !mod.Restricted && state is ModState.Installed or ModState.Disabled;
        ToggleModButton.Visibility = canToggle ? Visibility.Visible : Visibility.Collapsed;
        ToggleModButton.Content = disabled ? "Enable mod" : "Disable mod";
        ToggleModButton.IsEnabled = operation is null;
        DetailAction.Visibility = canToggle && !canRemove ? Visibility.Collapsed : Visibility.Visible;
        DetailAction.Style = (Style)FindResource(canRemove ? typeof(Button) : "Primary");
        DetailAction.Content = state switch
        {
            _ when mod.Restricted => "Unavailable",
            ModState.Installed or ModState.Disabled => "Remove",
            ModState.Conflict => "Multiple copies",
            _ => game is null || !loader.Installed ? "Game setup" : mod.LocalFile ? "Install from file" : "Install mod"
        };
        DetailAction.IsEnabled = operation is null && !mod.Restricted
            && (canRemove || state == ModState.Available && !(mod.SteamOnly && game?.Store == Storefront.Meta));
        DetailsOverlay.Visibility = Visibility.Visible;
        Reveal(DetailsCard, 14);
        if (canToggle && ToggleModButton.IsEnabled)
            ToggleModButton.Focus();
        else if (DetailAction.IsEnabled)
            DetailAction.Focus();
        else
            CloseDetailsButton.Focus();
    }

    private void HideDetails()
    {
        DetailsOverlay.Visibility = Visibility.Collapsed;
        selectedMod = null;
        lastDetailButton?.Focus();
        lastDetailButton = null;
    }

    private void ClipDetailArt(object sender, SizeChangedEventArgs e) =>
        DetailArt.Clip = new RectangleGeometry(new Rect(e.NewSize), 9, 9);

    private async void ChangeMod(object sender, RoutedEventArgs e)
    {
        if (selectedMod is not { } mod)
            return;
        var state = StateOf(mod);
        if (game is not { } target || !loader.Installed)
        {
            HideDetails();
            Navigate(Page.Setup);
            return;
        }
        string? localFile = null;
        if (mod.LocalFile && state == ModState.Available)
        {
            var picker = new OpenFileDialog
            {
                Title = $"Select the {mod.Name} DLL", Filter = "Mod DLL (*.dll)|*.dll",
                CheckFileExists = true
            };
            if (picker.ShowDialog(this) != true)
                return;
            localFile = picker.FileName;
        }
        HideDetails();
        if (state is ModState.Installed or ModState.Disabled)
        {
            await RunOperation((_, token) => RemoveWithHistory(target, mod, token), $"{mod.Name} removed.");
        }
        else
        {
            await RunOperation((report, token) => InstallWithHistory(target, mod, report, token, localFile),
                $"{mod.Name} installed.", $"Installing {mod.Name}");
        }
    }

    private async void ToggleMod(object sender, RoutedEventArgs e)
    {
        if (selectedMod is not { } mod || game is not { } target || operation is not null)
            return;
        var enable = Installer.IsDisabled(StateOf(mod));
        HideDetails();
        await RunOperation((_, _) => ToggleWithHistory(target, mod, enable), $"{mod.Name} {(enable ? "enabled" : "disabled")}.");
    }

    private void FilterMods(object sender, RoutedEventArgs e)
    {
        SelectCategory((string)((Button)sender).Content);
        CardScroll.ResetPosition();
        RenderCards();
    }

    private void SelectCategory(string name)
    {
        category = name;
        foreach (var button in CategoryButtons)
            button.Tag = (string)button.Content == name ? "Active" : null;
        MoveSelection(FilterChoices, FilterOutline, CategoryButtons.First(button => Equals(button.Content, name)), IsLoaded);
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized)
            return;
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (SearchBox.Text.Length > 0 && page == Page.Library)
            SelectCategory("All mods");
        CardScroll.ResetPosition();
        RenderCards();
    }

    private void ClearSearch(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void ResizeCards(object sender, SizeChangedEventArgs e) => Cards.Columns = Math.Clamp((int)(e.NewSize.Width / 250), 1, 3);
    private void CloseDetails(object sender, RoutedEventArgs e) => HideDetails();
    private void DismissDetailsFromBackdrop(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, DetailsOverlay))
            return;
        e.Handled = true;
        HideDetails();
    }
    private void OpenModSource(object sender, RoutedEventArgs e) { if (selectedMod?.Source is { } source) OpenLink(source); }
}

