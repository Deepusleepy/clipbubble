#!/bin/zsh
# Builds ClipBubble.app, installs it to ~/Applications and relaunches it.
set -e
cd "$(dirname "$0")"
APP=ClipBubble.app
rm -rf $APP && mkdir -p $APP/Contents/MacOS $APP/Contents/Resources
cp Info.plist $APP/Contents/
cp Assets/AppIcon.icns $APP/Contents/Resources/
swiftc -O -swift-version 5 Sources/*.swift -o $APP/Contents/MacOS/ClipBubble
codesign --force -s - $APP
# Quit the running app, but not `ClipBubble --mcp` servers that agents are using.
pkill -f 'ClipBubble.app/Contents/MacOS/ClipBubble$' || true
rm -rf ~/Applications/$APP && cp -R $APP ~/Applications/
open ~/Applications/$APP
