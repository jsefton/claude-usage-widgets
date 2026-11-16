#!/usr/bin/env bash
# Regenerates Resources/AppIcon.icns from scripts/make-icon.swift.
# Only needed when the icon design changes; the .icns is committed.
set -euo pipefail
cd "$(dirname "$0")/.."

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

swift scripts/make-icon.swift "$TMP/AppIcon.iconset"
iconutil -c icns "$TMP/AppIcon.iconset" -o Resources/AppIcon.icns
echo "Wrote Resources/AppIcon.icns"
