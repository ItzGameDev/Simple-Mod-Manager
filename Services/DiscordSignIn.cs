using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace SimpleModManager;

internal sealed class DiscordSignIn
{
    private const string ClientId = "1573769040701423626";
    private const string Redirect = "http://127.0.0.1:42813/discord/callback/";
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });

    public string Status { get; private set; } = "Signed out";
    public string ButtonLabel => pending is not null ? "Cancel sign-in" : DisplayName is not null ? "Sign out" : "Sign in with Discord";
    public string? DisplayName { get; private set; }
    public Task<byte[]?>? Avatar { get; private set; }
    public bool IsPending => pending is not null;

    private CancellationTokenSource? pending;
    public void Cancel() => pending?.Cancel();

    public async Task ToggleAsync(Action render)
    {
        if (pending is not null)
        {
            pending.Cancel();
            return;
        }

        if (DisplayName is not null)
        {
            DisplayName = null;
            Avatar = null;
            Status = "Signed out";
            render();
            return;
        }

        using var attempt = new CancellationTokenSource();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        pending = attempt;
        Status = "Waiting for Discord in your browser…";
        render();

        try
        {
            var account = await AuthorizeAsync(timeout.Token);
            DisplayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Username : account.DisplayName;
            Avatar = DownloadAvatarAsync(account);
            Status = string.IsNullOrWhiteSpace(account.DisplayName)
                ? $"Signed in as @{account.Username}"
                : $"Signed in as {account.DisplayName} (@{account.Username})";
        }
        catch (OperationCanceledException)
        {
            Status = attempt.IsCancellationRequested ? "Sign-in cancelled." : "Sign-in timed out. Try again.";
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
        }
        catch (HttpListenerException)
        {
            Status = "Couldn’t start the local sign-in listener. Close any other copy of the manager and try again.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Status = "Couldn’t open your default browser.";
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            Status = "Couldn’t verify your Discord account. Try again.";
        }
        finally
        {
            pending = null;
            render();
        }
    }

    private static async Task<Profile> AuthorizeAsync(CancellationToken cancellation)
    {
        string state = RandomToken();
        string verifier = RandomToken();
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string authUrl = "https://discord.com/oauth2/authorize" +
            $"?client_id={ClientId}&response_type=code&scope=identify&redirect_uri={Uri.EscapeDataString(Redirect)}" +
            $"&state={state}&code_challenge={challenge}&code_challenge_method=S256";

        using var listener = new HttpListener();
        listener.Prefixes.Add(Redirect);
        listener.Start();
        using var browser = Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        while (true)
        {
            var callback = await listener.GetContextAsync().WaitAsync(cancellation).ConfigureAwait(false);
            var query = callback.Request.QueryString;
            if (callback.Request.HttpMethod != "GET" || callback.Request.Url?.AbsolutePath != "/discord/callback/" ||
                query.GetValues("state") is not [var callbackState] || callbackState != state)
            {
                Reply(callback, 400, "Invalid sign-in request. Return to Simple Mod Manager.");
                continue;
            }

            try
            {
                if (query.GetValues("code") is not [var code] || string.IsNullOrWhiteSpace(code))
                    throw new InvalidOperationException("Discord authorization was declined or cancelled.");

                var account = await GetProfileAsync(code, verifier, cancellation).ConfigureAwait(false);
                Reply(callback, 200, "Signed in. You can close this tab and return to Simple Mod Manager.");
                return account;
            }
            catch
            {
                Reply(callback, 400, "Sign-in did not finish. Return to Simple Mod Manager for details.");
                throw;
            }
        }
    }

    private static async Task<Profile> GetProfileAsync(string code, string verifier, CancellationToken cancellation)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = Redirect,
            ["code_verifier"] = verifier
        });
        using var exchange = await Http.PostAsync("https://discord.com/api/v10/oauth2/token", form, cancellation).ConfigureAwait(false);
        if (!exchange.IsSuccessStatusCode)
            throw new InvalidOperationException("Discord couldn’t complete sign-in. Try again. If it keeps failing, check the app’s OAuth2 settings.");
        var token = await exchange.Content.ReadFromJsonAsync<Token>(cancellation).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token?.AccessToken))
            throw new InvalidOperationException("Discord didn’t return a sign-in token.");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/users/@me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await Http.SendAsync(request, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var account = await response.Content.ReadFromJsonAsync<Profile>(cancellation).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(account?.Id) || string.IsNullOrWhiteSpace(account.Username)
            ? throw new InvalidOperationException("Discord didn’t return a complete profile.")
            : account;
    }

    private static void Reply(HttpListenerContext callback, int status, string message)
    {
        try
        {
            using var response = callback.Response;
            response.StatusCode = status;
            response.ContentType = "text/plain; charset=utf-8";
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["Referrer-Policy"] = "no-referrer";
            byte[] body = Encoding.UTF8.GetBytes(message);
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body);
        }
        catch (Exception ex) when (ex is HttpListenerException or System.IO.IOException)
        {
            // The browser can close the tab before the response arrives.
        }
    }

    private static async Task<byte[]?> DownloadAvatarAsync(Profile account)
    {
        if (!ulong.TryParse(account.Id, out ulong id))
            return null;
        const string cdn = "https://cdn.discordapp.com";
        var index = int.TryParse(account.Discriminator, out int legacy) && legacy > 0 ? legacy % 5 : (int)((id >> 22) % 6);
        var fallback = $"{cdn}/embed/avatars/{index}.png";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var hash = account.Avatar;
            var hex = hash?.StartsWith("a_", StringComparison.Ordinal) == true ? hash[2..] : hash;
            if (hex is { Length: 32 } && hex.All(Uri.IsHexDigit))
            {
                var custom = await ReadAvatarAsync($"{cdn}/avatars/{id}/{hash}.png?size=64", timeout.Token).ConfigureAwait(false);
                if (custom is not null)
                    return custom;
            }

            return await ReadAvatarAsync(fallback, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadAvatarAsync(string url, CancellationToken cancellation)
    {
        const int maxSize = 256 * 1024;
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "image/png" ||
            response.Content.Headers.ContentLength > maxSize)
            return null;
        await response.Content.LoadIntoBufferAsync(maxSize, cancellation).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
    }

    private static string RandomToken() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Token([property: JsonPropertyName("access_token")] string AccessToken);
    private sealed record Profile(string Id, string Username, [property: JsonPropertyName("global_name")] string? DisplayName,
        string? Avatar, string? Discriminator);
}
