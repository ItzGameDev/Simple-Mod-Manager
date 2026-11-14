using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace SimpleModManager;

public partial class MainWindow
{
    private void OpenGameTools(object sender, RoutedEventArgs e)
    {
        if (game is not { } target || operation is not null) return;
        var panel = NewToolsPanel("Game tools", "Check the game installation and manage BepInEx logs and configs.");
        var row = ToolsRow(panel.Body);
        ToolButton(row, "Run health check", async () => await ReadTool(panel, "Checking the game folder…", token => Task.FromResult(
            GameHealth(target, ModWorkspace.Inventory(target, token))), lines => panel.Report(string.Join("\n", lines))), true);
        ToolButton(row, "Copy redacted support info", async () => await ReadTool(panel, "Preparing support summary…", token =>
            Task.FromResult(GameHealth(target, ModWorkspace.Inventory(target, token))), lines =>
        {
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, new[] { "Simple Mod Manager support summary",
                    $"App {typeof(MainWindow).Assembly.GetName().Version}", $"Windows {Environment.OSVersion.Version}" }.Concat(lines)));
                panel.Report("Copied version and health information. Paths, account names, plugin names, and raw logs were omitted.");
            }
            catch (System.Runtime.InteropServices.ExternalException) { panel.Report("Clipboard is busy. Try copying again."); }
        }));

        Section(panel.Body, "Gorilla Tag");
        var gameRow = ToolsRow(panel.Body);
        ToolButton(gameRow, "Launch Gorilla Tag", () =>
        {
            if (target.Store == Storefront.Steam)
                OpenLink("steam://rungameid/1533390");
            else
            {
                var executable = new[] { "Gorilla Tag.exe", "GorillaTag.exe" }.Select(name => Path.Combine(target.Path, name))
                    .FirstOrDefault(File.Exists);
                if (executable is null) { panel.Report("The game executable was not found."); return; }
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable)
                    { UseShellExecute = true, WorkingDirectory = target.Path });
                }
                catch (System.ComponentModel.Win32Exception ex) { panel.Report(ex.Message); }
            }
        });
        ToolButton(gameRow, "Open game folder", () => OpenLink(target.Path));
        ToolButton(gameRow, "Open plugins folder", () => OpenGameSubfolder(panel, target, Installer.PluginsPath));
        ToolButton(gameRow, "Open config folder", () => OpenGameSubfolder(panel, target, @"BepInEx\config"));

        Section(panel.Body, "BepInEx");
        var bepinex = ToolsRow(panel.Body);
        ToolButton(bepinex, "View log", () => OpenLog(target));
        ToolButton(bepinex, "Edit or reset config", () => OpenConfigEditor(target));
        ToolButton(bepinex, "Back up configs", async () =>
        {
            var picker = new SaveFileDialog { Filter = "Config ZIP (*.zip)|*.zip", FileName = "BepInEx-configs.zip" };
            if (picker.ShowDialog(this) != true) return;
            await ReadTool(panel, "Backing up configs…", _ => Task.Run(() =>
            {
                ModWorkspace.BackupConfigs(target, picker.FileName);
                return picker.FileName;
            }), file => panel.Report($"Saved {file}"));
        });
        ToolButton(bepinex, "Restore configs", async () =>
        {
            var picker = new OpenFileDialog { Filter = "Config ZIP (*.zip)|*.zip" };
            if (picker.ShowDialog(this) != true ||
                !await Confirm(panel, "Restore .cfg files from this ZIP? Matching configs will be replaced.")) return;
            await ReadTool(panel, "Restoring configs…", _ => Task.Run(() =>
            {
                ModWorkspace.RestoreConfigs(target, picker.FileName);
                return true;
            }), _ => panel.Report("Config restore completed."));
        });

        ShowTool(panel);
    }

    private static string[] GameHealth(GameInstall target, PluginInspection[] plugins)
    {
        var state = Installer.ReadLoader(target);
        var outdated = Version.TryParse(state.Version, out var installed) && installed < Version.Parse(Catalog.BepInExVersion);
        return
        [
            $"Game installation: {(GameDiscovery.Open(target.Path, target.Store) is null ? "Unavailable" : target.Label)}",
            $"BepInEx: {(!state.Installed ? "Missing" : $"{state.Version}, {(state.Enabled ? "enabled" : "disabled")}{(outdated ? ", update available" : "")}")}",
            $"Plugin files: {plugins.Length} total, {plugins.Count(item => item.Disabled)} disabled, {plugins.Count(item => item.Error is not null)} unreadable",
            $"Duplicate plugin IDs: {plugins.SelectMany(item => item.Identities.Select(id => (item.File, id.Id)))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Count(group => group.Select(item => item.File)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)}",
            $"Config files: {ModWorkspace.Configs(target).Length}",
            $"BepInEx log: {(File.Exists(Installer.CheckedPath(target.Path, @"BepInEx\LogOutput.log")) ? "Present" : "Missing")}"
        ];
    }

    private void OpenGameSubfolder(ToolsPanel panel, GameInstall target, string relative)
    {
        try
        {
            var folder = Installer.CheckedPath(target.Path, relative);
            if (Directory.Exists(folder)) OpenLink(folder);
            else panel.Report("This folder has not been created yet.");
        }
        catch (IOException ex) { panel.Report(ex.Message); }
    }

    private void OpenLog(GameInstall target)
    {
        var panel = NewToolsPanel("BepInEx log", "View up to the last 256 KB of LogOutput.log.");
        var log = FileEditor(panel.Body, "BepInEx log", readOnly: true);
        var filter = ToolInput(panel.Body, "Filter log lines");
        string content = "";
        void Filter()
        {
            var query = filter.Text.Trim();
            log.Text = query.Length == 0 ? content : string.Join("\n", content.Split('\n')
                .Where(line => line.Contains(query, StringComparison.OrdinalIgnoreCase)));
            log.ScrollToEnd();
        }
        filter.TextChanged += (_, _) => Filter();
        async Task RefreshLog() => await ReadTool(panel, "Reading log…", async token =>
        {
            var path = Installer.CheckedPath(target.Path, @"BepInEx\LogOutput.log");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = Math.Max(0, stream.Length - 256 * 1024);
            using var reader = new StreamReader(stream);
            if (stream.Position != 0) await reader.ReadLineAsync(token);
            var buffer = new char[256 * 1024];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), token);
            return new string(buffer, 0, count);
        }, value => { content = value; Filter(); });
        ToolButton(panel.Body, "Refresh log", async () => await RefreshLog());
        panel.OnOpen = () => RefreshLog();
        ShowTool(panel);
    }

    private TextBox FileEditor(Panel panel, string label, bool readOnly = false)
    {
        var editor = new TextBox
        {
            Height = 305, AcceptsReturn = true, AcceptsTab = !readOnly, IsReadOnly = readOnly,
            FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AutomationProperties.SetName(editor, label);
        panel.Children.Add(editor);
        return editor;
    }

    private void OpenConfigEditor(GameInstall target)
    {
        var panel = NewToolsPanel("Config editor", "Edit a BepInEx .cfg file. Reset backs it up and lets the mod regenerate defaults at the next launch.");
        var pick = new ComboBox { MinHeight = 32, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(pick, "Config file");
        panel.Body.Children.Add(pick);
        var editor = FileEditor(panel.Body, "Config text");
        string? loadedFile = null;
        string previous = "";
        var controls = ToolsRow(panel.Body);
        ToolButton(controls, "Load selected config", async () =>
        {
            if (pick.SelectedItem is not string file) return;
            if (loadedFile is not null && editor.Text != previous && !await Confirm(panel, "Discard unsaved edits?")) return;
            await ReadTool(panel, "Reading config…", _ => Task.FromResult(ModWorkspace.ReadConfig(target, file)), text =>
            {
                loadedFile = file;
                previous = text;
                editor.Text = text;
                panel.Report(file);
            });
        });
        ToolButton(controls, "Save changes", async () =>
        {
            if (loadedFile is not { } file) { panel.Report("Load a config first."); return; }
            var edited = editor.Text;
            await ReadTool(panel, "Saving config…", _ =>
            {
                ModWorkspace.WriteConfig(target, file, previous, edited);
                return Task.FromResult(edited);
            }, text =>
            {
                previous = text;
                panel.Report($"Saved {file}. The original is backed up in AppData.");
            });
        }, true);
        ToolButton(controls, "Reset config", async () =>
        {
            if (loadedFile is not { } file || !await Confirm(panel, $"Reset \"{file}\" to default on next launch?")) return;
            await ReadTool(panel, "Backing up and resetting config…", _ =>
            {
                ModWorkspace.WriteConfig(target, file, previous, null);
                return Task.FromResult(ModWorkspace.Configs(target));
            }, files =>
            {
                loadedFile = null;
                editor.Clear();
                pick.ItemsSource = files;
                panel.Report("Reset complete. The original is backed up in AppData.");
            });
        });
        ToolButton(controls, "Open automatic backups", () =>
        {
            var folder = Path.Combine(ModWorkspace.DataFolder, "config-backups");
            if (Directory.Exists(folder)) OpenLink(folder);
            else panel.Report("No automatic config backups yet.");
        });
        panel.CanLeave = () => loadedFile is null || editor.Text == previous
            ? Task.FromResult(true)
            : Confirm(panel, "Discard unsaved changes?");
        panel.OnOpen = () => ReadTool(panel, "Finding configs…", _ =>
            Task.FromResult(ModWorkspace.Configs(target)), files =>
            {
                pick.ItemsSource = files;
                if (files.Length > 0) pick.SelectedIndex = 0;
                else panel.Report("No config files yet.");
            });
        ShowTool(panel);
    }
}

