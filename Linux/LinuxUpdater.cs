using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;

namespace SimpleModManager.Linux;

internal static class LinuxUpdater
{
    private const string Executable = "SimpleModManager.Linux";
    public static bool Started { get; private set; }

    public static void Start(string archive, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException("Use the Linux package to update a Linux installation.");
        Started = false;
        var destination = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (Environment.ProcessPath != Path.Combine(destination, Executable) ||
            new DirectoryInfo(destination).LinkTarget is not null)
            throw new IOException("Automatic updates require the extracted, writable Linux package.");
        var parent = Directory.GetParent(destination)?.FullName ?? throw new IOException("Invalid app directory.");
        var staging = Path.Combine(parent, ".SimpleModManager-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            Extract(archive, staging, token);
            if (!File.Exists(Path.Combine(staging, Executable)) ||
                !File.Exists(Path.Combine(staging, "SimpleModManager.Linux.runtimeconfig.json")) ||
                !File.Exists(Path.Combine(staging, "libhostfxr.so")))
                throw new InvalidDataException("The release archive is missing the Linux app or its bundled runtime.");
            var helper = Path.Combine(ModWorkspace.DataFolder, "updates", "helper-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(helper);
            foreach (var file in Directory.EnumerateFiles(staging))
            {
                token.ThrowIfCancellationRequested();
                var copy = Path.Combine(helper, Path.GetFileName(file));
                File.Copy(file, copy);
                File.SetUnixFileMode(copy, File.GetUnixFileMode(file));
            }
            var start = new ProcessStartInfo(Path.Combine(helper, Executable)) { UseShellExecute = false, WorkingDirectory = helper };
            start.ArgumentList.Add("--complete-update");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(destination);
            start.ArgumentList.Add(staging);
            _ = Process.Start(start) ?? throw new IOException("Could not start the Linux update helper.");
            Started = true;
        }
        catch
        {
            // This folder is created above, never a user-supplied target.
            Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    internal static void Extract(string archive, string staging, CancellationToken token)
    {
        using var file = File.OpenRead(archive);
        using var compressed = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(compressed);
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
            if (entry.EntryType == TarEntryType.Directory && name is "" or ".")
                continue;
            var relative = name.TrimEnd('/');
            var path = Installer.CheckedPath(staging, relative);
            if (!names.Add(relative) || names.Count > 1024)
                throw new InvalidDataException("The update archive contains duplicate paths or too many files.");
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(path);
                continue;
            }
            total += entry.Length;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                total > 512L * 1024 * 1024 || entry.DataStream is null)
                throw new InvalidDataException("The update archive has unsupported entries or exceeds the size limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                entry.DataStream.CopyTo(output);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(path, relative == Executable || relative.EndsWith(".sh", StringComparison.Ordinal)
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    public static bool Complete(string[] args)
    {
        if (args.Length != 4 || args[0] != "--complete-update")
            return false;
        try
        {
            if (!OperatingSystem.IsLinux() || !int.TryParse(args[1], out var pid) || pid <= 0)
                throw new InvalidDataException("Invalid update helper invocation.");
            var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[2]));
            var staging = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[3]));
            if (Directory.GetParent(destination)?.FullName != Directory.GetParent(staging)?.FullName ||
                !Path.GetFileName(staging).StartsWith(".SimpleModManager-update-", StringComparison.Ordinal) ||
                new DirectoryInfo(destination).LinkTarget is not null || new DirectoryInfo(staging).LinkTarget is not null ||
                !File.Exists(Path.Combine(destination, Executable)) || !File.Exists(Path.Combine(staging, Executable)))
                throw new InvalidDataException("Invalid update installation paths.");
            try
            {
                using var parent = Process.GetProcessById(pid);
                if (parent.MainModule?.FileName != Path.Combine(destination, Executable))
                    throw new IOException("The process being updated does not match this installation.");
                if (!parent.WaitForExit(60000))
                    throw new IOException("The previous app did not close in time.");
            }
            catch (ArgumentException) { } // The original app already exited.
            var backup = destination + ".previous-" + Guid.NewGuid().ToString("N");
            Directory.Move(destination, backup);
            try
            {
                Directory.Move(staging, destination);
                var start = new ProcessStartInfo(Path.Combine(destination, Executable)) { UseShellExecute = false, WorkingDirectory = destination };
                start.ArgumentList.Add("--updated");
                _ = Process.Start(start) ?? throw new IOException("Could not launch the updated app.");
            }
            catch
            {
                if (Directory.Exists(destination)) Directory.Move(destination, staging);
                Directory.Move(backup, destination);
                throw;
            }
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(Path.Combine(ModWorkspace.DataFolder, "updates"));
            File.WriteAllText(Path.Combine(ModWorkspace.DataFolder, "updates", "last-error.txt"), ex.Message);
        }
        return true;
    }
}
