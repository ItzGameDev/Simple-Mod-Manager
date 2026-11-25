using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;

namespace SimpleModManager;

public partial class MainWindow
{
    private static readonly string[] PaletteKeys =
        ["Backdrop", "Rail", "Surface", "Raised", "Outline", "Accent", "AccentSoft", "AccentInk", "Selection", "SelectionOutline"];

    private static readonly Dictionary<string, string[]> Palettes = new()
    {
        ["Simple"] = ["#101318", "#171B22", "#1C222B", "#29323E", "#394351", "#DBF4A9", "#A7C477", "#19230E", "#2C392A", "#647952", "#202935", "#42523A"],
        ["Liquid glass"] = ["#000000", "#090909", "#101010", "#161616", "#3B3B3B", "#F5F5F5", "#BDBDBD", "#080808", "#1C1C1C", "#626262", "#000000", "#252525"],
        ["Slate"] = ["#151719", "#1C2024", "#23282D", "#313842", "#424C58", "#F3D29D", "#BA976A", "#271D11", "#423B31", "#685846", "#262D33", "#3C3831"],
        ["Midnight"] = Tinted("#7B9DFF", "#252C5C"),
        ["Aurora"] = Tinted("#96ECCD", "#283E62"),
        ["Ember"] = Tinted("#FFAD84", "#54332C"),
        ["Glacier"] = Tinted("#A4EBFF", "#25475A"),
        ["Lavender"] = Tinted("#DDC2FF", "#453657"),
        ["Rose"] = Tinted("#FFC0D6", "#54314C"),
        ["Forest"] = Tinted("#B4E5A5", "#304C35"),
        ["Ocean"] = Tinted("#8CCEFF", "#264B67"),
        ["Copper"] = Tinted("#F5C69B", "#563C34"),
        ["Violet"] = Tinted("#C5A1FF", "#3B285D"),
        ["Teal"] = Tinted("#80DDD9", "#23514E"),
        ["Crimson"] = Tinted("#FFA9B3", "#582934"),
        ["Mono"] = Tinted("#E0E5ED", "#3B424A")
    };

    private static string[] Tinted(string accent, string tint)
    {
        var color = ParseColor(tint);
        string Shade(double amount) => Color.FromRgb((byte)(13 + color.R * amount),
            (byte)(15 + color.G * amount), (byte)(19 + color.B * amount)).ToString();
        return [Shade(.07), Shade(.18), Shade(.27), Shade(.43), Shade(.63),
            accent, accent, "#10151D", Shade(.47), Shade(.89), Shade(.29), Shade(.9)];
    }

    private GradientStop? heroGlow;

    private int displayedProgress;
    private bool isGlassTheme;
    private bool heroAnimating;
    private bool MotionEnabled => preferences.Motion && SystemParameters.ClientAreaAnimation;

    private void LoadSettings()
    {
        foreach (var name in Palettes.Keys.Append(CustomThemeName))
        {
            var palette = ThemePalette(name);
            var accent = ParseColor(palette[5]);
            var gradient = new LinearGradientBrush(new GradientStopCollection
            {
                new(ParseColor(palette[^2]), 0),
                new(ParseColor(palette[^1]), .52),
                new(name == "Liquid glass" ? Colors.Black :
                    Color.FromRgb((byte)(accent.R * .53), (byte)(accent.G * .53), (byte)(accent.B * .53)), 1)
            }, 22);
            gradient.Freeze();
            var button = new Button { Content = name, DataContext = name, ToolTip = name, Background = gradient,
                BorderBrush = new SolidColorBrush(ParseColor(palette[4])),
                Style = (Style)FindResource("ThemeOption"), MinWidth = 0, Padding = new Thickness(4, 6, 4, 6) };
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            button.Click += ChooseTheme;
            ThemeOptions.Children.Add(button);
            if (name == CustomThemeName)
                button.Visibility = preferences.CustomTheme is null ? Visibility.Collapsed : Visibility.Visible;
        }
        MotionSwitch.IsChecked = preferences.Motion;
        BannersSwitch.IsChecked = preferences.AnimatedBanners;
        CardScroll.AnimateWheel = preferences.Motion;
    }

