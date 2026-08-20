#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(dirname "$SCRIPT_DIR")"
exec dotnet run --project "$REPO_DIR/Sts2McpBridge.Server.csproj" -- --daemon "$@"
