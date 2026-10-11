#!/bin/sh
# Installs Shoji for the current user (no root needed): ~/.local/opt/shoji, the app menu, and optionally start at login.
#   ./install.sh            install / update
#   ./install.sh --remove   uninstall (settings in ~/.config/Shoji stay)
set -e
DEST="$HOME/.local/opt/shoji"
HERE="$(cd "$(dirname "$0")" && pwd)"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
CONFIG="${XDG_CONFIG_HOME:-$HOME/.config}"
# Where the single-file binary unpacks its native libraries (one folder per build, ~12 MB each).
EXTRACT="${DOTNET_BUNDLE_EXTRACT_BASE_DIR:-$HOME/.net}/shoji"

# Asks a running Shoji to quit (it saves its history on SIGTERM) and waits up to 5 s for it to be gone, so its
# binary can be replaced and a new start doesn't just hand over to the copy that is still shutting down.
stop_running() {
    pkill -u "$(id -u)" -x shoji 2>/dev/null || return 0
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        pgrep -u "$(id -u)" -x shoji >/dev/null 2>&1 || return 0
        sleep 0.5
    done
    pkill -9 -u "$(id -u)" -x shoji 2>/dev/null || true
}

# Removes the unpacked libraries of older builds. A folder that some process still has mapped is kept.
clean_extracted() {
    [ -d "$EXTRACT" ] || return 0
    in_use="$(cat /proc/[0-9]*/maps 2>/dev/null | awk -v b="$EXTRACT/" 'index($6, b) == 1 { p = substr($6, length(b) + 1); sub("/.*", "", p); print b p }' | sort -u)"
    for d in "$EXTRACT"/*; do
        [ -d "$d" ] || continue
        printf '%s\n' "$in_use" | grep -qxF "$d" && continue
        rm -rf "$d"
    done
    rmdir "$EXTRACT" 2>/dev/null || true
}

if [ "$1" = "--remove" ]; then
    [ -x "$DEST/shoji" ] && "$DEST/shoji" --uninstall || true
    stop_running
    # Also when the binary is already gone (or failed): the menu entry, icon and login item it would have removed.
    rm -f "$DATA/applications/shoji.desktop" "$CONFIG/autostart/shoji.desktop" "$DATA/icons/hicolor/256x256/apps/shoji.png"
    rm -rf "$DEST" "$HOME/.local/bin/shoji"
    clean_extracted
    echo "Shoji removed."
    exit 0
fi

stop_running   # an older copy that's running
mkdir -p "$DEST" "$HOME/.local/bin"
# Copy aside and move into place: overwriting a binary that is still running fails with "Text file busy".
cp "$HERE/shoji" "$DEST/shoji.new"
chmod +x "$DEST/shoji.new"
mv -f "$DEST/shoji.new" "$DEST/shoji"
# Licence texts: Shoji's own and those of the bundled fonts and icons.
for f in "$HERE"/LICENSE* "$HERE"/OFL-*.txt "$HERE"/NOTICE* "$HERE"/THIRD-PARTY*; do
    if [ -f "$f" ]; then cp "$f" "$DEST/"; fi
done
ln -sf "$DEST/shoji" "$HOME/.local/bin/shoji"
clean_extracted

printf "Start Shoji when you sign in? [Y/n] "
read -r answer || answer=y
case "$answer" in
    [nN]*)
        "$DEST/shoji" --install || true
        rm -f "$CONFIG/autostart/shoji.desktop"   # "no" also turns off a login item from an earlier install
        ;;
    *) "$DEST/shoji" --install --autostart-on || true ;;
esac

echo "Starting Shoji… (it opens on your smallest secondary screen; settings are in the tray menu and the app grid)"
nohup "$DEST/shoji" --settings >/dev/null 2>&1 &
