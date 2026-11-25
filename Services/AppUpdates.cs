using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimpleModManager;

internal sealed record AppRelease(string Version, string Page, string AssetUrl, string FileName, string Sha256);

internal static partial class AppUpdates
{
    public const string DefaultRepository = "ItzGameDev/Simple-Mod-Manager";
    public const string DefaultVersionUrl = "https://raw.githubusercontent.com/ItzGameDev/Simple-Mod-Manager/main/version.txt";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };
    public static string CurrentVersion => typeof(AppUpdates).Assembly.GetName().Version?.ToString(3) ?? "3.0.0";

    public static string Repository(string input)
    {
        var value = input.Trim().TrimEnd('/');
        if (value.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            value = value["https://github.com/".Length..];
        if (!RepositoryPattern().IsMatch(value))
            throw new InvalidDataException("Enter a GitHub repository as owner/repository.");
        return value;
    }

    private static Version VersionNumber(string value)
    {
        if (!Version.TryParse(value.Trim().TrimStart('v', 'V'), out var version) || version.Build < 0 || version.Revision > 0)
            throw new InvalidDataException("Use a version such as 1.2.6 in version.txt and the release tag.");
        return new(version.Major, version.Minor, version.Build);
    }

    public static async Task<AppRelease?> Check(Preferences settings, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(settings.UpdateRepository))
            return null;
        var repository = Repository(settings.UpdateRepository);
        using var repo = await GetJson($"https://api.github.com/repos/{repository}", token);
        var branch = repo.RootElement.GetProperty("default_branch").GetString() ??
            throw new InvalidDataException("The GitHub repository has no default branch.");
        var versionUrl = string.IsNullOrWhiteSpace(settings.UpdateVersionUrl)
            ? $"https://raw.githubusercontent.com/{repository}/{Uri.EscapeDataString(branch)}/version.txt"
            : settings.UpdateVersionUrl.Trim();
        if (!Uri.TryCreate(versionUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "raw.githubusercontent.com" ||
            !uri.AbsolutePath.StartsWith("/" + repository + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("version.txt must be a raw.githubusercontent.com URL in the same repository.");
        var advertised = Encoding.UTF8.GetString(await GetBytes(versionUrl, 128, token)).Trim().TrimStart('\uFEFF');
        if (advertised.Length == 0)
            throw new InvalidDataException("version.txt is empty. Put the published app version in that file.");
        var advertisedNumber = VersionNumber(advertised);
        if (advertisedNumber <= VersionNumber(CurrentVersion))
            return null;
        using var release = await GetJson($"https://api.github.com/repos/{repository}/releases/latest", token);
        var root = release.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var name = OperatingSystem.IsLinux() ? "SimpleModManager-linux-x64.tar.gz" : "SimpleModManagerSetup.exe";
        var asset = root.GetProperty("assets").EnumerateArray().FirstOrDefault(item => item.GetProperty("name").GetString() == name);
        if (asset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException($"The release is missing {name}.");
        var download = AssetUrl(asset, repository);
        string? hash = null;
        if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String &&
            digest.GetString() is { } text && text.StartsWith("sha256:", StringComparison.Ordinal))
            hash = text["sha256:".Length..];
        if (hash is null || !HashPattern().IsMatch(hash))
        {
            var sums = root.GetProperty("assets").EnumerateArray().FirstOrDefault(item => item.GetProperty("name").GetString() == "SHA256SUMS.txt");
            if (sums.ValueKind == JsonValueKind.Undefined)
                throw new InvalidDataException("Publish SHA256SUMS.txt with the release so the downloaded update can be verified.");
            var content = Encoding.UTF8.GetString(await GetBytes(AssetUrl(sums, repository), 16384, token));
            hash = content.Split('\n').Select(line => line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length == 2 && parts[1].TrimStart('*') == name).Select(parts => parts[0]).SingleOrDefault();
        }
        if (hash is null || !HashPattern().IsMatch(hash))
            throw new InvalidDataException("The release has no valid SHA-256 checksum for this update.");
        return new(advertisedNumber.ToString(3), $"https://github.com/{repository}/releases/tag/{Uri.EscapeDataString(tag)}",
            download, name, hash);
    }

    private static string AssetUrl(JsonElement asset, string repository)
    {
        var value = asset.GetProperty("browser_download_url").GetString() ?? "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith("/" + repository + "/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The release asset URL does not match the configured repository.");
        return value;
    }

    public static async Task<string> Download(AppRelease release, Action<InstallProgress> progress, CancellationToken token)
    {
        var folder = Path.Combine(ModWorkspace.DataFolder, "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, release.FileName);
        using var request = Request(release.AssetUrl);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        try
        {
            await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[65536];
                var total = response.Content.Headers.ContentLength;
                var last = -1;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    if (output.Length + count > 256 * 1024 * 1024)
                        throw new InvalidDataException("The app update exceeds the 256 MB size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    sha.AppendData(buffer, 0, count);
                    var percent = total is > 0 ? (int)Math.Min(95, output.Length * 95 / total.Value) : 0;
                    if (percent != last) { progress(new("Downloading app update", percent)); last = percent; }
                }
                if (!Convert.ToHexString(sha.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The app update checksum did not match.");
            }
            progress(new("App update verified", 100));
            return file;
        }
        catch { File.Delete(file); throw; }
    }

    private static HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("SimpleModManager");
        return request;
    }

    private static async Task<JsonDocument> GetJson(string url, CancellationToken token) =>
        JsonDocument.Parse(await GetBytes(url, 2 * 1024 * 1024, token));

    private static async Task<byte[]> GetBytes(string url, int limit, CancellationToken token)
    {
        using var request = Request(url);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("The update response exceeds its size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryPattern();
    [GeneratedRegex(@"^[A-Fa-f0-9]{64}$")]
    private static partial Regex HashPattern();
}
