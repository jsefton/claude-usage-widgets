#!/usr/bin/env bash
# Builds ClaudeUsageBar and wraps it in a menu-bar-only .app bundle at build/Claude Usage.app.
# Usage: scripts/build-app.sh [--install]   (--install copies the app to /Applications)
set -euo pipefail
cd "$(dirname "$0")/.."

swift build -c release
BIN="$(swift build -c release --show-bin-path)/ClaudeUsageBar"

APP="build/Claude Usage.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/ClaudeUsageBar"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"

# Ad-hoc sign so macOS will launch it locally and allow the login item.
codesign --force --sign - "$APP"

echo "Built $APP"
if [[ "${1:-}" == "--install" ]]; then
  rm -rf "/Applications/Claude Usage.app"
  cp -R "$APP" /Applications/
  # Nudge Finder/Spotlight to pick up the (possibly new) icon.
  touch "/Applications/Claude Usage.app"
  echo "Installed to /Applications/Claude Usage.app"
  open "/Applications/Claude Usage.app"
fi
