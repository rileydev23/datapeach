#!/usr/bin/env bash
# Builds a single-file, self-contained, portable DataPeach for Linux x64 into ./dist
# Optional version (./build.sh 1.2.0), stamped into the binary; without it the csproj's <Version> is used.
set -euo pipefail
cd "$(dirname "$0")"
VERSIONARG=()
if [[ $# -gt 0 && -n "$1" ]]; then VERSIONARG=("-p:Version=$1"); fi
rm -rf dist
# Always from scratch: a stale incremental Release build can ship without the compiled XAML (the app dies at startup).
rm -rf src/DataPeach/obj/Release
# No compression: the binary is about twice as big but starts faster and uses a third of the memory, since nothing
# has to be unpacked into RAM at every start.
dotnet publish src/DataPeach/DataPeach.csproj -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true \
  -p:PublishReadyToRun=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=false \
  -p:DebugType=none -p:DebugSymbols=false "${VERSIONARG[@]}" \
  -o dist
chmod +x dist/DataPeach
echo
echo "Listo: $(pwd)/dist/DataPeach"
echo "Para modo 100% portable (datos junto al binario) crea un archivo \"portable.txt\" al lado."
