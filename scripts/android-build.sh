#!/usr/bin/env bash
# Builds the Android app with the toolchain scripts/android-toolchain.sh installed, and copies the signed APK to
# client/publish/Josour-android.apk (the same fixed name the release assets use).
#
#   bash scripts/android-build.sh            # Debug
#   bash scripts/android-build.sh Release
#
# Extra arguments go to `dotnet build` as they are (for example --no-restore).
set -euo pipefail

CONFIG="${1:-Debug}"
shift || true
ROOT="${JOSOUR_ANDROID_HOME:-$HOME/.josour-android}"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_DIR="$REPO/client/src/Josour.Android"

if [ ! -x "$ROOT/dotnet/dotnet" ]; then
  echo "No Android toolchain in $ROOT; run scripts/android-toolchain.sh first." >&2
  exit 1
fi
export DOTNET_ROOT="$ROOT/dotnet"
export PATH="$ROOT/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

signing=()
if [ -f "$ROOT/debug.keystore" ] && [ -z "${JOSOUR_ANDROID_KEYSTORE:-}" ]; then
  # The test key scripts/android-toolchain.sh made (not the SDK's default under the home folder).
  signing=(-p:AndroidKeyStore=true -p:AndroidSigningKeyStore="$ROOT/debug.keystore" -p:AndroidSigningKeyAlias=androiddebugkey
    -p:AndroidSigningStorePass=android -p:AndroidSigningKeyPass=android)
elif [ -n "${JOSOUR_ANDROID_KEYSTORE:-}" ]; then
  # The release key: path, alias and passwords from the environment, never from a file in the repository.
  signing=(-p:AndroidKeyStore=true -p:AndroidSigningKeyStore="$JOSOUR_ANDROID_KEYSTORE"
    -p:AndroidSigningKeyAlias="${JOSOUR_ANDROID_KEY_ALIAS:?}" -p:AndroidSigningStorePass="env:JOSOUR_ANDROID_STORE_PASS"
    -p:AndroidSigningKeyPass="env:JOSOUR_ANDROID_KEY_PASS")
fi

dotnet build "$PROJECT_DIR/Josour.Android.csproj" -c "$CONFIG" -f net10.0-android \
  -p:AndroidSdkDirectory="$ROOT/android-sdk" -p:JavaSdkDirectory="$ROOT/jdk" ${signing[@]+"${signing[@]}"} "$@"

apk="$(find "$PROJECT_DIR/bin/$CONFIG/net10.0-android" -name '*-Signed.apk' -print | head -1)"
if [ -z "$apk" ]; then
  echo "The build finished but no signed APK was found." >&2
  exit 1
fi
mkdir -p "$REPO/client/publish"
cp "$apk" "$REPO/client/publish/Josour-android.apk"
echo "APK: $REPO/client/publish/Josour-android.apk"
