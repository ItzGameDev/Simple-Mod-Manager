using System.IO;
using System.Text.Json;

namespace SimpleModManager;

internal sealed record Preferences(string? GamePath = null, string Theme = "Simple",
    bool Motion = true, bool AnimatedBanners = true, bool TutorialSeen = false, bool CompactLibrary = false, string[]? Favorites = null,
    ModPreset[]? Profiles = null, ModCollection[]? Collections = null, HistoryEntry[]? History = null,
    DownloadRequest[]? Queue = null, int TutorialPosition = 0, CustomPalette? CustomTheme = null, bool? TutorialInProgress = null);

internal sealed class PreferencesStore(string? storagePath = null)
{
    private readonly string path = storagePath ?? Path.Combine(ModWorkspace.DataFolder, "preferences.json");

    public Preferences Read()
    {
        try
        {
            var preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? new();
            return preferences.Theme == "Charcoal" ? preferences with { Theme = "Simple" } : preferences;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Write(Preferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(preferences));
        File.Move(temporary, path, overwrite: true);
    }
}
