#!/bin/bash
# Deploy SpireBridge mod to Slay the Spire 2
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

# Game paths
GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources"
MODS_DIR="$GAME_DIR/mods"

echo "=== SpireBridge Deploy ==="

# Build
echo "Building..."
cd "$PROJECT_DIR"
dotnet build -c Release -o "$PROJECT_DIR/out" 2>&1

# Create mods directory
mkdir -p "$MODS_DIR"

# Copy DLL
echo "Copying SpireBridge.dll..."
cp "$PROJECT_DIR/out/SpireBridge.dll" "$MODS_DIR/"

# Create PCK file
# The PCK must contain mod_manifest.json at res://mod_manifest.json
# Using Godot PCK format — for now we create a minimal one with a script
echo "Creating SpireBridge.pck..."
"$SCRIPT_DIR/create-pck.sh"
cp "$PROJECT_DIR/out/SpireBridge.pck" "$MODS_DIR/"

echo ""
echo "=== Deployed to: $MODS_DIR ==="
echo "Files:"
ls -la "$MODS_DIR/SpireBridge"* 2>/dev/null || true
echo ""
echo "Launch the game to load the mod."
echo "Connect via WebSocket: ws://127.0.0.1:38642/"
