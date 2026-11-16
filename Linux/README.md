# Simple Mod Manager for Linux

Extract `SimpleModManager-linux-x64.tar.gz` and run `./SimpleModManager.Linux`. The archive includes the .NET runtime. For an application menu entry, run `sh install.sh` from the extracted folder. This installs a user copy under `$XDG_DATA_HOME/simple-mod-manager`, or `~/.local/share/simple-mod-manager` when XDG_DATA_HOME is unset.

The Linux app manages the Windows Steam edition of Gorilla Tag through Proton. Choose its game folder in **Game Setup**. Detection includes standard Steam libraries, additional libraries in libraryfolders.vdf, and Flatpak Steam. BepInEx setup creates its plugins folder immediately.

Steam needs the winhttp DLL override for BepInEx. In Gorilla Tag's Steam properties, merge this into Launch Options.

```text
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

This setting configures the BepInEx loader. Gorilla Tag and individual mods still depend on your Proton and VR setup. Gameplay under Linux has not been verified here. See the [BepInEx Proton instructions](https://docs.bepinex.dev/articles/advanced/proton_wine.html).

The Linux interface includes the library, favorites, installed DLL controls, collections, profiles, queue, plugin scan, duplicate IDs, install history, config tools, logs, and themes. Linux uses a dark fallback for Liquid glass and static gradient artwork for GIF banners. Settings and backups are saved in the OS application data directory under SimpleModManager, typically `~/.local/share/SimpleModManager`.

The self-contained archive supports glibc Linux x64 desktops. Avalonia needs the desktop's native libraries, including X11, fontconfig, and freetype. Packaging details are in the [Avalonia Linux documentation](https://docs.avaloniaui.net/docs/deployment/linux).

To build the archive from Windows, run `pwsh -File Linux/package.ps1` from the source root. To publish from Linux, run the command below from the source root, then copy Linux/install.sh into the output folder.

```sh
dotnet publish Linux/SimpleModManager.Linux.csproj -c Release -r linux-x64 --self-contained true -p:DebugType=none -o dist/linux-x64
```

App updates use the same GitHub version.txt and release checksums as the Windows app. Publish the Linux archive as `SimpleModManager-linux-x64.tar.gz` in the latest release. The app replaces a writable extracted installation after confirmation and keeps the previous folder as a backup.
