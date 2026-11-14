using System.IO;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

namespace SimpleModManager;

internal sealed record PluginIdentity(string Id, string Name, string Version);
internal sealed record PluginInspection(string File, string? CatalogId, bool Disabled, string? AssemblyVersion,
    IReadOnlyList<PluginIdentity> Identities, IReadOnlyList<string> Indicators, string? Error);
internal sealed record PluginScan(PluginInspection[] Plugins, string IndicatorSource);

internal static class PluginInspector
{
    private const int MaxDllBytes = 32 * 1024 * 1024;
    private const int MaxFeedBytes = 128 * 1024;
    private static readonly string[] BuiltIn = ["israelauth", "israel auth", "israel_auth", "israel-auth"];
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public static async Task<PluginScan> Scan(GameInstall game, bool useCommunityFeed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var indicators = BuiltIn;
        var source = "Built-in name indicators";
        if (useCommunityFeed)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using var response = await Http.GetAsync("https://gtag.website/bannedurls",
                    HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var payload = new byte[MaxFeedBytes + 1];
                var read = await stream.ReadAtLeastAsync(payload, payload.Length, false, timeout.Token);
                if (read > MaxFeedBytes)
                    throw new InvalidDataException("The indicator feed exceeds the size limit.");
                using var json = JsonDocument.Parse(payload.AsMemory(0, read));
                var extra = new List<string>();
                foreach (var name in new[] { "bannedURLs", "bannedKeywords" })
                {
                    if (!json.RootElement.TryGetProperty(name, out var entries) || entries.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("Unexpected indicator feed format.");
                    foreach (var entry in entries.EnumerateArray())
                        if (entry.ValueKind == JsonValueKind.String && entry.GetString()?.Trim() is { Length: >= 5 and <= 300 } value &&
                            !value.Any(char.IsControl) && extra.Count < 256)
                            extra.Add(value);
                }
                indicators = BuiltIn.Concat(extra).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                source += $" and {indicators.Length - BuiltIn.Length} community indicators";
            }
            catch (Exception ex) when (!token.IsCancellationRequested &&
                ex is HttpRequestException or IOException or InvalidDataException or JsonException or InvalidOperationException or OperationCanceledException)
            {
                source += " (community feed unavailable)";
            }
        }

        var plugins = new List<PluginInspection>();
        var root = Installer.CheckedPath(game.Path, Installer.PluginsPath);
        foreach (var path in Installer.PluginFiles(root))
        {
            token.ThrowIfCancellationRequested();
            plugins.Add(Inspect(root, path, indicators, token));
        }
        return new(plugins.OrderBy(item => item.File, StringComparer.OrdinalIgnoreCase).ToArray(), source);
    }

    internal static PluginInspection Inspect(string root, string path, string[]? indicators, CancellationToken token)
    {
        var relative = Path.GetRelativePath(root, path);
        var disabled = path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        var catalogId = Catalog.Mods.FirstOrDefault(mod =>
            mod.FileName.Equals(Installer.PluginKey(path), StringComparison.OrdinalIgnoreCase))?.Id;
        var identities = new List<PluginIdentity>();
        string? assemblyVersion = null;
        var matches = new List<string>();
        string? error = null;
        try
        {
            using var file = File.OpenRead(Installer.CheckedPath(root, relative));
            if (file.Length > MaxDllBytes)
                throw new InvalidDataException("File exceeds the scan size limit.");
            if (indicators is not null)
            {
                var data = new byte[(int)file.Length];
                file.ReadExactly(data);
                var ascii = Encoding.Latin1.GetString(data);
                var wide = Encoding.Unicode.GetString(data);
                var wideShifted = data.Length > 1 ? Encoding.Unicode.GetString(data, 1, data.Length - 1) : "";
                foreach (var marker in indicators)
                {
                    token.ThrowIfCancellationRequested();
                    if (relative.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                        ascii.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                        wide.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                        wideShifted.Contains(marker, StringComparison.OrdinalIgnoreCase))
                        matches.Add(marker);
                }
                file.Position = 0;
            }
            using var pe = new PEReader(file);
            if (pe.HasMetadata)
            {
                var metadata = pe.GetMetadataReader();
                if (metadata.IsAssembly)
                    assemblyVersion = metadata.GetAssemblyDefinition().Version.ToString();
                foreach (var handle in metadata.CustomAttributes)
                {
                    var attribute = metadata.GetCustomAttribute(handle);
                    if (attribute.Parent.Kind != HandleKind.TypeDefinition || attribute.Constructor.Kind != HandleKind.MemberReference)
                        continue;
                    var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    if (constructor.Parent.Kind != HandleKind.TypeReference)
                        continue;
                    var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
                    if (!metadata.StringComparer.Equals(type.Namespace, "BepInEx") ||
                        !metadata.StringComparer.Equals(type.Name, "BepInPlugin"))
                        continue;
                    var values = metadata.GetBlobReader(attribute.Value);
                    if (values.ReadUInt16() == 1 && values.ReadSerializedString() is { Length: > 0 } id)
                        identities.Add(new(id, values.ReadSerializedString() ?? id, values.ReadSerializedString() ?? ""));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            error = ex.Message;
        }
        return new(relative, catalogId, disabled, assemblyVersion, identities, matches, error);
    }
}
