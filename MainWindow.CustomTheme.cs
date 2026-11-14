using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace SimpleModManager;

public partial class MainWindow
{
    private const string CustomThemeName = "Custom";

    private static CustomPalette DefaultCustomTheme => new("#DBF4A9", "#394B44", 16, .92);

    private static CustomPalette ValidateTheme(CustomPalette theme)
    {
        static string ColorText(string? input)
        {
            if (input is null || input.Length != 7 || input[0] != '#' ||
                !input.Skip(1).All(Uri.IsHexDigit))
                throw new InvalidDataException("Use six-digit hex colors such as #DBF4A9.");
            return input.ToUpperInvariant();
        }
        if (!double.IsFinite(theme.Seconds) || theme.Seconds is < 4 or > 60 ||
            !double.IsFinite(theme.Opacity) || theme.Opacity is < .60 or > 1)
            throw new InvalidDataException("Enter a gradient duration of 4–60 seconds and opacity of 0.60–1.00.");
        var accent = ColorText(theme.Accent);
        var color = ParseColor(accent);
        if (color.R * .2126 + color.G * .7152 + color.B * .0722 < 120)
            throw new InvalidDataException("Choose a brighter accent so primary buttons stay readable.");
        return new(accent, ColorText(theme.Tint), theme.Seconds, theme.Opacity);
    }

    private void ValidateSavedTheme()
    {
        if (preferences.CustomTheme is not { } theme)
            return;
        try { preferences = preferences with { CustomTheme = ValidateTheme(theme) }; }
        catch (InvalidDataException)
        {
            preferences = preferences with { CustomTheme = null,
                Theme = preferences.Theme == CustomThemeName ? "Simple" : preferences.Theme };
        }
    }
    private string[] ThemePalette(string name) => name == CustomThemeName && preferences.CustomTheme is { } custom
        ? Tinted(custom.Accent, custom.Tint) : Palettes.GetValueOrDefault(name, Palettes["Simple"]);

    private void OpenThemeEditor(object sender, RoutedEventArgs e)
    {
        var panel = NewToolsPanel("Custom theme", "Pick hex colors, gradient duration, and panel opacity.");
        var accent = AddInput("Accent color");
        var tint = AddInput("Gradient tint");
        var speed = AddInput("Gradient duration in seconds");
        var opacity = AddInput("Panel opacity");
        var preview = new Border { Height = 60, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 5, 0, 6) };
        panel.Body.Children.Add(preview);

        TextBox AddInput(string title)
        {
            var row = ToolsRow(panel.Body);
            row.Children.Add(new TextBlock { Text = title, Width = 205, VerticalAlignment = VerticalAlignment.Center });
            return ToolInput(row, title, width: 130);
        }

        CustomPalette Read()
        {
            if (!double.TryParse(speed.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ||
                !double.TryParse(opacity.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha))
                throw new InvalidDataException("Enter numeric values for gradient duration and panel opacity.");
            return ValidateTheme(new(accent.Text.Trim(), tint.Text.Trim(), duration, alpha));
        }

        void Fill(CustomPalette theme)
        {
            accent.Text = theme.Accent;
            tint.Text = theme.Tint;
            speed.Text = theme.Seconds.ToString(CultureInfo.InvariantCulture);
            opacity.Text = theme.Opacity.ToString(CultureInfo.InvariantCulture);
        }

        void Preview()
        {
            try
            {
                var theme = Read();
                preview.Background = new LinearGradientBrush(ParseColor(theme.Tint), ParseColor(theme.Accent), 18)
                { Opacity = theme.Opacity };
                panel.Report("");
            }
            catch (InvalidDataException ex) { panel.Report(ex.Message); }
        }

        var actions = ToolsRow(panel.Body);
        ToolButton(actions, "Apply theme", () =>
        {
            try
            {
                var custom = Read();
                preferences = preferences with { CustomTheme = custom, Theme = CustomThemeName };
                ApplyTheme(CustomThemeName, animate: true);
                SavePreferences();
                CloseTool(panel);
            }
            catch (InvalidDataException ex) { panel.Report(ex.Message); }
        }, true);
        ToolButton(actions, "Import JSON", () =>
        {
            var picker = new OpenFileDialog { Filter = "Theme JSON (*.json)|*.json" };
            if (picker.ShowDialog(this) != true) return;
            try { Fill(ValidateTheme(ModWorkspace.ImportTheme(picker.FileName))); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            { panel.Report(ex.Message); }
        });
        ToolButton(actions, "Export JSON", () =>
        {
            try
            {
                var theme = Read();
                var picker = new SaveFileDialog { Filter = "Theme JSON (*.json)|*.json", FileName = "SimpleModManager-theme.json" };
                if (picker.ShowDialog(this) == true) ModWorkspace.ExportTheme(picker.FileName, theme);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { panel.Report(ex.Message); }
        });
        foreach (var input in new[] { accent, tint, speed, opacity })
            input.TextChanged += (_, _) => Preview();
        Fill(preferences.CustomTheme ?? DefaultCustomTheme);
        ShowTool(panel);
    }
}

