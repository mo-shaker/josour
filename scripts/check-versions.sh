#!/usr/bin/env bash
# Josour has one version number, written in four files. Print it, or fail if they disagree.
#
#   scripts/check-versions.sh            # prints e.g. 2.0.1
#
# scripts/release.sh changes all four together; the release workflow runs this before it builds.
set -euo pipefail
cd "$(dirname "$0")/.."

read_version() {
  case "$1" in
    *.csproj) sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$1" | head -1 ;;
    *.toml) sed -n 's/^version = "\(.*\)"$/\1/p' "$1" | head -1 ;;
    *.py) sed -n 's/^__version__ = "\(.*\)"$/\1/p' "$1" | head -1 ;;
  esac
}

primary=client/src/Josour.App/Josour.App.csproj
version="$(read_version "$primary")"
status=0
for file in backend/pyproject.toml relay/pyproject.toml relay/relay/__init__.py; do
  found="$(read_version "$file")"
  if [[ -z $found ]]; then
    echo "no version found in $file" >&2
    status=1
  elif [[ $found != "$version" ]]; then
    echo "version mismatch: $file has $found, $primary has $version" >&2
    status=1
  fi
done
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "\"$version\" in $primary is not a MAJOR.MINOR.PATCH version" >&2; status=1; }
((status == 0)) || exit 1
echo "$version"
