#!/bin/sh
set -eu
source_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
destination="${XDG_DATA_HOME:-$HOME/.local/share}/simple-mod-manager"
applications="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
if [ "$source_dir" != "$destination" ]; then
    mkdir -p "$destination"
    cp -R "$source_dir/." "$destination/"
fi
chmod u+x "$destination/SimpleModManager.Linux"
mkdir -p "$applications"
# Desktop-entry Exec quoting follows the desktop specification, including percent escaping.
exec_path=$(printf '%s' "$destination/SimpleModManager.Linux" | sed 's/\\/\\\\/g; s/"/\\"/g; s/`/\\`/g; s/\$/\\$/g; s/%/%%/g')
printf '[Desktop Entry]\nType=Application\nName=Simple Mod Manager\nExec="%s"\nTerminal=false\nCategories=Game;Utility;\n' "$exec_path" > "$applications/simple-mod-manager.desktop"
printf 'Installed Simple Mod Manager. Open it from your application menu.\n'
