#!/usr/bin/env bash
#
# Builds Josour.app for macOS, and optionally a .dmg to hand somebody.
#
# The Windows counterpart (publish-exe.ps1) exists because the distribution story there is one file a
# non-technical person copies and double-clicks. On macOS that story is a bundle: a bare Unix binary opens a
# Terminal window when double-clicked, has no icon and no name in the menu bar, because all three come from
# Info.plist inside a .app. So this does not only publish — it assembles, and then checks what it assembled.
#
# Self-contained on purpose: the .NET runtime travels inside, so nobody has to install anything first.
#
# And NOT single-file. On Windows that flag exists because the deliverable really is one loose .exe somebody
# copies. Here the deliverable is the bundle, which is a directory the user drags as one icon, so the runtime
# and the native libraries simply live inside it where dyld expects them. The first version of this script did
# use single-file, kept only the executable, and threw libSkiaSharp.dylib away — the app died before it could
# draw a window.
#
#   scripts/publish-app.sh                 # for this Mac's architecture
#   scripts/publish-app.sh --arch x64      # for Intel Macs
#   scripts/publish-app.sh --dmg           # also produce Josour-<version>-<arch>.dmg
#
# Gatekeeper: the result is UNSIGNED (ADR-0011). macOS refuses an unsigned app on first launch until the user
# right-clicks it and chooses Open, once. The script prints that at the end rather than leaving it to be
# discovered.

set -euo pipefail

arch="$(uname -m)"
case "$arch" in
    arm64) arch=arm64 ;;
    x86_64) arch=x64 ;;
    *) echo "unknown architecture: $arch" >&2; exit 1 ;;
esac

output="publish/mac"
make_dmg=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --arch) arch="$2"; shift 2 ;;
        --output) output="$2"; shift 2 ;;
        --dmg) make_dmg=1; shift ;;
        *) echo "unknown option: $1" >&2; exit 1 ;;
    esac
done

if [[ "$OSTYPE" != darwin* ]]; then
    echo "this builds a macOS bundle and needs macOS tools (sips, iconutil, hdiutil)" >&2
    exit 1
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
client="$root/client"
project="$client/src/Josour.App"
rid="osx-$arch"

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$project/Josour.App.csproj" | head -1)"
[[ -n "$version" ]] || { echo "could not read <Version> from the project file" >&2; exit 1; }

out_dir="$client/$output"
app="$out_dir/Josour.app"

echo "building Josour $version for $rid"

# A running copy holds its own files open and the publish fails halfway through.
if pgrep -f "$app/Contents/MacOS/Josour" >/dev/null 2>&1; then
    echo "Josour is running from $app. Quit it first." >&2
    exit 1
fi

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

dotnet publish "$project" \
    -c Release -f net8.0 -r "$rid" \
    --self-contained true \
    -o "$staging/publish" --nologo -v q

[[ -f "$staging/publish/Josour" ]] || { echo "no Josour binary was produced" >&2; exit 1; }

# Everything the publish produced, minus the debug symbols a user has no use for. Nothing is filtered beyond
# that: deciding which of the runtime's files "look needed" is how the native libraries got lost once already.
find "$staging/publish" -name '*.pdb' -delete
cp -R "$staging/publish/." "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/Josour"

# ---------------------------------------------------------------- icon
#
# The source art is 64x64, which is all there is. macOS asks for sizes up to 1024 and will happily upscale,
# so the large icon looks soft. Replacing Assets/josour.ico with a 1024x1024 master is the fix; this is
# noted at the end of the run rather than hidden.
iconset="$staging/josour.iconset"
mkdir -p "$iconset"

# Through PNG first: sips refuses to scale an .ico up past its own size, and would take the whole script
# down with it under `set -e`. From a PNG it will.
base="$staging/josour-base.png"
sips -s format png "$project/Assets/josour.ico" --out "$base" >/dev/null

