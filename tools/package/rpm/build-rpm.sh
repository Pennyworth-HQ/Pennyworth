#!/usr/bin/env bash
# Builds an RPM from the tarball made by tools/package/build-linux.sh (it needs the administrator helper in it).
# Usage: tools/package/rpm/build-rpm.sh <pennyworth-VERSION-linux-x64.tar.gz> [outdir]
set -euo pipefail
tarball=$(realpath "${1:?usage: build-rpm.sh <tarball> [outdir]}")
out=$(realpath -m "${2:-$(dirname "$tarball")}")
spec="$(cd "$(dirname "$0")" && pwd)/pennyworth.spec"
name=$(basename "$tarball" .tar.gz)
version=${name#pennyworth-}; version=${version%-linux-x64}
rpmversion=${version//-/_}   # an RPM version cannot contain '-'
top=$(mktemp -d)
trap 'rm -rf "$top"' EXIT
mkdir -p "$top/tree" "$out"
tar -xzf "$tarball" -C "$top/tree"
tree="$top/tree/$name"
[ -x "$tree/libexec/pennyworth/pennyworth-helper" ] || { echo "the tarball has no administrator helper (built with --no-helper?)" >&2; exit 1; }
rpmbuild -bb "$spec" --define "_topdir $top/rpm" --define "pw_version $rpmversion" --define "pw_tree $tree" \
    --define "_rpmdir $out"
find "$out" -name "pennyworth-$rpmversion-*.rpm" -print
