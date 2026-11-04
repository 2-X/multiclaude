#!/bin/bash
# Build + install MultiClaude.app into ~/Applications.
#   ./build.sh            build, install, (re)launch
#   ./build.sh dist       build a release zip into ../dist (no install)
#   SIGN="-" ./build.sh   ad-hoc sign (for machines without a Developer ID;
#                         the Accessibility grant then resets on every rebuild)
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$HERE")"
OUT="$HERE/build/MultiClaude.app"
DEST="${DEST:-$HOME/Applications/MultiClaude.app}"
# A stable Developer ID keeps the Accessibility (window cycler) grant across rebuilds.
SIGN="${SIGN:-$(security find-identity -v -p codesigning | awk -F'"' '/Developer ID Application/{print $2; exit}')}"
SIGN="${SIGN:--}"

# Theme toolchain (optional): only fetched when blackmode is installed and npm exists.
if [ -d "$HOME/.config/Claude/blackmode" ] && [ ! -d "$ROOT/node_modules/@electron/asar" ] && command -v npm >/dev/null; then
  (cd "$ROOT" && npm install --silent --no-audit --no-fund)
fi

rm -rf "$OUT"; mkdir -p "$OUT/Contents/MacOS" "$OUT/Contents/Resources"
# Universal (Apple silicon + Intel).
for arch in arm64 x86_64; do
  swiftc -O -swift-version 5 -parse-as-library -target $arch-apple-macos14.0 \
    -o "$HERE/build/MultiClaude-$arch" "$HERE/MultiClaude.swift"
done
lipo -create -output "$OUT/Contents/MacOS/MultiClaude" "$HERE/build/MultiClaude-arm64" "$HERE/build/MultiClaude-x86_64"
rm -f "$HERE/build/MultiClaude-arm64" "$HERE/build/MultiClaude-x86_64"
cp "$HERE/Info.plist" "$OUT/Contents/Info.plist"
cp "$ROOT/multiclaude" "$ROOT/entitlements.plist" "$OUT/Contents/Resources/"
# asar/fuses CLIs: only used when the optional blackmode theme hook is installed.
[ -d "$ROOT/node_modules" ] && cp -R "$ROOT/node_modules" "$OUT/Contents/Resources/node_modules"
VER="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$HERE/Info.plist")"

# App icon: three stacked Claude-ish tiles.
if [ -f "$HERE/AppIcon.icns" ]; then
  cp "$HERE/AppIcon.icns" "$OUT/Contents/Resources/AppIcon.icns"
elif command -v magick >/dev/null; then
  T="$(mktemp -d)"; I="$T/AppIcon.iconset"; mkdir -p "$I"
  magick -size 1024x1024 xc:none \
    -fill '#3B2A6B' -draw 'roundrectangle 300,120 900,720 110,110' \
    -fill '#15433D' -draw 'roundrectangle 210,230 810,830 110,110' \
    -fill '#111111' -draw 'roundrectangle 120,340 720,940 110,110' \
    -fill '#FEFCFB' -font Helvetica-Bold -pointsize 520 -gravity NorthWest -annotate +255+300 '*' \
    "$T/icon.png"
  for s in 16 32 128 256 512; do
    sips -z $s $s "$T/icon.png" --out "$I/icon_${s}x${s}.png" >/dev/null
    sips -z $((s*2)) $((s*2)) "$T/icon.png" --out "$I/icon_${s}x${s}@2x.png" >/dev/null
  done
  iconutil -c icns "$I" -o "$OUT/Contents/Resources/AppIcon.icns"; rm -rf "$T"
fi

if [ "$SIGN" = "-" ]; then
  codesign --force --deep --sign - "$OUT"
else
  codesign --force --deep --options runtime --timestamp --entitlements "$HERE/MultiClaude.entitlements" --sign "$SIGN" "$OUT"
fi
codesign --verify --deep --strict "$OUT"
echo "signed with: $SIGN"

if [ "${1:-}" = "dist" ]; then
  mkdir -p "$ROOT/dist"
  Z="$ROOT/dist/MultiClaude-$VER.zip"
  rm -f "$Z"; ditto -c -k --keepParent "$OUT" "$Z"
  # Notarize when a notarytool keychain profile is configured (NOTARY_PROFILE=name).
  if [ -n "${NOTARY_PROFILE:-}" ]; then
    xcrun notarytool submit "$Z" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$OUT" && rm -f "$Z" && ditto -c -k --keepParent "$OUT" "$Z"
  fi
  shasum -a 256 "$Z"
  exit 0
fi

pkill -x MultiClaude 2>/dev/null && sleep 1 || true
rm -rf "$DEST"; ditto "$OUT" "$DEST"
open "$DEST"
echo "installed $DEST"
