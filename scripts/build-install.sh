#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(dirname "$SCRIPT_DIR")"
USER_PROFILE="${HOME:?HOME is required}"
GAME_MANAGED_DIR="${STS2_MANAGED_DIR:-$USER_PROFILE/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64}"
MODS_DIR="${STS2_MODS_DIR:-$USER_PROFILE/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods}"
INSTALL_DIR="$MODS_DIR/Sts2McpBridge"

dotnet run --project "$REPO_DIR/tests/Sts2McpBridge.Tests.csproj" -c Release
dotnet build "$REPO_DIR/Sts2McpBridge.Server.csproj" -c Release
dotnet build "$REPO_DIR/Sts2McpBridge.Mod.csproj" -c Release -p:Sts2ManagedDir="$GAME_MANAGED_DIR"
mkdir -p "$INSTALL_DIR"
rm -f "$INSTALL_DIR/Sts2McpBridge.Core.dll" "$INSTALL_DIR/Sts2McpBridge.Server.dll"
install -m 0644 "$REPO_DIR/bin/Release/net9.0/Sts2McpBridge.dll" "$INSTALL_DIR/Sts2McpBridge.dll"
install -m 0644 "$REPO_DIR/Sts2McpBridge.json" "$INSTALL_DIR/Sts2McpBridge.json"
printf 'Installed mod files to %s\n' "$INSTALL_DIR"
