#!/bin/bash
# Build AgentTimeline.app from the SwiftPM package.
# Usage: scripts/build-app.sh [debug|release]   (default: release)
set -euo pipefail

CONFIG="${1:-release}"
PKG_DIR="$(cd "$(dirname "$0")/.." && pwd)"
APP_NAME="AgentTimeline"
BUILD_DIR="$PKG_DIR/.build/$CONFIG"
OUT_DIR="$PKG_DIR/dist"
APP="$OUT_DIR/$APP_NAME.app"

cd "$PKG_DIR"
# design/design-tokens.json is the canonical shared spec; embed it as Swift source
# (no resource bundle → no CFBundle lookup at launch).
python3 - <<'EOF'
import json, pathlib
src = pathlib.Path("../design/design-tokens.json").read_text()
json.loads(src)  # validate
out = '// GENERATED from design/design-tokens.json by scripts/build-app.sh — do not edit.\n'
out += 'enum DesignTokensData {\n    static let json = #"""\n%s\n"""#\n}\n' % src.rstrip()
pathlib.Path("Sources/AgentTimeline/UI/DesignTokensData.swift").write_text(out)
EOF
swift build -c "$CONFIG"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp "$BUILD_DIR/$APP_NAME" "$APP/Contents/MacOS/"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>AgentTimeline</string>
    <key>CFBundleIdentifier</key>
    <string>com.litianyi.agent-timeline</string>
    <key>CFBundleName</key>
    <string>Agent Timeline</string>
    <key>CFBundleDisplayName</key>
    <string>Agent Timeline</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>0.1.0</string>
    <key>CFBundleVersion</key>
    <string>1</string>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSHumanReadableCopyright</key>
    <string>© 2026 litianyi</string>
</dict>
</plist>
PLIST

codesign --force --sign - "$APP" 2>/dev/null || true
echo "Built: $APP"
echo "Run:   open \"$APP\""
