#!/usr/bin/env bash
# Builds the plugin and the Thunderstore package (dist/ServerConfigSync-<version>.zip).
# Usage: VALHEIM_DIR=/path/to/Valheim ./build.sh
set -euo pipefail
cd "$(dirname "$0")"

dotnet build src/ServerConfigSync -c Release -nologo "$@"

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/ServerConfigSync/ServerConfigSync.csproj)
manifest_version=$(sed -n 's/.*"version_number": "\(.*\)".*/\1/p' thunderstore/manifest.json)
if [ "$version" != "$manifest_version" ]; then
  echo "Version mismatch: csproj $version, manifest.json $manifest_version" >&2
  exit 1
fi
if ! grep -q "public const string Version = \"$version\";" src/ServerConfigSync/Plugin.cs; then
  echo "Version mismatch: Plugin.cs does not say $version" >&2
  exit 1
fi

stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
cp thunderstore/manifest.json thunderstore/icon.png README.md CHANGELOG.md "$stage/"
cp src/ServerConfigSync/bin/Release/ServerConfigSync.dll "$stage/"

mkdir -p dist
out="dist/ServerConfigSync-$version.zip"
rm -f "$out"
(cd "$stage" && zip -q -X -r - .) > "$out"
echo "Package: $out"
unzip -l "$out"
