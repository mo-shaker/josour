#!/usr/bin/env bash
# Installs what building the Android app needs, all of it under one folder (default ~/.josour-android), touching nothing
# system-wide and leaving the desktop's own .NET 8 SDK alone:
#
#   dotnet/        the .NET 10 SDK with the android workload
#   jdk/           Microsoft's build of OpenJDK 21
#   android-sdk/   the Android SDK packages the workload asks for (platform, build-tools, platform-tools)
#
# Then it restores the Android project's packages and builds it once, so later builds work offline.
# Re-running it is safe: each step is skipped when its result is already there. Remove the folder to undo everything.
#
#   bash scripts/android-toolchain.sh
set -euo pipefail

ROOT="${JOSOUR_ANDROID_HOME:-$HOME/.josour-android}"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO/client/src/Josour.Android/Josour.Android.csproj"
mkdir -p "$ROOT"

step() { printf '\n==> %s\n' "$*"; }

# 1. The .NET 10 SDK, in its own folder.
if [ ! -x "$ROOT/dotnet/dotnet" ] || ! "$ROOT/dotnet/dotnet" --list-sdks | grep -q '^10\.'; then
  step "Installing the .NET 10 SDK into $ROOT/dotnet"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$ROOT/dotnet-install.sh"
  bash "$ROOT/dotnet-install.sh" --channel 10.0 --install-dir "$ROOT/dotnet" --no-path
fi
export DOTNET_ROOT="$ROOT/dotnet"
export PATH="$ROOT/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# 2. The android workload.
if ! dotnet workload list | grep -q '^android'; then
  step "Installing the android workload"
  dotnet workload install android
fi

# 3. A JDK (21).
JAVA_DIR="$ROOT/jdk"
if [ ! -x "$JAVA_DIR/bin/java" ]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) flavour=macos-aarch64 ;;
    Darwin-x86_64) flavour=macos-x64 ;;
    Linux-x86_64) flavour=linux-x64 ;;
    Linux-aarch64) flavour=linux-aarch64 ;;
    *) echo "Unsupported system: $(uname -s) $(uname -m)" >&2; exit 1 ;;
  esac
  step "Downloading OpenJDK 21 ($flavour)"
  tmp="$(mktemp -d)"
  curl -fsSL "https://aka.ms/download-jdk/microsoft-jdk-21-$flavour.tar.gz" -o "$tmp/jdk.tar.gz"
  tar -xzf "$tmp/jdk.tar.gz" -C "$tmp"
  home="$(dirname "$(dirname "$(find "$tmp" -path '*/bin/java' -type f | head -1)")")"
  rm -rf "$JAVA_DIR"
  mv "$home" "$JAVA_DIR"
  rm -rf "$tmp"
fi

# 4. The Android SDK packages this project needs, installed by the workload itself (and the licences accepted).
step "Installing the Android SDK packages into $ROOT/android-sdk"
dotnet build "$PROJECT" -t:InstallAndroidDependencies -f net10.0-android \
  -p:AndroidSdkDirectory="$ROOT/android-sdk" -p:JavaSdkDirectory="$JAVA_DIR" -p:AcceptAndroidSdkLicenses=True

# 5. A signing key for test builds, kept with the toolchain rather than in the repository. A phone only installs an
#    update signed with the same key, so this one is for testing; releases are signed with the release key.
if [ ! -f "$ROOT/debug.keystore" ]; then
  step "Creating the test signing key $ROOT/debug.keystore"
  "$JAVA_DIR/bin/keytool" -genkeypair -v -keystore "$ROOT/debug.keystore" -storepass android -keypass android \
    -alias androiddebugkey -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=Josour Debug,O=Josour,C=US"
fi

# 6. Restore: the packages land in the NuGet cache, so later builds need no network.
step "Restoring the app's packages"
dotnet restore "$PROJECT" -p:AndroidSdkDirectory="$ROOT/android-sdk" -p:JavaSdkDirectory="$JAVA_DIR"

# 7. A first build. Not fatal: the toolchain is in place whether or not the code of the day compiles.
step "Building the app once"
bash "$REPO/scripts/android-build.sh" Debug || echo "The toolchain is ready; the build itself failed (see above)."

step "Done. Build again with: bash scripts/android-build.sh [Debug|Release]"
