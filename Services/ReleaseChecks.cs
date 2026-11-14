using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimpleModManager;

internal sealed record ReleaseCheck(Mod Mod, string Installed, string Published, string Status, string Notes, string? Page);

internal static partial class ReleaseChecks
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static async Task<ReleaseCheck[]> Check(PluginInspection[] inventory, CancellationToken token)
    {
        var result = new List<ReleaseCheck>();
        foreach (var mod in Catalog.Mods.Where(entry => entry.Usage is not (UsageTag.Illegal or UsageTag.Restricted) &&
                     RepositoryPattern().IsMatch(entry.Repository)))
        {
            var installed = inventory.FirstOrDefault(item => item.CatalogId == mod.Id);
            var local = installed?.Identities.FirstOrDefault()?.Version ?? installed?.AssemblyVersion;
            var published = mod.DisplayVersion;
            var status = "Pinned catalog version";
            var notes = "";
            string? page = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{mod.Repository}/releases/latest");
                request.Headers.UserAgent.ParseAdd("SimpleModManager");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await Http.SendAsync(request, token);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    result.Add(new(mod, local ?? "—", published, "No stable release reported", notes, mod.Source));
                    continue;
                }
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                published = json.RootElement.GetProperty("tag_name").GetString() ?? published;
                notes = json.RootElement.GetProperty("body").GetString() ?? "";
                if (notes.Length > 12000)
                    notes = notes[..12000];
                page = $"https://github.com/{mod.Repository}/releases/tag/{Uri.EscapeDataString(published)}";
                status = Classify(local ?? mod.Version, published, installed is not null);
            }
            catch (Exception ex) when (!token.IsCancellationRequested &&
                ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException or KeyNotFoundException)
            {
                status = "Release check unavailable";
            }
            result.Add(new(mod, local ?? (installed is null ? "—" : "Unknown"), published, status, notes, page));
        }
        return result.ToArray();
    }

    private static string Classify(string current, string published, bool installed)
    {
        var a = VersionPattern().Match(current.TrimStart('v', 'V'));
        var b = VersionPattern().Match(published.TrimStart('v', 'V'));
        if (!a.Success || !b.Success || !Version.TryParse(a.Value, out var first) || !Version.TryParse(b.Value, out var second))
            return "Compare release tags";
        if (second > first || second == first && current.Contains('-') && !published.Contains('-'))
            return installed ? "Newer release available" : "Newer than catalog";
        return installed ? "No newer stable version" : "No newer catalog release";
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryPattern();
    [GeneratedRegex(@"^\d+(?:\.\d+){1,3}")]
    private static partial Regex VersionPattern();
}
