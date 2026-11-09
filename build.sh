#!/bin/bash
# Baut das Plugin in einem .NET-SDK-Container (auf dem Unraid-Server muss nichts installiert werden).
# Aufruf:  ./build.sh            → für Jellyfin 12.x (.NET 10)
#          ./build.sh 10.11      → für Jellyfin 10.11 (.NET 9)
set -euo pipefail
cd "$(dirname "$0")"

JF_MINOR="${1:-12}"
case "$JF_MINOR" in
  12)    TFM=net10.0; SDK=10.0; PKG="12.*";    ABI="12.0.0.0" ;;
  10.11) TFM=net9.0;  SDK=9.0;  PKG="10.11.*"; ABI="10.11.0.0" ;;
  *)     echo "Unbekannte Version: $JF_MINOR (12 oder 10.11)"; exit 1 ;;
esac

VERSION=$(grep -oP '(?<=<AssemblyVersion>)[^<]+' Jellyfin.Plugin.FranchiseCollections/Jellyfin.Plugin.FranchiseCollections.csproj)
OUT="dist/FranchiseCollections_${VERSION}"
rm -rf dist/build "$OUT"
mkdir -p "$OUT"

docker run --rm -v "$PWD":/src -w /src "mcr.microsoft.com/dotnet/sdk:${SDK}" \
  dotnet publish Jellyfin.Plugin.FranchiseCollections/Jellyfin.Plugin.FranchiseCollections.csproj \
    -c Release -o /src/dist/build \
    -p:PluginTargetFramework="$TFM" -p:JellyfinVersion="$PKG"

cp dist/build/Jellyfin.Plugin.FranchiseCollections.dll "$OUT/"
sed -e "s/__VERSION__/${VERSION}/" -e "s/__ABI__/${ABI}/" meta.json > "$OUT/meta.json"
rm -rf dist/build

echo
echo "Fertig: $OUT"
echo "Diesen Ordner in den Jellyfin-Plugin-Ordner kopieren und Jellyfin neu starten."
