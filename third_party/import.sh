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

# Decompile Stationeers' game code for reference (see CLAUDE.md).
# Skipped when the game isn't installed; regenerated when the DLL changes.
STATIONEERS_DIR="${STATIONEERS_DIR:-$HOME/.local/share/Steam/steamapps/common/Stationeers}"
STATIONEERS_DLL="$STATIONEERS_DIR/rocketstation_Data/Managed/Assembly-CSharp.dll"
DECOMP_DIR="stationeers-decomp"

if [ ! -f "$STATIONEERS_DLL" ]; then
  echo "==> Skipping Stationeers decompile: $STATIONEERS_DLL not found (set STATIONEERS_DIR to override)"
else
  dll_hash=$(sha256sum "$STATIONEERS_DLL" | cut -d' ' -f1)
  stamp="$DECOMP_DIR/.dll-sha256"
  if [ -f "$stamp" ] && [ "$(cat "$stamp")" = "$dll_hash" ]; then
    echo "==> Stationeers decompile up to date"
  else
    echo "==> Decompiling Stationeers Assembly-CSharp.dll (takes a few minutes)"
    if command -v ilspycmd >/dev/null 2>&1; then
      ILSPY=(ilspycmd)
    else
      ILSPY=(nix run nixpkgs#ilspycmd --)
    fi
    rm -rf "$DECOMP_DIR"
    mkdir -p "$DECOMP_DIR"
    "${ILSPY[@]}" -p -o "$DECOMP_DIR" "$STATIONEERS_DLL"
    echo "$dll_hash" > "$stamp"
    echo "==> Decompiled $(find "$DECOMP_DIR" -name '*.cs' | wc -l) files into third_party/$DECOMP_DIR"
  fi
fi
