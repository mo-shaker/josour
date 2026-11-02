# Releasing a new version

Josour has **one version number** for the apps, the server and the relay, in the form `MAJOR.MINOR.PATCH`
([Semantic Versioning](https://semver.org)):

| Change | Example | Bump |
|---|---|---|
| Fixes only, nothing anyone has to do differently | a crash fixed | `2.1.0` → `2.1.1` |
| Something new, and older apps still work with the new server | a new screen, a new server setting | `2.1.1` → `2.2.0` |
| Older apps stop working, or people must do something by hand | a protocol change behind `min_client_version` | `2.2.0` → `3.0.0` |

The number lives in five files, and [`scripts/check-versions.sh`](../scripts/check-versions.sh) fails if they
disagree: `client/src/Josour.App/Josour.App.csproj` (`<Version>`), `client/src/Josour.Android/Josour.Android.csproj`
(`<Version>`; the Android version code is computed from it), `backend/pyproject.toml`, `relay/pyproject.toml` and
`relay/relay/__init__.py`. It is what the exe reports under *Properties → Details*, what the Mac app reports in
*Get Info*, what `/healthz` answers, and what the app sends in its `User-Agent` (which `min_client_version` compares).

## The steps

1. **Say what changed.** Under `## Unreleased` at the top of [CHANGELOG.md](../CHANGELOG.md), add a line for each
   change people will notice. Commit it with the work, as you go.
2. **Make sure `main` is green** — the `ci-backend`, `ci-client`, `ci-relay` and `ci-licenses` workflows on the
   Actions tab.
3. **Cut the release**, from a clean checkout of `main`:

   ```bash
   scripts/release.sh 2.1.1
   git push origin main v2.1.1
   ```

   The script writes the version into the five files, renames `## Unreleased` to `## 2.1.1 - <today>`, commits
   `release: v2.1.1` and tags it `v2.1.1`. It refuses a version that is not newer, a dirty tree, or an empty
   *Unreleased* section.
4. **Wait for the `release-client` workflow** (about 10 minutes). It builds on GitHub's machines and publishes the
   release [`v2.1.1`](https://github.com/mo-shaker/josour/releases) with:

   | File | For |
   |---|---|
   | `Josour-2.1.1-win-x64.exe` and `Josour-win-x64.exe` | Windows 10/11 — one self-contained file |
   | `Josour-2.1.1-arm64.dmg` and `Josour-macos-arm64.dmg` | Macs with Apple silicon |
   | `Josour-2.1.1-x64.dmg` and `Josour-macos-x64.dmg` | Intel Macs |
   | `Josour-2.1.1-android.apk` and `Josour-android.apk` | Android 8 and later, signed with the release key |
   | `SHA256SUMS.txt` | The SHA-256 of every file, to check a copy against |

   The release notes are this version's section of the changelog, followed by download and first-launch notes.
   The files without a version in their name are what the README's links point at:
   `https://github.com/mo-shaker/josour/releases/latest/download/Josour-win-x64.exe` is always the newest release, so
   no link anywhere needs editing.

5. **Update the servers.** On each server, `sudo bash /opt/josour/deploy/install.sh` moves it to the newest release
   tag (backing up first). Servers follow release tags, not `main`, so a commit on `main` reaches no server until it
   is released.

## Rules the workflow enforces

- The tag must match the version in the files (`v2.1.1` ↔ `2.1.1`).
- **A released version is never rebuilt from another commit.** Running the workflow again for a version whose tag
  already exists only works on that same commit (useful when a build step failed for an unrelated reason); for
  anything else it stops and asks for a new version. People compare the SHA-256 of what they were sent, so one
  version must always mean one set of bytes.
- The Windows exe is checked for the release's version (a mismatch is flagged on the run).
- The APK is signed with the release key from the repository secrets; without them the release fails.

## The Android signing key

Android installs an update only over an app signed with **the same key**, so the APK is signed with one key for as
long as the app exists. Lose it and every phone has to uninstall Josour (and sign in again) to take the next version.
Make it once, on your own machine, with the `keytool` that `scripts/android-toolchain.sh` installed:

```bash
~/.josour-android/jdk/bin/keytool -genkeypair -v -keystore josour-release.keystore -alias josour \
  -keyalg RSA -keysize 4096 -validity 36500 -dname "CN=Josour"
```

It asks for a password (use the same one for the key when asked). Then give it to the release workflow as four
repository secrets (*Settings → Secrets and variables → Actions*, or with `gh`):

```bash
base64 -i josour-release.keystore | gh secret set ANDROID_KEYSTORE_BASE64
gh secret set ANDROID_KEY_ALIAS --body josour
gh secret set ANDROID_STORE_PASSWORD     # paste the password
gh secret set ANDROID_KEY_PASSWORD       # the same password
```

Keep `josour-release.keystore` and its password somewhere safe **outside** GitHub as well (a password manager), and
never commit it. To sign a build on your own machine with it: `JOSOUR_ANDROID_KEYSTORE=josour-release.keystore
JOSOUR_ANDROID_KEY_ALIAS=josour JOSOUR_ANDROID_STORE_PASS=… JOSOUR_ANDROID_KEY_PASS=… bash scripts/android-build.sh Release`.

## Without the workflow

To build on your own machine (needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)):
`powershell -ExecutionPolicy Bypass -File scripts\publish-exe.ps1` on Windows, `scripts/publish-app.sh --dmg` on a Mac
(`--arch x64` for an Intel build). The Android app needs its own toolchain: `bash scripts/android-toolchain.sh`
once, then `bash scripts/android-build.sh Release`. They produce the same files the workflow does, but nothing is
published.
