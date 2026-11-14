using System.IO;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SimpleModManager;

internal enum Storefront { Steam, Meta, Custom }

internal sealed record GameInstall(string Path, Storefront Store)
{
    public string Label => Store switch
    {
        Storefront.Steam => "Steam",
        Storefront.Meta => "Meta Horizon PC",
        _ => "PC installation"
    };
}

internal static partial class GameDiscovery
{
    private const string MetaSlug = "another-axiom-gorilla-tag";

    public static IReadOnlyList<GameInstall> Find()
    {
        var found = new Dictionary<string, GameInstall>(StringComparer.OrdinalIgnoreCase);
        void Add(string directory, Storefront store)
        {
            if (Open(directory, store) is { } game)
                found.TryAdd(game.Path, game);
        }

        var programRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase);

        var steamRoots = programRoots.Select(path => Path.Combine(path, "Steam"))
            .Concat(new[]
            {
                ReadRegistry(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                ReadRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
            }.OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var steam in steamRoots)
        {
            Add(Path.Combine(steam, "steamapps", "common", "Gorilla Tag"), Storefront.Steam);
            var libraries = ReadText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"));
            foreach (Match match in LibraryPath().Matches(libraries))
            {
                var path = match.Groups[1].Value.Replace(@"\\", @"\");
                if (Path.IsPathFullyQualified(path))
                    Add(Path.Combine(path, "steamapps", "common", "Gorilla Tag"), Storefront.Steam);
            }
        }

        foreach (var root in programRoots)
            Add(Path.Combine(root, "Oculus", "Software", "Software", MetaSlug), Storefront.Meta);

        var metaBase = ReadRegistry(Registry.LocalMachine, @"SOFTWARE\Oculus VR, LLC\Oculus", "Base");
        if (!string.IsNullOrWhiteSpace(metaBase))
            Add(Path.Combine(metaBase, "Software", "Software", MetaSlug), Storefront.Meta);

        var metaLibraries = ReadRegistry(Registry.CurrentUser,
            @"Software\Oculus VR, LLC\Oculus\Libraries", "DefaultLibrary");
        using var libraryKey = Registry.CurrentUser.OpenSubKey(@"Software\Oculus VR, LLC\Oculus\Libraries");
        var libraryIds = libraryKey?.GetSubKeyNames() ?? [];
        foreach (var id in libraryIds.OrderByDescending(id => id.Equals(metaLibraries, StringComparison.OrdinalIgnoreCase)))
        {
            var path = ReadRegistry(Registry.CurrentUser, $@"Software\Oculus VR, LLC\Oculus\Libraries\{id}", "Path");
            if (!string.IsNullOrWhiteSpace(path))
                Add(Path.Combine(path, "Software", MetaSlug), Storefront.Meta);
        }

        return found.Values.ToArray();
    }

    public static GameInstall? Open(string directory, Storefront store = Storefront.Custom)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            foreach (var name in new[] { "Gorilla Tag", "GorillaTag" })
            {
                var executable = Path.Combine(root, name + ".exe");
                var data = Path.Combine(root, name + "_Data", "Managed", "Assembly-CSharp.dll");
                if (!File.Exists(executable) || !File.Exists(data))
                    continue;

                using var file = File.OpenRead(executable);
                using var pe = new PEReader(file);
                if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64)
                    continue;

                if (store == Storefront.Custom)
                {
                    if (Path.GetFileName(root).Equals(MetaSlug, StringComparison.OrdinalIgnoreCase))
                        store = Storefront.Meta;
                    else if (Directory.GetParent(root)?.Parent?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) == true)
                        store = Storefront.Steam;
                }
                return new(root, store);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or BadImageFormatException) { }
        return null;
    }

    private static string? ReadRegistry(RegistryKey root, string path, string value)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(value) as string;
    }

    private static string ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPath();
}
