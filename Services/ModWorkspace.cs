using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SimpleModManager;

internal sealed record ModPreset(string Name, string GamePath, string[] EnabledFiles, string[]? KnownFiles = null);
internal sealed record ModCollection(string Name, string[] ModIds);
internal sealed record DownloadRequest(string GamePath, string ModId, string State, string? Error = null);
internal sealed record FileChange(string Before, string After, string Hash);
internal sealed record HistoryEntry(DateTimeOffset Time, string GamePath, string Action, FileChange[] Changes, bool Undone = false);
internal sealed record CustomPalette(string Accent, string Tint, double Seconds, double Opacity);

internal static class ModWorkspace
{
    internal static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "SimpleModManager");
    private const string DisabledSuffix = ".disabled";
    private static readonly string ConfigPath = Path.Combine("BepInEx", "config");
    private const long ConfigLimit = 32 * 1024 * 1024;

    public static string ActiveName(string relative) => relative.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
        ? relative[..^DisabledSuffix.Length] : relative;

    public static PluginInspection[] Inventory(GameInstall game, CancellationToken token)
    {
        var root = Installer.CheckedPath(game.Path, Installer.PluginsPath);
        return Installer.PluginFiles(root).Select(path =>
        {
            token.ThrowIfCancellationRequested();
            return PluginInspector.Inspect(root, path, null, token);
        }).OrderBy(item => item.File, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    public static FileChange[] PlanToggles(GameInstall game, PluginInspection[] inventory,
        IReadOnlyDictionary<string, bool> desired, CancellationToken token)
    {
        var root = Installer.CheckedPath(game.Path, Installer.PluginsPath);
        var byCatalog = inventory.Where(item => item.CatalogId is not null).ToLookup(item => item.CatalogId!);
        var duplicates = inventory.SelectMany(item => item.Identities.Select(identity => (item, identity)))
            .GroupBy(pair => pair.identity.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(pair => pair.item.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changes = new List<FileChange>();
        foreach (var item in inventory)
        {
            token.ThrowIfCancellationRequested();
            var activeName = ActiveName(item.File);
            if (!desired.TryGetValue(activeName, out var enable) || enable != item.Disabled)
                continue;
            var mod = Catalog.Mods.FirstOrDefault(mod => mod.Id == item.CatalogId);
            if (enable && (mod is { Restricted: true } || mod?.Usage == UsageTag.Illegal && mod.Id != "walk-simulator"))
                throw new InvalidOperationException($"{mod!.Name} is unavailable for activation from this panel.");
            if (enable && mod is { SteamOnly: true } && game.Store == Storefront.Meta)
                throw new InvalidOperationException($"{mod.Name} is published for Steam.");
            if (enable && (item.Error is not null || item.Identities.Any(identity => duplicates.Contains(identity.Id))))
                throw new InvalidOperationException($"Resolve unreadable or duplicate plugin IDs before enabling {item.File}.");
            if (enable && mod?.Dependency is { } dependency)
            {
                var required = Catalog.Mods.Single(entry => entry.Id == dependency);
                var candidates = byCatalog[dependency].ToArray();
                var candidate = candidates.Length == 1 ? candidates[0] : null;
                var pinned = candidate is not null && Hash(Installer.CheckedPath(root, candidate.File))
                    .Equals(required.Sha256, StringComparison.OrdinalIgnoreCase);
                var minimumMet = candidate is not null && mod.MinimumDependencyVersion is { } minimum &&
                    Version.TryParse(minimum, out var needed) && candidate.Identities.Any(identity =>
                        identity.Id == required.PluginId && Version.TryParse(identity.Version, out var version) && version >= needed);
                if (candidate is null || !desired.GetValueOrDefault(ActiveName(candidate.File), !candidate.Disabled) ||
                    !(pinned || minimumMet))
                    throw new InvalidOperationException($"Enable one compatible copy of {required.Name} first.");
            }
            if (!enable && mod is not null && Catalog.Mods.Where(entry => entry.Dependency == mod.Id)
                .SelectMany(entry => byCatalog[entry.Id]).Any(candidate =>
                    desired.GetValueOrDefault(ActiveName(candidate.File), !candidate.Disabled)))
                throw new InvalidOperationException($"Disable mods that depend on {mod.Name} first.");
            changes.Add(new(item.File, enable ? activeName : activeName + DisabledSuffix,
                Hash(Installer.CheckedPath(root, item.File))));
        }
        return changes.ToArray();
    }

    public static void MovePlugins(GameInstall game, FileChange[] changes, CancellationToken token)
    {
        Installer.RequireClosedGame(game);
        var root = Installer.CheckedPath(game.Path, Installer.PluginsPath);
        var existing = Installer.PluginFiles(root).Select(p => Path.GetRelativePath(root, p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var planned = new List<(string Before, string After)>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            token.ThrowIfCancellationRequested();
            if (!existing.Remove(change.Before) || !destinations.Add(change.After) || existing.Contains(change.After) ||
                !ActiveName(change.Before).Equals(ActiveName(change.After), StringComparison.OrdinalIgnoreCase) ||
                change.Before.Equals(change.After, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The plugins have changed since this action was previewed.");
            var before = Installer.CheckedPath(root, change.Before);
            var after = Installer.CheckedPath(root, change.After);
            if (File.Exists(after) || !Hash(before).Equals(change.Hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"The plugin changed: {change.Before}");
            planned.Add((before, after));
        }

        var completed = new Stack<(string Before, string After)>();
        try
        {
            foreach (var pair in planned)
            {
                token.ThrowIfCancellationRequested();
                File.Move(pair.Before, pair.After);
                completed.Push(pair);
            }
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var (before, after) in completed)
                try { File.Move(after, before); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rollbackErrors.Add(ex); }
            if (rollbackErrors.Count > 0)
                throw new IOException("Some moves could not be rolled back. Refresh the plugin inventory before retrying.",
                    new AggregateException([error, .. rollbackErrors]));
            throw;
        }
    }

    public static void UndoInstall(GameInstall game, FileChange[] changes, CancellationToken token)
    {
        Installer.RequireClosedGame(game);
        var root = Installer.CheckedPath(game.Path, Installer.PluginsPath);
        var states = Installer.ReadMods(game);
        var names = changes.Select(change => Installer.PluginKey(change.After)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedIds = Catalog.Mods.Where(item => names.Contains(item.FileName)).Select(item => item.Id).ToHashSet();
        if (Catalog.Mods.Any(item => !removedIds.Contains(item.Id) && item.Dependency is { } dependency &&
            removedIds.Contains(dependency) && states[item.Id] != ModState.Available))
            throw new InvalidOperationException("Another installed mod now uses one of these files.");
        var staged = new Stack<(string Path, string Temporary)>();
        try
        {
            foreach (var change in changes)
            {
                token.ThrowIfCancellationRequested();
                var path = Installer.CheckedPath(root, change.After);
                if (!Hash(path).Equals(change.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("An installed file changed. Undo stopped before removing it.");
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".undo";
                File.Move(path, temp);
                staged.Push((path, temp));
            }
        }
        catch
        {
            foreach (var (path, temp) in staged)
                File.Move(temp, path);
            throw;
        }
        foreach (var (_, temp) in staged)
            File.Delete(temp);
    }

    public static void UndoRemoval(GameInstall game, FileChange change)
    {
        Installer.RequireClosedGame(game);
        var mod = Catalog.Mods.FirstOrDefault(item => item.FileName.Equals(Installer.PluginKey(change.Before), StringComparison.OrdinalIgnoreCase));
        if (mod is null || mod.Restricted || mod.Usage == UsageTag.Illegal && mod.Id != "walk-simulator" ||
            Installer.ReadMods(game)[mod.Id] != ModState.Available)
            throw new InvalidOperationException("The removed mod is unavailable or another copy is now installed.");
        if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(change.After), "N", out _) || Path.GetExtension(change.After) != ".bak")
            throw new InvalidDataException("Invalid plugin backup name.");
        var archive = Installer.CheckedPath(Path.Combine(DataFolder, "plugin-backups"), change.After);
        var root = Installer.CheckedPath(game.Path, Installer.PluginsPath);
        var destination = Installer.CheckedPath(root, change.Before);
        var data = File.ReadAllBytes(archive);
        if (!Convert.ToHexString(SHA256.HashData(data)).Equals(change.Hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The plugin backup changed.");
        var staged = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            File.WriteAllBytes(staged, data);
            File.Move(staged, destination);
        }
        finally { File.Delete(staged); }
    }
    public static string[] Configs(GameInstall game)
    {
        var root = Installer.CheckedPath(game.Path, ConfigPath);
        return Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.cfg", new EnumerationOptions
        {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true
        }).Select(path => Path.GetRelativePath(root, path)).Order(StringComparer.OrdinalIgnoreCase).ToArray() : [];
    }

    private static string ConfigFile(GameInstall game, string relative)
    {
        if (!relative.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only BepInEx .cfg files can be edited or restored.");
        return Installer.CheckedPath(game.Path, Path.Combine(ConfigPath, relative));
    }

    public static string ReadConfig(GameInstall game, string relative)
    {
        var path = ConfigFile(game, relative);
        if (new FileInfo(path).Length > 1024 * 1024)
            throw new IOException("This config exceeds the editor's 1 MB limit.");
        return File.ReadAllText(path);
    }

    public static void WriteConfig(GameInstall game, string relative, string previous, string? next)
    {
        Installer.RequireClosedGame(game);
        var path = ConfigFile(game, relative);
        if (ReadConfig(game, relative) != previous)
            throw new IOException("This config changed on disk. Reopen it before editing.");
        if (next is { Length: > 1024 * 1024 })
            throw new IOException("The edited config exceeds the editor's limit.");
        var backup = Path.Combine(DataFolder, "config-backups", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        BackupConfigs(game, backup, [relative]);
        if (next is null)
            File.Delete(path);
        else
            ReplaceFile(path, System.Text.Encoding.UTF8.GetBytes(next));
    }

    private static void ReplaceFile(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(staged, data);
            File.Move(staged, path, overwrite: true);
        }
        finally { File.Delete(staged); }
    }

    public static void BackupConfigs(GameInstall game, string path, string[]? files = null)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                long total = 0;
                foreach (var relative in files ?? Configs(game))
                {
                    var source = ConfigFile(game, relative);
                    using var input = File.OpenRead(source);
                    total += input.Length;
                    if (total > ConfigLimit)
                        throw new IOException("Config backup exceeds the 32 MB limit.");
                    using var destination = zip.CreateEntry(relative.Replace('\\', '/')).Open();
                    input.CopyTo(destination);
                }
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public static void RestoreConfigs(GameInstall game, string file)
    {
        Installer.RequireClosedGame(game);
        using var zip = ZipFile.OpenRead(file);
        if (zip.Entries.Count > 512)
            throw new InvalidDataException("Too many entries in the config archive.");
        var data = new Dictionary<string, (byte[] Next, byte[]? Previous)>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var path = ConfigFile(game, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            total += entry.Length + (File.Exists(path) ? new FileInfo(path).Length : 0);
            if (total > ConfigLimit || data.ContainsKey(path))
                throw new InvalidDataException("This archive is too large or contains duplicate config paths.");
            using var input = entry.Open();
            var bytes = new byte[(int)entry.Length];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1)
                throw new InvalidDataException("Archive entry length does not match its header.");
            data.Add(path, (bytes, File.Exists(path) ? File.ReadAllBytes(path) : null));
        }

        var written = new Stack<string>();
        try
        {
            foreach (var (path, version) in data)
            {
                ReplaceFile(path, version.Next);
                written.Push(path);
            }
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var path in written)
                try
                {
                    if (data[path].Previous is { } previous) ReplaceFile(path, previous);
                    else File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rollbackErrors.Add(ex); }
            if (rollbackErrors.Count > 0)
                throw new IOException("Config restore was interrupted and some original files could not be restored.",
                    new AggregateException([error, .. rollbackErrors]));
            throw;
        }
    }

    public static void ExportTheme(string path, CustomPalette palette) =>
        ReplaceFile(path, JsonSerializer.SerializeToUtf8Bytes(palette, new JsonSerializerOptions { WriteIndented = true }));

    public static CustomPalette ImportTheme(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > 8192)
            throw new InvalidDataException("This theme file exceeds 8 KB.");
        return JsonSerializer.Deserialize<CustomPalette>(input) ?? throw new InvalidDataException("Theme file is empty.");
    }
}


