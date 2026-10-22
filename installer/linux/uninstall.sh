#!/usr/bin/env bash
# Removes what install.sh added. Your settings (projects, tabs, window) in ~/.config/peachapps/DataPeach stay.
set -euo pipefail
data="${XDG_DATA_HOME:-$HOME/.local/share}"
dir="$HOME/.local/opt/datapeach"
config="${XDG_CONFIG_HOME:-$HOME/.config}/peachapps/DataPeach"

rm -f "$data/applications/datapeach.desktop"
rm -f "$data/icons/hicolor/256x256/apps/datapeach.png"
# Only our own link, never a "datapeach" the user put there.
if [[ -L "$HOME/.local/bin/datapeach" && "$(readlink "$HOME/.local/bin/datapeach")" == "$dir/DataPeach" ]]; then
  rm -f "$HOME/.local/bin/datapeach"
fi
rm -f "$dir/DataPeach" "$dir/DataPeach.new" "$dir/DataPeach.bin.new" "$dir/installed.txt" "$dir/uninstall.sh"
rmdir "$dir" 2>/dev/null || true

command -v update-desktop-database >/dev/null && update-desktop-database -q "$data/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$data/icons/hicolor" || true

echo "DataPeach desinstalado."
echo "Tu configuración sigue en $config (bórrala a mano si ya no la quieres)."