    private void SettingsSwitchLoaded(object sender, RoutedEventArgs e) => PositionSwitch((ToggleButton)sender, false);

    private void ChangeSetting(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleButton)sender;
        var enabled = toggle.IsChecked == true;
        var previouslyAnimated = MotionEnabled;
        if (toggle == MotionSwitch)
        {
            preferences = preferences with { Motion = enabled };
            CardScroll.AnimateWheel = enabled;
            UpdateHeroMotion();
        }
        else
        {
            preferences = preferences with { AnimatedBanners = enabled };
        }

        PositionSwitch(toggle, previouslyAnimated || MotionEnabled);
        SavePreferences();
    }

    private static void PositionSwitch(ToggleButton toggle, bool animate)
    {
        if (toggle.Template?.FindName("SwitchKnob", toggle) is not System.Windows.Shapes.Ellipse knob ||
            knob.RenderTransform is not TranslateTransform translation)
            return;
        if (translation.IsFrozen)
        {
            translation = translation.CloneCurrentValue();
            knob.RenderTransform = translation;
        }
        AnimateTo(translation, TranslateTransform.XProperty, toggle.IsChecked == true ? 24 : 0, animate);
    }

    private void UpdateHeroMotion()
    {
        UpdateGradientMotion();
        var play = initialized && IsLoaded && MotionEnabled && WindowState != WindowState.Minimized
            && page is Page.Library or Page.Installed;
        if (play == heroAnimating)
            return;
        heroAnimating = play;
        MoveSpark((TranslateTransform)OuterSpark.RenderTransform, "M 217,30 L 263,57 263,111 217,139 171,111 171,57 Z", 5.3, play);
        MoveSpark((TranslateTransform)InnerSpark.RenderTransform, "M 190,68 L 217,52 244,68 217,84 Z", 3.4, play);
        MoveSpark((TranslateTransform)FaceSpark.RenderTransform, "M 217,84 L 244,68 244,99 217,116 190,99 190,68 Z", 4.5, play);
    }

    private static void MoveSpark(TranslateTransform spark, string path, double seconds, bool play)
    {
        if (!play)
        {
            spark.BeginAnimation(TranslateTransform.XProperty, null);
            spark.BeginAnimation(TranslateTransform.YProperty, null);
            return;
        }

        var geometry = PathGeometry.CreateFromGeometry(Geometry.Parse(path));
        geometry.Freeze();
        var x = new DoubleAnimationUsingPath
        {
            PathGeometry = geometry, Source = PathAnimationSource.X, Duration = TimeSpan.FromSeconds(seconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(x, 60);
        var y = x.Clone();
        y.Source = PathAnimationSource.Y;
        spark.BeginAnimation(TranslateTransform.XProperty, x);
        spark.BeginAnimation(TranslateTransform.YProperty, y);
    }

    private void ChooseTheme(object sender, RoutedEventArgs e)
    {
        var name = (string)((Button)sender).DataContext;
        if (name == preferences.Theme)
            return;
        preferences = preferences with { Theme = name };
        ApplyTheme(name, animate: true);
        SavePreferences();
    }

    private void ApplyTheme(string theme, bool animate)
    {
        if (!Palettes.ContainsKey(theme) && !(theme == CustomThemeName && preferences.CustomTheme is not null))
            theme = "Simple";
        var palette = ThemePalette(theme);
        var custom = theme == CustomThemeName ? preferences.CustomTheme : null;

        for (var index = 0; index < PaletteKeys.Length; index++)
            Resources[PaletteKeys[index]] = new SolidColorBrush(ParseColor(palette[index]))
            {
                Opacity = custom is not null && index is 1 or 2 or 3 ? custom.Opacity : 1
            };

        isGlassTheme = theme == "Liquid glass";
        Resources["PrimaryFill"] = isGlassTheme ? GlassBrush("#18FFFFFF", "#08000000") : Resources["Accent"];
        Resources["PrimaryText"] = isGlassTheme ? Brushes.White : Resources["AccentInk"];
        Resources["PrimaryEdge"] = isGlassTheme ? GlassEdge(.56) : Resources["AccentSoft"];

        heroGlow?.BeginAnimation(GradientStop.OffsetProperty, null);
        var start = ParseColor(isGlassTheme ? "#00000000" : palette[^2]);
        var end = ParseColor(isGlassTheme ? "#14FFFFFF" : palette[^1]);
        heroGlow = new GradientStop(end, .72);
        LibraryHero.Background = new LinearGradientBrush(
            new GradientStopCollection { new(start, 0), heroGlow, new(start, 1) }, 13);
        UpdateGradientMotion();
        foreach (var button in ThemeOptions.Children.OfType<Button>())
        {
            button.Tag = Equals(button.DataContext, theme) ? "Active" : null;
            if (Equals(button.DataContext, CustomThemeName) && preferences.CustomTheme is { } design)
            {
                button.Visibility = Visibility.Visible;
                button.Background = new LinearGradientBrush(ParseColor(design.Tint), ParseColor(design.Accent), 18);
            }
        }
        UpdateThemeIcon(ParseColor(palette[5]), ParseColor(palette[7]));
        ApplyNativeBackdrop();
        MoveSelection(ThemeChoices, ThemeOutline, ActiveThemeButton(), animate);
    }

    private static LinearGradientBrush GlassBrush(string highlight, string shade)
    {
        var top = ParseColor(highlight);
        var bottom = ParseColor(shade);
        return new LinearGradientBrush(new GradientStopCollection
        {
            new(ParseColor("#33FFFFFF"), 0), new(top, .12),
            new(bottom, 1)
        }, 90);
    }

    private static LinearGradientBrush GlassEdge(double opacity) =>
        new(new GradientStopCollection
        {
            new(Color.FromScRgb((float)opacity, 1, 1, 1), 0),
            new(ParseColor("#36FFFFFF"), .25),
            new(ParseColor("#1DFFFFFF"), 1)
        }, 90);

    private void UpdateGradientMotion()
    {
        var play = IsLoaded && MotionEnabled && WindowState != WindowState.Minimized
            && page is Page.Library or Page.Installed;
        heroGlow?.BeginAnimation(GradientStop.OffsetProperty, play
            ? new DoubleAnimation(.72, .36, TimeSpan.FromSeconds(preferences.Theme == CustomThemeName ? preferences.CustomTheme?.Seconds ?? 16 : 16))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            }
            : null);
    }

    private Button ActiveThemeButton() => ThemeOptions.Children.OfType<Button>()
        .FirstOrDefault(button => Equals(button.Tag, "Active")) ?? (Button)ThemeOptions.Children[0];

    private void UpdateThemeIcon(Color background, Color foreground)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRoundedRectangle(new SolidColorBrush(background), null, new Rect(0, 0, 32, 32), 6, 6);
            context.PushTransform(new ScaleTransform(1.23, 1.23, 16, 16));
            context.DrawGeometry(new SolidColorBrush(foreground), null,
                Geometry.Parse("M 8,15 L 16,9 L 16,14 L 24,9 L 24,17 L 16,24 L 16,19 L 8,24 Z"));
            context.Pop();
        }
        // Give WPF separate small and large icon frames for Windows shell surfaces.
        int[] sizes = [16, 24, 32, 48, 64, 128, 256];
        var frames = new List<byte[]>();
        foreach (var size in sizes)
        {
            var dpi = size * 3d;
            var frame = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, dpi, dpi, PixelFormats.Pbgra32);
            frame.Render(drawing);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
            using var png = new System.IO.MemoryStream();
            encoder.Save(png);
            frames.Add(png.ToArray());
        }

        using var ico = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(ico);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Length);
        var offset = 6 + sizes.Length * 16;
        for (var index = 0; index < sizes.Length; index++)
        {
            var dimension = (byte)(sizes[index] % 256);
            writer.Write(dimension);
            writer.Write(dimension);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(frames[index].Length);
            writer.Write(offset);
            offset += frames[index].Length;
        }
        foreach (var frame in frames)
            writer.Write(frame);
        writer.Flush();
        ico.Position = 0;
        var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(ico,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        Icon = decoder.Frames[0];
    }

    private Button ActiveNavigationButton() => page switch
    {
        Page.Unofficial => UnofficialNav, Page.Installed => InstalledNav, Page.ModTools => ModToolsNav, Page.Setup => SetupNav, Page.Settings => SettingsNav, _ => LibraryNav
    };

    private Button[] CategoryButtons => [AllFilter, GameplayFilter, CosmeticsFilter, CaptureFilter, UtilitiesFilter, LibraryFilter];

    private void AlignSelections(object sender, SizeChangedEventArgs e) => AlignSelectionScope((FrameworkElement)sender);
    private void AlignVisibleSelection(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (initialized && (bool)e.NewValue)
            Dispatcher.InvokeAsync(() => AlignSelectionScope((FrameworkElement)sender),
                System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void AlignSelections()
    {
        AlignSelectionScope(NavigationChoices);
        AlignSelectionScope(FilterChoices);
        AlignSelectionScope(ThemeChoices);
    }

    private void AlignSelectionScope(FrameworkElement scope)
    {
        if (!initialized)
            return;
        if (scope == NavigationChoices)
            MoveSelection(scope, NavigationOutline, ActiveNavigationButton(), false);
        else if (scope == FilterChoices)
            MoveSelection(scope, FilterOutline, CategoryButtons.First(button => Equals(button.Content, category)), false);
        else if (scope == ThemeChoices)
            MoveSelection(scope, ThemeOutline, ActiveThemeButton(), false);
    }

    private void MoveSelection(FrameworkElement scope, Border outline, Button target, bool animate)
    {
        if (target.ActualWidth <= 0 || target.ActualHeight <= 0 || scope.ActualWidth <= 0)
            return;
        var position = target.TranslatePoint(new Point(0, 0), scope);
        if (outline.RenderTransform is not TranslateTransform translation)
        {
            translation = new TranslateTransform();
            outline.RenderTransform = translation;
        }

        animate &= outline.Width > 0 && MotionEnabled;
        AnimateTo(translation, TranslateTransform.XProperty, position.X, animate);
        AnimateTo(translation, TranslateTransform.YProperty, position.Y, animate);
        AnimateTo(outline, WidthProperty, target.ActualWidth, animate);
        AnimateTo(outline, HeightProperty, target.ActualHeight, animate);
    }

    private static void AnimateTo(DependencyObject item, DependencyProperty property, double value, bool animate)
    {
        var from = (double)item.GetValue(property);
        item.SetValue(property, value);
        var animation = animate && Math.Abs(from - value) > .1 ? Ease(from, value, 215) : null;
        if (item is Animatable transform)
            transform.BeginAnimation(property, animation);
        else if (item is UIElement element)
            element.BeginAnimation(property, animation);
    }

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static DoubleAnimation Ease(double from, double to, int milliseconds, int delay = 0) =>
        new(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };

    private void Reveal(UIElement element, double distance, int delay = 0, int duration = 240)
    {
        if (!MotionEnabled)
            return;
        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.YProperty, Ease(distance, 0, duration, delay));
        element.BeginAnimation(OpacityProperty, Ease(.27, 1, duration, delay));
    }

    private void ShowDownload(string title)
    {
        downloadDismissing = false;
        downloadCloseAnimation = null;
        displayedProgress = 0;
        DownloadBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        DownloadBar.Value = 0;
        DownloadTitle.Text = title;
        DownloadStage.Text = "Preparing game folder";
        DownloadPercent.Text = "0%";
        CancelDownloadButton.IsEnabled = true;
        DownloadOverlay.Visibility = Visibility.Visible;
        Reveal(DownloadCard, 14);
        CancelDownloadButton.Focus();
        if (MotionEnabled && DownloadOrbit.RenderTransform is RotateTransform rotate)
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(.95))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
        }
    }

    private void UpdateDownload(InstallProgress progress)
    {
        if (progress.Percent < displayedProgress)
            return;
        DownloadStage.Text = progress.Message;
        displayedProgress = progress.Percent;
        DownloadPercent.Text = $"{progress.Percent}%";
        var current = DownloadBar.Value;
        DownloadBar.Value = progress.Percent;
        if (MotionEnabled)
            DownloadBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, Ease(current, progress.Percent, 290));
    }

    private bool downloadDismissing;
    private Task? downloadCloseAnimation;

    private Task HideDownload() => downloadCloseAnimation ??= AnimateDownloadClose();

    private async Task AnimateDownloadClose()
    {
        if (DownloadOverlay.Visibility != Visibility.Visible)
            return;
        downloadDismissing = true;
        CancelDownloadButton.IsEnabled = false;
        if (MotionEnabled && !closeAfterOperation)
        {
            var fade = Ease(DownloadOverlay.Opacity, 0, 175);
            fade.FillBehavior = FillBehavior.HoldEnd;
            DownloadOverlay.BeginAnimation(OpacityProperty, fade);
            if (DownloadCard.RenderTransform is TranslateTransform slide)
                AnimateTo(slide, TranslateTransform.YProperty, 12, true);
            await Task.Delay(175);
        }
        DownloadOverlay.Visibility = Visibility.Collapsed;
        DownloadOverlay.BeginAnimation(OpacityProperty, null);
        if (DownloadOrbit.RenderTransform is RotateTransform rotate)
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void DismissDownloadPanel()
    {
        if (downloadDismissing)
            return;
        Status($"{DownloadStage.Text} · {DownloadPercent.Text}");
        _ = HideDownload();
    }

    private void DismissDownloadFromBackdrop(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, DownloadOverlay))
            return;
        e.Handled = true;
        DismissDownloadPanel();
    }

    private void CancelDownload(object sender, RoutedEventArgs e)
    {
        if (operation is null)
            return;
        CancelDownloadButton.IsEnabled = false;
        DownloadStage.Text = "Cancelling…";
        operation.Cancel();
    }

    private void RoundWindow(object? sender, EventArgs e)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;
        const uint windowCornerPreference = 33;
        var round = 2;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, windowCornerPreference, in round, sizeof(int));
        ApplyNativeBackdrop();
    }

    private void ApplyNativeBackdrop()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var available = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && !SystemParameters.HighContrast;
        var acrylic = false;
        if (handle != 0 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            const uint immersiveDarkMode = 20;
            const uint systemBackdropType = 38;
            var dark = 1;
            var backdrop = isGlassTheme && available ? 3 : 1;
            DwmSetWindowAttribute(handle, immersiveDarkMode, in dark, sizeof(int));
            acrylic = DwmSetWindowAttribute(handle, systemBackdropType, in backdrop, sizeof(int)) == 0 && backdrop == 3;
            if (WindowChrome.GetWindowChrome(this) is { } chrome)
                chrome.GlassFrameThickness = new Thickness(acrylic ? -1 : 0);
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
                target.BackgroundColor = acrylic ? Color.FromArgb(0, 0, 0, 0) : Colors.Black;
        }

        if (isGlassTheme)
        {
            Resources["Backdrop"] = acrylic ? Brushes.Transparent : Brushes.Black;
            Resources["Rail"] = GlassBrush("#09FFFFFF", "#10000000");
            Resources["Surface"] = GlassBrush("#0CFFFFFF", "#08000000");
            Resources["Raised"] = GlassBrush("#12FFFFFF", "#06000000");
            Resources["Selection"] = GlassBrush("#20FFFFFF", "#0AFFFFFF");
            Resources["Outline"] = GlassEdge(.42);
            Resources["SelectionOutline"] = GlassEdge(.64);
        }
        AppearanceNote.Text = acrylic || available && handle == 0
            ? "Windows Acrylic blurs the desktop while this window is active. Windows dims the backdrop when it loses focus."
            : "Liquid glass uses a black fallback when Windows acrylic is unavailable.";
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, in int value, uint size);
}

