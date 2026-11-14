# Simple Mod Manager

Simple Mod Manager is an open-source Windows app for setting up BepInEx and managing community mods for Gorilla Tag on PCVR. It supports Steam and Meta Horizon PC installations. Check each mod’s details for compatibility.

## Getting started

1. Close Gorilla Tag and run `SimpleModManagerSetup.exe`. Setup adds a Start menu shortcut and offers a desktop shortcut.
2. The compact installer checks for the **.NET 10 Desktop Runtime (x64)** and downloads it from Microsoft when needed. That download requires internet access and may prompt for administrator approval.
3. Open the manager. In **Game Setup**, select your Steam or Meta Horizon PC installation, or browse to the game folder. The manager installs BepInEx when needed. Launch Gorilla Tag once after setup, then close it before changing mods.
4. Open **Mod Libary**, select a mod card, and choose **Install mod**. Check the details for dependencies and compatibility. Use the star to save a favorite.

## What you can do

| Where | What you can do |
| --- | --- |
| **Mod Libary** | Search by name, filter by category, favorite mods, and queue downloads. |
| **Installed Mods** | See catalog mods and DLLs added manually. Enable or disable them while Gorilla Tag is closed. |
| **Mod Tools** | Organize collections, preview and apply profiles, check releases, view install history, and inspect plugin DLLs for duplicate IDs. |
| **Game Setup** | Detect the game folder, check BepInEx, open game folders, and access logs and config tools. |
| **Settings** | Change themes and animation settings, import or export a custom theme, and open or resume the tutorial. |

The manager verifies catalog downloads against their expected SHA-256 checksums. It installs managed mods in `BepInEx\plugins\SimpleModManager` and keeps disabled DLLs with a `.disabled` suffix. Some mods install a dependency automatically. For example, Mono Sandbox uses GorillaLibrary and Bark uses Utilla.

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

## Community and license

Use **Request a mod** in the app or join the [community Discord](https://discord.gg/itzgame-s-fries-1536853966741180476). Follow the [Gorilla Tag Mod Policy](https://www.gorillatagvr.com/fan-content-mod-policy) and use gameplay mods in modded rooms. The **Legal** badge is a community catalog label.

The C# source is [MIT licensed](LICENSE). Mod DLLs retain their own licenses. Update Changer was supplied by its author, ItzGame. Mod artwork is credited to its respective creators and rights holders. Simple Mod Manager is an independent community project. Gorilla Tag and its game assets belong to Another Axiom.
