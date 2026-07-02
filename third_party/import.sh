#!/usr/bin/env bash
# Clone or update each third-party repository referenced by this project.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

# name|url|branch
REPOS=(
  "ElectricalAge|git@github.com:age-series/ElectricalAge.git|main"
  "revolt|https://github.com/Sukasa/ReVolt.git|main"
  "sparky|git@github.com:Baughn/sparky.git|master"
  "vsapi|https://github.com/anegostudios/vsapi.git|master"
  "vsessentialsmod|https://github.com/anegostudios/vsessentialsmod.git|master"
  "vssurvivalmod|https://github.com/anegostudios/vssurvivalmod.git|master"
)

for entry in "${REPOS[@]}"; do
  IFS='|' read -r name url branch <<< "$entry"
  if [ -d "$name/.git" ]; then
    echo "==> Updating $name"
    git -C "$name" fetch origin
    git -C "$name" checkout "$branch"
    git -C "$name" pull --ff-only origin "$branch"
  else
    echo "==> Cloning $name"
    git clone --branch "$branch" "$url" "$name"
  fi
done