missing=0
for spec in "16:icon_16x16" "32:icon_16x16@2x" "32:icon_32x32" "64:icon_32x32@2x" \
            "128:icon_128x128" "256:icon_128x128@2x" "256:icon_256x256" "512:icon_256x256@2x" \
            "512:icon_512x512" "1024:icon_512x512@2x"; do
    size="${spec%%:*}"
    name="${spec##*:}"
    if ! sips -z "$size" "$size" "$base" --out "$iconset/$name.png" >/dev/null 2>&1; then
        # A size that cannot be produced is a softer icon at that size, not a reason to fail the build.
        missing=$((missing + 1))
    fi
done

[[ "$missing" == "0" ]] || echo "note: $missing icon size(s) could not be generated"
iconutil -c icns "$iconset" -o "$app/Contents/Resources/josour.icns"

# ---------------------------------------------------------------- Info.plist
#
# CFBundleIdentifier matches StartupRegistration.LaunchAgentLabel, so "start at login" writes a LaunchAgent
# whose label and whose program agree about what this application is.
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>Josour</string>
    <key>CFBundleDisplayName</key>
    <string>Josour</string>
    <key>CFBundleIdentifier</key>
    <string>com.josour.client</string>
    <key>CFBundleExecutable</key>
    <string>Josour</string>
    <key>CFBundleIconFile</key>
    <string>josour.icns</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$version</string>
    <key>CFBundleVersion</key>
    <string>$version</string>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.utilities</string>
    <!-- Arabic first, which is what the interface defaults to; macOS reads this when it picks a language. -->
    <key>CFBundleDevelopmentRegion</key>
    <string>ar</string>
    <key>CFBundleLocalizations</key>
    <array>
        <string>ar</string>
        <string>en</string>
    </array>
</dict>
</plist>
PLIST

plutil -lint "$app/Contents/Info.plist" >/dev/null || { echo "the generated Info.plist is not valid" >&2; exit 1; }

# ---------------------------------------------------------------- check what was assembled

# The check that matters is not "how many files" but "can it draw". Avalonia needs these two at run time, and
# their absence is not a build error — it is a window that never opens, which is what shipped the first time.
for lib in libSkiaSharp.dylib libHarfBuzzSharp.dylib; do
    [[ -f "$app/Contents/MacOS/$lib" ]] || {
        echo "$lib is missing from the bundle: the app would fail before drawing anything." >&2
        exit 1
    }
done

# And the app must actually start. Launching it headless for a moment catches a bundle that assembles cleanly
# and still dies on a missing dependency — the only way to know is to run it.
if ! "$app/Contents/MacOS/Josour" --uninstall-notifications >/dev/null 2>&1; then
    echo "the bundled executable could not run (tried --uninstall-notifications, which exits immediately)." >&2
    exit 1
fi

files="$(find "$app" -type f | wc -l | tr -d ' ')"

size="$(du -sh "$app" | cut -f1)"
echo
echo "app    : $app"
echo "size   : $size ($files files)"
echo "arch   : $(lipo -archs "$app/Contents/MacOS/Josour" 2>/dev/null || echo "$arch")"

# ---------------------------------------------------------------- dmg

if [[ "$make_dmg" == "1" ]]; then
    dmg="$out_dir/Josour-$version-$arch.dmg"
    rm -f "$dmg"
    stage="$staging/dmg"
    mkdir -p "$stage"
    cp -R "$app" "$stage/"
    # The Applications symlink is the whole convention: open the disk image, drag the icon onto the folder.
    ln -s /Applications "$stage/Applications"
    hdiutil create -volname "Josour $version" -srcfolder "$stage" -ov -format UDZO -quiet "$dmg"
    echo "dmg    : $dmg ($(du -sh "$dmg" | cut -f1))"
fi

echo
echo "UNSIGNED (ADR-0011). On another Mac the first launch must be: right-click the app, choose Open, then"
echo "confirm. Double-clicking it first will only say it cannot be opened, with no way forward in the dialog."
echo
echo "The icon is generated from a 64x64 source, so it is soft above that size. A 1024x1024 master in"
echo "client/src/Josour.App/Assets is what fixes it."
