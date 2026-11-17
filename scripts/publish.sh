#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runtime="${1:-}"
case "$runtime" in
  win-x64|win-arm64|linux-x64|linux-arm64|osx-x64|osx-arm64) ;;
  *) echo "Usage: $0 {win-x64|win-arm64|linux-x64|linux-arm64|osx-x64|osx-arm64}" >&2; exit 2 ;;
esac
output="$root/artifacts/$runtime"
if [[ "$runtime" == osx-* && "$(uname -s)" != Darwin ]]; then
  echo "Build macOS on a Mac with Xcode Command Line Tools (xcode-select --install)." >&2
  exit 2
fi
dotnet publish "$root/PartyTime.csproj" -c Release -r "$runtime" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "$output"

if [[ "$runtime" == osx-* ]]; then
  helper="$output/Native/macOS/PartyTimeCapture"
  if [[ ! -f "$helper" ]]; then
    echo "The published macOS capture helper is missing: $helper" >&2
    echo "PartyTimeCapture must be built and copied outside the .NET single-file bundle (ExcludeFromSingleFile=true)." >&2
    exit 1
  fi
  if [[ "$runtime" == osx-arm64 ]]; then architecture=arm64; else architecture=x86_64; fi
  lipo -verify_arch "$architecture" "$helper"
  bundle="$root/artifacts/$runtime-app/PartyTime.app"
  mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
  cp -R "$output/." "$bundle/Contents/MacOS/"
  cp "$root/README.md" "$root/LICENSE" "$bundle/Contents/Resources/"
  cp "$root/Native/macOS/Info.plist" "$bundle/Contents/Info.plist"
  chmod +x "$bundle/Contents/MacOS/PartyTime" "$bundle/Contents/MacOS/Native/macOS/PartyTimeCapture"
  iconset="$root/artifacts/$runtime-icon.iconset"
  mkdir -p "$iconset"
  for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$root/Assets/PartyTime-v2.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$root/Assets/PartyTime-v2.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
  done
  iconutil -c icns "$iconset" -o "$bundle/Contents/Resources/PartyTime.icns"
  codesign --force --sign - --identifier com.partytime.capture "$bundle/Contents/MacOS/Native/macOS/PartyTimeCapture"
  codesign --force --deep --sign - "$bundle"
  codesign --verify --deep --strict "$bundle"
  tar -czf "$root/artifacts/PartyTime-$runtime.tar.gz" -C "$(dirname "$bundle")" PartyTime.app
  tar -tzf "$root/artifacts/PartyTime-$runtime.tar.gz" "PartyTime.app/Contents/MacOS/Native/macOS/PartyTimeCapture" >/dev/null
elif [[ "$runtime" == linux-* ]]; then
  chmod +x "$output/PartyTime"
  cp "$root/Assets/PartyTime-v2.png" "$output/PartyTime.png"
  cp "$root/README.md" "$root/LICENSE" "$output/"
  tar -czf "$root/artifacts/PartyTime-$runtime.tar.gz" -C "$output" .
else
  cp "$output/PartyTime.exe" "$root/artifacts/PartyTime-$runtime.exe"
fi
echo "Published $runtime to $output"
