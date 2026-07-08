#!/usr/bin/env bash
# Assemble MeshWeaver.app — the native macOS desktop shell bundling the LocalMesh (SQLite monolith mesh).
#
# Steps:
#   1. Publish LocalMesh (framework-dependent by default; pass --self-contained for a no-.NET-needed build).
#   2. Compile the native Swift launcher (AppKit + WKWebView) with swiftc.
#   3. Lay out MeshWeaver.app/Contents/{MacOS,Resources} — launcher + published mesh + baked dotnet path.
#   4. Ad-hoc codesign so Gatekeeper lets it run locally.
#
# Usage:  ./build-macos-app.sh [--self-contained] [--out <dir>]
# Result: <out>/MeshWeaver.app  (default out = ./dist)
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../../.." && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
OUT="$HERE/dist"
SELF_CONTAINED=0
RID="osx-$(uname -m | sed 's/arm64/arm64/;s/x86_64/x64/')"

while [ $# -gt 0 ]; do
  case "$1" in
    --self-contained) SELF_CONTAINED=1; shift ;;
    --out) OUT="$2"; shift 2 ;;
    *) echo "unknown arg: $1"; exit 1 ;;
  esac
done

APP="$OUT/MeshWeaver.app"
MESH_PROJ="$REPO/memex/Memex.LocalMesh/Memex.LocalMesh.csproj"

echo "▸ [1/4] publishing LocalMesh ($([ $SELF_CONTAINED = 1 ] && echo "self-contained $RID" || echo "framework-dependent"))"
STAGE="$(mktemp -d)/localmesh"
if [ "$SELF_CONTAINED" = 1 ]; then
  "$DOTNET" publish "$MESH_PROJ" -c Release -r "$RID" --self-contained true -o "$STAGE"
else
  "$DOTNET" publish "$MESH_PROJ" -c Release -o "$STAGE"
fi

echo "▸ [2/4] compiling native launcher (swiftc)"
BIN="$(mktemp -d)/MeshWeaver"
swiftc -O "$HERE/MeshWeaverApp.swift" -o "$BIN" -framework Cocoa -framework WebKit

echo "▸ [3/4] assembling $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/MeshWeaver"
cp "$HERE/Info.plist" "$APP/Contents/Info.plist"
cp -R "$STAGE" "$APP/Contents/Resources/localmesh"
# Bake the dotnet muxer path (a GUI app has no shell PATH); the launcher also falls back to common spots.
printf '%s' "$DOTNET" > "$APP/Contents/Resources/dotnet-path.txt"
[ -f "$HERE/AppIcon.icns" ] && cp "$HERE/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns" || true
printf 'APPL????' > "$APP/Contents/PkgInfo"

echo "▸ [4/4] ad-hoc codesign"
codesign --force --deep --sign - "$APP" 2>/dev/null || echo "  (codesign skipped)"

echo "✓ built $APP"
du -sh "$APP" | awk '{print "  size: "$1}'
