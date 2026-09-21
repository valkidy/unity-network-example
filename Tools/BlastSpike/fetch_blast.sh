#!/usr/bin/env bash
# Fetches the pinned Blast SDK source into ./blast and applies the local patches.
set -euo pipefail

BLAST_COMMIT=da950a3537927784951853c66618036f332ca0ce  # Blast 5.0.6, NVIDIA-Omniverse/PhysX main
here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

git -C "$work" init -q physx
git -C "$work/physx" remote add origin https://github.com/NVIDIA-Omniverse/PhysX.git
git -C "$work/physx" sparse-checkout set blast
git -C "$work/physx" fetch -q --depth 1 --filter=blob:none origin "$BLAST_COMMIT"
git -C "$work/physx" checkout -q FETCH_HEAD

rm -rf "$here/blast"
cp -R "$work/physx/blast" "$here/blast"
for patch in "$here"/patches/*.patch; do
    patch -d "$here" -p1 --quiet < "$patch"
    echo "applied $(basename "$patch")"
done
echo "blast source ready in $here/blast"
