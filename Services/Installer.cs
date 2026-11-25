using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SimpleModManager;

internal enum ModState { Available, Installed, Disabled, External, ExternalDisabled, DifferentVersion, Conflict }
internal sealed record InstallProgress(string Message, int Percent);
internal sealed record LoaderState(string? Version, bool Enabled)
{
    public bool Installed => Version is not null;
}

internal static class Installer
{
    public static readonly string PluginsPath = Path.Combine("BepInEx", "plugins");
    private static readonly string ManagedPlugins = Path.Combine(PluginsPath, "SimpleModManager");
    private const string ConfigPath = "doorstop_config.ini";
    private const string DisabledSuffix = ".disabled";
    private const int StandardDownloadLimit = 32 * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly ConcurrentDictionary<string, (DateTime Modified, DateTime Created, string Hash)> LargePluginHashes = new();
    private static readonly EnumerationOptions PluginSearch = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
        MatchCasing = MatchCasing.CaseInsensitive
    };

    public static LoaderState ReadLoader(GameInstall game)
    {
        var core = Path.Combine(game.Path, "BepInEx", "core", "BepInEx.dll");
        var preloader = Path.Combine(game.Path, "BepInEx", "core", "BepInEx.Preloader.dll");
        var proxy = Path.Combine(game.Path, "winhttp.dll");
        var config = Path.Combine(game.Path, ConfigPath);
        if (!File.Exists(core) || !File.Exists(preloader) || !File.Exists(proxy) || !File.Exists(config))
            return new(null, false);

        try
        {
            var text = File.ReadAllText(config);
            if (!UsesBepInEx(text))
                return new(null, false);
            var version = FileVersionInfo.GetVersionInfo(core).FileVersion ?? "Unknown version";
            var enabled = !Regex.IsMatch(text, @"(?im)^\s*enabled\s*=\s*false\s*(?:[;#].*)?$");
            return new(version, enabled);
        }
        catch (IOException) { return new(null, false); }
    }

    public static Dictionary<string, ModState> ReadMods(GameInstall game)
    {
        var plugins = Path.Combine(game.Path, PluginsPath);
        var files = PluginFiles(plugins).ToLookup(PluginKey, StringComparer.OrdinalIgnoreCase);
        return Catalog.Mods.ToDictionary(mod => mod.Id, mod =>
        {
            var copies = files[mod.FileName].Take(2).ToArray();
            if (copies.Length == 0)
                return ModState.Available;
            if (copies.Length > 1)
                return ModState.Conflict;
            var disabled = IsDisabledFile(copies[0]);
            var managed = Path.Combine(game.Path, ManagedPlugins, mod.FileName) + (disabled ? DisabledSuffix : "");
            if (!copies[0].Equals(managed, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                return disabled ? ModState.ExternalDisabled : ModState.External;
            if (MatchesCatalogPlugin(managed, mod))
                return disabled ? ModState.Disabled : ModState.Installed;
            return disabled ? ModState.ExternalDisabled : ModState.DifferentVersion;
        });
    }

    internal static IEnumerable<string> PluginFiles(string plugins) => Directory.Exists(plugins)
        ? Directory.EnumerateFiles(plugins, "*.dll*", PluginSearch)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || IsDisabledFile(path))
        : [];

    public static string[] UnlistedPlugins(GameInstall game)
    {
        var root = Path.Combine(game.Path, PluginsPath);
        var known = Catalog.Mods.Select(mod => mod.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return PluginFiles(root).Where(path => !known.Contains(PluginKey(path)))
            .Select(path => Path.GetRelativePath(root, path)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static FileChange SetUnlistedPluginEnabled(GameInstall game, string relativeFile, bool enable)
    {
        RequireClosedGame(game);
        var plugins = CheckedPath(game.Path, PluginsPath);
        if (!UnlistedPlugins(game).Contains(relativeFile, StringComparer.OrdinalIgnoreCase) ||
            IsDisabledFile(relativeFile) != enable)
            throw new IOException("The local plugin changed. Refresh Installed mods and try again.");

        var source = CheckedPath(plugins, relativeFile);
        var destination = enable ? relativeFile[..^DisabledSuffix.Length] : relativeFile + DisabledSuffix;
        var hash = ModWorkspace.Hash(source);
        File.Move(source, CheckedPath(plugins, destination));
        return new(relativeFile, destination, hash);
    }

    private static bool IsDisabledFile(string path) => path.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase);
    internal static string PluginKey(string path)
    {
        var name = Path.GetFileName(path);
        if (IsDisabledFile(name))
            name = name[..^DisabledSuffix.Length];
        return Regex.IsMatch(name, @"^PokruksCameraMod(?:_[0-9]+(?:\.[0-9]+)*)?(?: \([0-9]+\))?\.dll$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? "PokruksCameraMod.dll" : name;
    }
    public static bool IsDisabled(ModState state) => state is ModState.Disabled or ModState.ExternalDisabled;
    public static bool IsEnabled(ModState state) => state is ModState.Installed or ModState.External or ModState.DifferentVersion;

    public static async Task InstallLoader(GameInstall game, Action<InstallProgress> progress, CancellationToken cancellation)
    {
        if (ReadLoader(game).Installed)
        {
            Directory.CreateDirectory(CheckedPath(game.Path, PluginsPath));
            return;
        }
        RequireClosedGame(game);
        var label = $"Downloading BepInEx {Catalog.BepInExVersion} from GitHub";
        progress(new(label, 3));
        using var bytes = new MemoryStream(await Download(Catalog.BepInEx,
            percent => progress(new(label, 3 + percent * 79 / 100)), cancellation));
        progress(new("Verifying and extracting the BepInEx archive", 86));
        using var zip = new ZipArchive(bytes);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries.Where(entry => entry.Name.Length > 0))
        {
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (!(relative is "winhttp.dll" or ConfigPath or ".doorstop_version" or "changelog.txt"
                || (relative.StartsWith(Path.Combine("BepInEx", "core") + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && relative == Path.Combine("BepInEx", "core", entry.Name))))
                throw new InvalidDataException($"Unexpected file in the BepInEx archive: {entry.FullName}");

            var destination = CheckedPath(game.Path, relative);
            if (File.Exists(destination) &&
                (relative == "changelog.txt" || relative == ConfigPath && UsesBepInEx(File.ReadAllText(destination))))
                continue;

            using var stream = entry.Open();
            using var content = new MemoryStream();
            stream.CopyTo(content);
            files.Add(relative, content.ToArray());
        }
        progress(new("Installing BepInEx in the Gorilla Tag folder", 94));
        Commit(game, files, cancellation);
        Directory.CreateDirectory(CheckedPath(game.Path, PluginsPath));
    }

    public static async Task<FileChange[]> InstallMod(GameInstall game, Mod mod, Action<InstallProgress> progress,
        CancellationToken cancellation, string? localFile = null, Action? startingDownload = null)
    {
        if (mod.Restricted)
            throw new InvalidOperationException("This catalog entry is unavailable for installation.");
        if (mod.SteamOnly && game.Store == Storefront.Meta)
            throw new InvalidOperationException($"{mod.Name} is published for Steam.");
        if (!ReadLoader(game).Installed)
            throw new InvalidOperationException("Set up BepInEx before installing a mod.");
        RequireClosedGame(game);
        var states = ReadMods(game);
        if (mod.DownloadSize is > StandardDownloadLimit)
            return await InstallLargePlugin(game, mod, states[mod.Id], progress, cancellation, startingDownload);
        var selected = new List<Mod>();
        if (mod.Dependency is { } id)
            selected.Add(Catalog.Mods.Single(item => item.Id == id));
        selected.Add(mod);

        var files = new Dictionary<string, byte[]>();
        for (var index = 0; index < selected.Count; index++)
        {
            var item = selected[index];
            if (states[item.Id] == ModState.Installed)
                continue;
            if (item.Id == mod.Dependency && mod.MinimumDependencyVersion is { } minimum
                && IsEnabled(states[item.Id]) && HasCompatibleDependency(game, item, minimum))
                continue;
            if (IsDisabled(states[item.Id]))
                throw new InvalidOperationException(item.DependencyOnly
                    ? $"Restore the .dll extension on {item.Name} in the plugins folder."
                    : $"Enable {item.Name} from Installed mods.");
            if (states[item.Id] != ModState.Available)
                throw new IOException($"A copy of {item.Name} already exists. Move it out of the plugins folder to install this release.");

            if (item.LocalFile || item.BundledResource is not null)
            {
                progress(new($"Verifying the {item.Name} DLL", 38));
                var bytes = await ReadProvidedAsset(item, localFile, cancellation);
                files.Add(Path.Combine(ManagedPlugins, item.FileName), bytes);
                continue;
            }

            var label = $"Downloading {item.Name} {item.DisplayVersion}";
            var start = 3 + index * 81 / selected.Count;
            var span = 81 / selected.Count;
            cancellation.ThrowIfCancellationRequested();
            startingDownload?.Invoke();
            progress(new(label, start));
            var data = await Download(item.Asset,
                percent => progress(new(label, start + percent * span / 100)), cancellation);
            files.Add(Path.Combine(ManagedPlugins, item.FileName), ExtractPlugin(item, data));
        }
        progress(new($"Installing {mod.Name}", 94));
        var committed = Commit(game, files, cancellation);
        return committed.Select(path => new FileChange("", Path.GetRelativePath(Path.Combine(game.Path, PluginsPath), path),
            Convert.ToHexString(SHA256.HashData(files[Path.GetRelativePath(game.Path, path)])))).ToArray();
    }

    private static async Task<FileChange[]> InstallLargePlugin(GameInstall game, Mod mod, ModState state,
        Action<InstallProgress> progress, CancellationToken cancellation, Action? startingDownload)
    {
        if (state == ModState.Installed)
            return [];
        if (state != ModState.Available)
            throw new IOException($"A copy of {mod.Name} already exists. Move it out of the plugins folder to install this release.");

        var expectedSize = mod.DownloadSize!.Value;
        var relative = Path.Combine(ManagedPlugins, mod.FileName);
        var destination = CheckedPath(game.Path, relative);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        cancellation.ThrowIfCancellationRequested();
        startingDownload?.Invoke();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            var label = $"Downloading {mod.Name} ({expectedSize / 1_000_000_000d:0.00} GB)";
            progress(new(label, 3));
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await DownloadTo(mod.Asset, output, percent => progress(new(label, 3 + percent * 90 / 100)),
                    cancellation, expectedSize);
                await output.FlushAsync(cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            progress(new($"Installing {mod.Name}", 96));
            RequireClosedGame(game);
            File.Move(temporary, CheckedPath(game.Path, relative));
            var installed = new FileInfo(destination);
            LargePluginHashes[destination] = (installed.LastWriteTimeUtc, installed.CreationTimeUtc, mod.Sha256);
            return [new("", Path.GetRelativePath(Path.Combine(game.Path, PluginsPath), destination), mod.Sha256.ToUpperInvariant())];
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static byte[] ExtractPlugin(Mod mod, byte[] data)
    {
        if (mod.ArchiveEntry is null)
            return data;
        using var input = new MemoryStream(data);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        var entries = zip.Entries.Where(entry => entry.FullName == mod.ArchiveEntry).ToArray();
        if (entries.Length != 1 || entries[0].Length is <= 0 or > 32 * 1024 * 1024)
            throw new InvalidDataException("The ZIP does not contain the expected plugin DLL.");
        // Extract the catalog's exact entry in memory so archive paths never become destinations.
        using var stream = entries[0].Open();
        var plugin = new byte[(int)entries[0].Length];
        stream.ReadExactly(plugin);
        if (stream.ReadByte() != -1 ||
            !Convert.ToHexString(SHA256.HashData(plugin)).Equals(mod.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The extracted DLL does not match the expected SHA-256.");
        return plugin;
    }

    public static FileChange RemoveMod(GameInstall game, Mod mod)
    {
        RequireClosedGame(game);
        var states = ReadMods(game);
        if (states[mod.Id] is not (ModState.Installed or ModState.Disabled))
            throw new InvalidOperationException("Only unmodified copies installed by this manager can be removed here.");
        var dependent = Catalog.Mods.FirstOrDefault(item =>
            item.Dependency == mod.Id && states[item.Id] != ModState.Available);
        if (dependent is not null)
            throw new InvalidOperationException($"Remove {dependent.Name} before removing {mod.Name}.");

        var name = mod.FileName + (IsDisabled(states[mod.Id]) ? DisabledSuffix : "");
        var gameFile = CheckedPath(game.Path, Path.Combine(ManagedPlugins, name));
        var relative = Path.GetRelativePath(Path.Combine(game.Path, PluginsPath), gameFile);
        var archive = Path.Combine(ModWorkspace.DataFolder, "plugin-backups", Guid.NewGuid().ToString("N") + ".bak");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        var hash = ModWorkspace.Hash(gameFile);
        File.Copy(gameFile, archive);
        try
        {
            if (!Matches(archive, hash) || !Matches(gameFile, hash))
                throw new IOException("The plugin changed during removal.");
            File.Delete(gameFile);
        }
        catch
        {
            File.Delete(archive);
            throw;
        }
        return new(relative, Path.GetFileName(archive), hash);
    }

    public static FileChange SetModEnabled(GameInstall game, Mod mod, bool enable)
    {
        if (enable && mod.Restricted)
            throw new InvalidOperationException("This catalog entry is unavailable for activation.");
        RequireClosedGame(game);
        var states = ReadMods(game);
        var state = states[mod.Id];
        if (enable ? !IsDisabled(state) : !IsEnabled(state))
            throw new InvalidOperationException($"Check for duplicate or missing copies of {mod.Name} in the plugins folder.");

        if (enable && mod.Dependency is { } id)
        {
            var dependency = Catalog.Mods.Single(item => item.Id == id);
            if (states[id] != ModState.Installed && !(IsEnabled(states[id]) &&
                mod.MinimumDependencyVersion is { } minimum && HasCompatibleDependency(game, dependency, minimum)))
                throw new InvalidOperationException($"Install or enable a compatible copy of {dependency.Name} first.");
        }

        if (!enable && Catalog.Mods.FirstOrDefault(item => item.Dependency == mod.Id &&
            (IsEnabled(states[item.Id]) || states[item.Id] == ModState.Conflict)) is { } dependent)
            throw new InvalidOperationException($"Disable {dependent.Name} before disabling {mod.Name}.");

        var plugins = CheckedPath(game.Path, PluginsPath);
        var copies = PluginFiles(plugins).Where(path =>
            IsDisabledFile(path) == enable && PluginKey(path).Equals(mod.FileName, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (copies.Length != 1)
            throw new IOException($"Expected one copy of {mod.Name}.");

        var source = CheckedPath(game.Path, Path.GetRelativePath(game.Path, copies[0]));
        var destination = enable ? source[..^DisabledSuffix.Length] : source + DisabledSuffix;
        var hash = ModWorkspace.Hash(source);
        File.Move(source, CheckedPath(game.Path, Path.GetRelativePath(game.Path, destination)));
        return new(Path.GetRelativePath(plugins, source), Path.GetRelativePath(plugins, destination), hash);
    }

    private static async Task<byte[]> ReadProvidedAsset(Mod mod, string? path, CancellationToken cancellation)
    {
        if (mod.BundledResource is null && (path is null || !Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Select the supplied {mod.Name} DLL.");

        using var input = mod.BundledResource is { } resource
            ? typeof(Installer).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidDataException($"The bundled {mod.Name} DLL is missing.")
            : File.OpenRead(path!);
        if (input.Length is <= 0 or > 32 * 1024 * 1024)
            throw new InvalidDataException("The DLL has an unexpected file size.");

        var bytes = new byte[(int)input.Length];
        await input.ReadExactlyAsync(bytes, cancellation);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(mod.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The DLL does not match the supplied {mod.Name} {mod.DisplayVersion} build.");
        return bytes;
    }

    private static async Task<byte[]> Download(ReleaseAsset asset, Action<int> report, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        await DownloadTo(asset, output, report, cancellation);
        return output.ToArray();
    }

    private static async Task DownloadTo(ReleaseAsset asset, Stream output, Action<int> report,
        CancellationToken cancellation, long? expectedSize = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        cancellation = deadline.Token;
        using var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength ?? expectedSize;
        var limit = expectedSize ?? StandardDownloadLimit;
        if (length > limit || (expectedSize is { } expected && length != expected))
            throw new InvalidDataException("The download has an unexpected file size.");
        using var input = await response.Content.ReadAsStreamAsync(cancellation);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[expectedSize.HasValue ? 128 * 1024 : 32768];
        long received = 0;
        var lastProgress = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
        {
            received += count;
            if (received > limit)
                throw new InvalidDataException("The download exceeded the release size limit.");
            if (expectedSize.HasValue)
                deadline.CancelAfter(TimeSpan.FromSeconds(60));
            await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
            sha256.AppendData(buffer, 0, count);
            if (length is > 0)
            {
                var percent = (int)Math.Min(100, received * 100 / length.Value);
                if (percent >= lastProgress + 3)
                {
                    report(percent);
                    lastProgress = percent;
                }
            }
        }
        if (expectedSize.HasValue && received != expectedSize.Value)
            throw new InvalidDataException("The download ended before the full release arrived.");
        if (!Convert.ToHexString(sha256.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded file did not match the published SHA-256.");
        report(100);
    }

    internal static bool HasCompatibleDependency(GameInstall game, Mod mod, string minimum)
    {
        var plugins = CheckedPath(game.Path, PluginsPath);
        var copies = Directory.EnumerateFiles(plugins, mod.FileName, PluginSearch).Take(2).ToArray();
        if (copies.Length != 1)
            return false;
        try
        {
            using var file = File.OpenRead(copies[0]);
            using var assembly = new PEReader(file);
            if (!assembly.HasMetadata)
                return false;
            var metadata = assembly.GetMetadataReader();
            foreach (var handle in metadata.CustomAttributes)
            {
                var attribute = metadata.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference)
                    continue;
                var member = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (member.Parent.Kind != HandleKind.TypeReference)
                    continue;
                var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
                if (!metadata.StringComparer.Equals(type.Namespace, "BepInEx") ||
                    !metadata.StringComparer.Equals(type.Name, "BepInPlugin"))
                    continue;
                var values = metadata.GetBlobReader(attribute.Value);
                if (values.ReadUInt16() != 1 || values.ReadSerializedString() != mod.PluginId)
                    continue;
                values.ReadSerializedString(); // Plugin display name
                if (Version.TryParse(values.ReadSerializedString(), out var version) && version >= Version.Parse(minimum))
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException) { }
        return false;
    }

    private static List<string> Commit(GameInstall game, Dictionary<string, byte[]> files, CancellationToken cancellation)
    {
        RequireClosedGame(game);
        var pending = new List<(string Destination, byte[] Data)>();
        foreach (var (relative, data) in files)
        {
            var path = CheckedPath(game.Path, relative);
            if (File.Exists(path))
            {
                if (!Matches(path, Convert.ToHexString(SHA256.HashData(data))))
                    throw new IOException($"An existing file differs from the release: {relative}. Back up the existing installation before retrying.");
                continue;
            }
            pending.Add((path, data));
        }

        var staged = new List<(string Temporary, string Destination)>();
        var committed = new List<string>();
        try
        {
            foreach (var (destination, data) in pending)
            {
                cancellation.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                staged.Add((temporary, destination));
                File.WriteAllBytes(temporary, data);
            }

            cancellation.ThrowIfCancellationRequested();
            RequireClosedGame(game);
            foreach (var (temporary, destination) in staged)
            {
                File.Move(temporary, destination);
                committed.Add(destination);
            }
        }
        catch
        {
            foreach (var path in committed)
                File.Delete(path);
            throw;
        }
        finally
        {
            foreach (var (temporary, _) in staged)
                File.Delete(temporary);
        }
        return committed;
    }

    internal static string CheckedPath(string root, string relative)
    {
        var current = root;
        foreach (var segment in relative.Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.Contains(':'))
                throw new InvalidDataException($"Invalid installation path: {relative}");
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"The selected game folder contains a linked file or directory: {relative}");
        }
        return current;
    }

    private static bool MatchesCatalogPlugin(string path, Mod mod)
    {
        if (mod.DownloadSize is not > StandardDownloadLimit)
            return Matches(path, mod.Sha256);
        var file = new FileInfo(path);
        if (file.Length != mod.DownloadSize)
            return false;
        var stamp = (file.LastWriteTimeUtc, file.CreationTimeUtc);
        if (LargePluginHashes.TryGetValue(path, out var verified) &&
            (verified.Modified, verified.Created) == stamp && verified.Hash == mod.Sha256)
            return true;
        if (!Matches(path, mod.Sha256))
            return false;
        LargePluginHashes[path] = (stamp.LastWriteTimeUtc, stamp.CreationTimeUtc, mod.Sha256);
        return true;
    }

    private static bool Matches(string path, string hash)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    private static bool UsesBepInEx(string config) => Regex.IsMatch(config,
        @"(?im)^\s*target_assembly\s*=\s*BepInEx[\\/]core[\\/]BepInEx\.Preloader\.dll\s*(?:[;#].*)?$");

    internal static void RequireClosedGame(GameInstall game)
    {
        if (GameDiscovery.Open(game.Path, game.Store) is null)
            throw new IOException("The Gorilla Tag installation could not be verified. Choose the game again.");

        foreach (var name in OperatingSystem.IsLinux()
            ? new[] { "Gorilla Tag", "GorillaTag", "Gorilla Tag.exe", "GorillaTag.exe" }
            : new[] { "Gorilla Tag", "GorillaTag" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0)
                    throw new InvalidOperationException("Close Gorilla Tag before changing its files.");
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }
    }
}

