#!/usr/bin/env bash
set -euo pipefail

USER_PROFILE="${HOME:?HOME is required}"
KNOWLEDGE_BASE="${STS2_KNOWLEDGE_BASE:-$USER_PROFILE/.local/share/sts2-knowledge/spire-codex}"
LANGUAGE="${STS2_KNOWLEDGE_LANG:-zhs}"
ARCHIVE="$KNOWLEDGE_BASE/spire-codex-$LANGUAGE.zip"
OUTPUT="$KNOWLEDGE_BASE/$LANGUAGE"

printf '%s\n' 'Spire Codex uses the PolyForm Noncommercial License 1.0.0.'
printf '%s\n' 'Continue only for a permitted personal/noncommercial use.'
mkdir -p "$OUTPUT"
curl -fL "https://spire-codex.com/api/exports/$LANGUAGE" -o "$ARCHIVE"
unzip -o "$ARCHIVE" -d "$OUTPUT"
shasum -a 256 "$ARCHIVE"
printf 'Knowledge downloaded to %s\n' "$OUTPUT"
