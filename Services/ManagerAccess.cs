using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimpleModManager;

internal sealed class GuestDownloadLimitException() : InvalidOperationException(
    "You’ve used all 5 guest downloads for this hour. Log in to get more downloads!");

internal sealed class ManagerAccess(string? guestHistoryPath = null, TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan ReminderInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DownloadWindow = TimeSpan.FromHours(1);
    private readonly string historyPath = guestHistoryPath ?? Path.Combine(ModWorkspace.DataFolder, "guest-downloads.json");
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public DiscordSignIn Discord { get; } = new();
    public bool IsGuest { get; private set; }
    public bool CanEnter => IsGuest || Discord.DisplayName is not null;
    public string Welcome => Discord.DisplayName is { } name ? $"Welcome {name}!" : "Welcome, guest!";

    public void EnterAsGuest()
    {
        Discord.Cancel();
        IsGuest = true;
    }

    public async Task ToggleSignIn(Action render)
    {
        await Discord.ToggleAsync(() =>
        {
            if (Discord.DisplayName is not null)
                IsGuest = false;
            render();
        });
    }

    public async Task<FileChange[]> InstallMod(GameInstall game, Mod mod, Action<InstallProgress> progress,
        CancellationToken cancellation, string? localFile = null)
    {
        if (!CanEnter)
            throw new InvalidOperationException("Sign in with Discord or continue as a guest first.");

        Guid? ticket = null;
        Action? reserve = IsGuest ? () => ticket ??= ReserveDownload() : null;
        try
        {
            return await Installer.InstallMod(game, mod, progress, cancellation, localFile, reserve);
        }
        catch
        {
            if (ticket is { } id)
            {
                try { ChangeHistory(entries => entries.Where(entry => entry.Id != id).ToArray()); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // Preserve the install error. A reservation that cannot be removed expires in an hour.
                }
            }
            throw;
        }
    }

    internal Guid ReserveDownload()
    {
        var id = Guid.NewGuid();
        ChangeHistory(entries => entries.Length >= 5
            ? throw new GuestDownloadLimitException()
            : [.. entries, new(id, clock.GetUtcNow())]);
        return id;
    }

    private void ChangeHistory(Func<DownloadEntry[], DownloadEntry[]> change)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(historyPath))));
        using var mutex = new Mutex(false, "SimpleModManager-GuestDownloads-" + key);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                throw new IOException("The guest download counter is busy. Try again.");
        }
        catch (AbandonedMutexException)
        {
            // The previous process exited. The last complete history file is still usable.
        }

        try
        {
            var cutoff = clock.GetUtcNow() - DownloadWindow;
            var entries = File.Exists(historyPath)
                ? JsonSerializer.Deserialize<DownloadEntry[]>(File.ReadAllText(historyPath)) ?? []
                : [];
            var updated = change(entries.Where(entry => entry.Time > cutoff).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
            var temporary = historyPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(updated));
            File.Move(temporary, historyPath, overwrite: true);
        }
        catch (JsonException ex)
        {
            throw new IOException("Could not read the guest download counter. Log in to get more downloads!", ex);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private sealed record DownloadEntry(Guid Id, DateTimeOffset Time);
}
