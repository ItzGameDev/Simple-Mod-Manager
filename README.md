# Simple Mod Manager

Simple Mod Manager 3.0.0 is an open-source app for setting up BepInEx and managing community mods for Gorilla Tag on PCVR. The Windows app supports Steam and Meta Horizon PC installations. The Linux app manages the Windows Steam edition through Proton. Check each mod’s details for compatibility.

## Getting started

1. Close Gorilla Tag and run `SimpleModManagerSetup.exe`. Setup asks for administrator approval, adds a Start menu shortcut, and offers a desktop shortcut.
2. Setup checks for the **.NET 10 Desktop Runtime (x64)** and downloads it from Microsoft when needed. That download requires internet access. Before copying files, setup adds Windows Defender exclusions for its own install folder and for every Gorilla Tag `BepInEx\plugins` folder it can find on Steam or Meta Horizon PC.
3. Open the manager. In **Game Setup**, select your Steam or Meta Horizon PC installation, or browse to the game folder. The manager installs BepInEx when needed. Launch Gorilla Tag once after setup, then close it before changing mods.
4. Open **Official Mods**, select a mod card, and choose **Install mod**. Check the details for dependencies and compatibility. Use the star to save a favorite.

For Linux, extract `SimpleModManager-linux-x64.tar.gz` and run `./SimpleModManager.Linux`. Run `sh install.sh` for an application menu entry. The Linux archive includes the .NET runtime. Follow the [Linux setup instructions](Linux/README.md) to configure Steam Proton and the BepInEx DLL override.

## What you can do

| Where | What you can do |
| --- | --- |
| **Official Mods** | Search by name, filter by category, favorite mods, and queue downloads. |
| **Installed Mods** | See catalog mods and DLLs added manually. Enable or disable them while Gorilla Tag is closed. |
| **Mod Tools** | Organize collections, preview and apply profiles, check releases, view install history, and inspect plugin DLLs for duplicate IDs. |
| **Game Setup** | Detect the game folder, check BepInEx, open game folders, and access logs and config tools. |
| **Settings** | Change themes and animation settings, import or export a custom theme, and open or resume the tutorial. |

The manager verifies catalog downloads against their expected SHA-256 checksums. It installs managed mods in `BepInEx/plugins/SimpleModManager` and keeps disabled DLLs with a `.disabled` suffix. BepInEx setup creates the plugins folder immediately. Old Modes, Bark, and Walk Simulator install Utilla when needed. Gorilla Info Watch and Mono Sandbox install GorillaLibrary when needed. The manager verifies the bundled Walk Simulator DLL before installing it.

## Discord sign-in

On startup, sign in with Discord through your browser or choose **Continue as guest**. Signed-in users see their Discord display name and avatar above the game status in the sidebar and have unlimited mod downloads. Sign-in lasts until you sign out or close the manager.

Guests can download five mods in a rolling hour and see a sign-in reminder every 30 minutes. Direct and queued downloads share a per-user counter saved on the device. An install and its automatically downloaded dependencies count as one download. Guests can sign in from **Settings → Discord account** at any time.

To enable sign-in for this build, open application `1573769040701423626` in the Discord Developer Portal. Under **OAuth2**, enable **Public Client** and add the exact redirect `http://127.0.0.1:42813/discord/callback/`. The desktop app requests the `identify` scope and uses PKCE. It needs no client secret or hosted callback.

## App updates

The app checks the project’s GitHub version.txt on startup. A newer published version offers a download and installation after you choose to update. The Windows app starts the setup EXE. The Linux app replaces a writable extracted installation and keeps the previous app folder as a backup. You can turn startup checks off in Settings or check manually.

To publish an update, build the release with its new app version, upload its assets, then update version.txt in the GitHub repository to match. For this release, the local version.txt contains `3.0.0`. Future updates need a higher version, such as `3.0.1`. The updater uses the assets in the latest published GitHub release, so publish the new assets before changing version.txt.

Use `SimpleModManagerSetup.exe` for the Windows asset and `SimpleModManager-linux-x64.tar.gz` for Linux. Each asset needs the SHA-256 digest supplied by GitHub, or an entry in a release asset named `SHA256SUMS.txt`. Checksums use the format `<64-character hash>  <asset filename>`.

## Troubleshooting

- **Game folder missing.** Choose **Game Setup → Browse for game**. Typical folders are `C:\Program Files (x86)\Steam\steamapps\common\Gorilla Tag` for Steam and `C:\Program Files\Oculus\Software\Software\another-axiom-gorilla-tag` for Meta Horizon PC.
- **A mod disappeared.** Check **Windows Security → Virus & threat protection → Protection history**. If there’s a matching alert, report the DLL filename and detection name to the mod manager team. Leave the file in quarantine while it’s reviewed. If there’s no alert, share the manager’s exact install message. [Microsoft’s Protection History guide](https://support.microsoft.com/en-us/windows/security/windows-security/protection-history-in-the-windows-security-app)
- **Check for Israel Auth.** Choose **Mod Tools → Scan for Israel Auth** to check enabled and disabled plugin DLLs. The scan reports filenames that match known indicators and identifies unreadable files. A match can also come from a plugin that references Israel Auth.

## Build from source

Use Windows, the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), and Inno Setup 6.6 or later. From the source folder, run these PowerShell commands. Use the path to your local Inno Setup compiler if it differs.

```powershell
$output = Join-Path (Get-Location).Path 'dist\installer-build'
$iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'

dotnet publish .\SimpleModManager.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o $output
if ($LASTEXITCODE -eq 0) {
    & $iscc /DInstallDesktopRuntime "/DReleaseDir=$output" ".\Installer\SimpleModManager.iss"
}
```

The resulting installer is `dist\installer-build\SimpleModManagerSetup.exe`. The standalone executable in the same folder uses the shared .NET Desktop Runtime. The installer adds runtime detection and installation.

Build the Linux archive with `pwsh -File Linux/package.ps1`. The script writes `dist/SimpleModManager-linux-x64.tar.gz`. Linux builds use the Avalonia project in Linux/, with installation and scanning services shared with the Windows app.

## Community and license

Use **Request a mod** in the app or join the [community Discord](https://discord.gg/itzgame-s-fries-1536853966741180476). Follow the [Gorilla Tag Mod Policy](https://www.gorillatagvr.com/fan-content-mod-policy) and use gameplay mods in modded rooms. The **Legal** badge is a community catalog label.

The C# source is [MIT licensed](LICENSE). Mod DLLs retain their own licenses. Update Changer downloads from ItzGame’s GitHub release and requires about 2 GB of free space in the game’s drive. Mod artwork is credited to its respective creators and rights holders. Simple Mod Manager is an independent community project. Gorilla Tag and its game assets belong to Another Axiom.
