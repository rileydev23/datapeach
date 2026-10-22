#!/usr/bin/env bash
# Installs DataPeach for the current user, without root: the binary in ~/.local/opt/datapeach (so the app can update
# itself there), a "datapeach" command in ~/.local/bin and a menu entry, so the desktop's app search finds it.
# Run it from the extracted release:  ./install.sh      Undo with:  ~/.local/opt/datapeach/uninstall.sh
set -euo pipefail
src="$(cd "$(dirname "$0")" && pwd)"
data="${XDG_DATA_HOME:-$HOME/.local/share}"
dir="$HOME/.local/opt/datapeach"
bin="$HOME/.local/bin"

if [[ ! -f "$src/DataPeach" ]]; then
  echo "No se encontró el binario DataPeach junto a install.sh." >&2
  exit 1
fi

mkdir -p "$dir" "$bin" "$data/applications" "$data/icons/hicolor/256x256/apps"
# Through a temporary name: replacing a running binary in place would break it, a rename does not.
install -m 755 "$src/DataPeach" "$dir/DataPeach.tmp"
mv -f "$dir/DataPeach.tmp" "$dir/DataPeach"
install -m 755 "$src/uninstall.sh" "$dir/uninstall.sh"
install -m 644 "$src/installed.txt" "$dir/installed.txt"
install -m 644 "$src/datapeach.png" "$data/icons/hicolor/256x256/apps/datapeach.png"
ln -sf "$dir/DataPeach" "$bin/datapeach"

cat > "$data/applications/datapeach.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=DataPeach
GenericName=Database client
GenericName[es]=Cliente de bases de datos
Comment=Fast database client with staged changes and delete history
Comment[es]=Cliente de bases de datos rápido, con cambios en staging e historial de borrados
Exec="$dir/DataPeach" %F
Icon=datapeach
Terminal=false
Categories=Development;Database;
Keywords=sql;database;query;postgres;mysql;sqlserver;
EOF

command -v update-desktop-database >/dev/null && update-desktop-database -q "$data/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$data/icons/hicolor" || true

echo "DataPeach instalado en $dir"
echo "Búscalo en el menú de aplicaciones o ejecuta: datapeach"
case ":$PATH:" in *":$bin:"*) ;; *) echo "(Agrega $bin a tu PATH para usar el comando datapeach.)" ;; esac
echo "Para desinstalar: $dir/uninstall.sh"
