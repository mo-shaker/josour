#!/usr/bin/env bash
# Prepare a new Josour release: set the version everywhere, date the changelog, commit and tag.
#
#   scripts/release.sh 2.1.0          # then: git push origin main v2.1.0
#
# What it does, in order:
#   1. refuses unless the working tree is clean and the version is newer than the current one;
#   2. writes the version into the four files scripts/check-versions.sh reads;
#   3. turns "## Unreleased" in CHANGELOG.md into "## 2.1.0 - <today>" (it must have entries);
#   4. commits "release: v2.1.0" and creates the tag v2.1.0.
# Pushing the tag starts .github/workflows/release-client.yml, which builds Josour-2.1.0-win-x64.exe
# and the two macOS disk images and publishes them as the GitHub Release v2.1.0 (docs/releasing.md).
set -euo pipefail
cd "$(dirname "$0")/.."

new="${1:-}"
[[ $new =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "usage: scripts/release.sh MAJOR.MINOR.PATCH" >&2; exit 2; }

current="$(scripts/check-versions.sh)"
newest="$(printf '%s\n%s\n' "$current" "$new" | sort -t. -k1,1n -k2,2n -k3,3n | tail -1)"
[[ $new != "$current" && $newest == "$new" ]] || { echo "$new is not newer than the current $current" >&2; exit 1; }
[[ -z "$(git status --porcelain --untracked-files=no)" ]] || { echo "commit or stash your changes first" >&2; exit 1; }
git rev-parse -q --verify "refs/tags/v$new" >/dev/null && { echo "the tag v$new exists already" >&2; exit 1; }

grep -q '^## Unreleased' CHANGELOG.md || { echo 'CHANGELOG.md has no "## Unreleased" section' >&2; exit 1; }
entries="$(awk '/^## Unreleased/ {on = 1; next} /^## / {on = 0} on && /[^[:space:]]/' CHANGELOG.md)"
[[ -n $entries ]] || { echo 'the "## Unreleased" section of CHANGELOG.md is empty: say what changed first' >&2; exit 1; }

# sed -i differs between GNU and BSD (macOS); a temporary file works on both.
replace() { # replace FILE SED-EXPRESSION
  local tmp
  tmp="$(mktemp)"
  sed "$2" "$1" >"$tmp" && cat "$tmp" >"$1" && rm -f "$tmp"
}
replace client/src/Josour.App/Josour.App.csproj "s:<Version>$current</Version>:<Version>$new</Version>:"
replace backend/pyproject.toml "s/^version = \"$current\"$/version = \"$new\"/"
replace relay/pyproject.toml "s/^version = \"$current\"$/version = \"$new\"/"
replace relay/relay/__init__.py "s/^__version__ = \"$current\"$/__version__ = \"$new\"/"
replace CHANGELOG.md "s/^## Unreleased.*$/## Unreleased\\
\\
## $new - $(date -u +%Y-%m-%d)/"

[[ "$(scripts/check-versions.sh)" == "$new" ]] || { echo "the version did not change everywhere; see git diff" >&2; exit 1; }

git add client/src/Josour.App/Josour.App.csproj backend/pyproject.toml relay/pyproject.toml relay/relay/__init__.py CHANGELOG.md
git commit -q -m "release: v$new"
git tag "v$new"
echo "Version $new committed and tagged. Publish it with:"
echo
echo "  git push origin main v$new"
echo
echo "The release workflow then builds the Windows and macOS apps and attaches them to the release v$new."
