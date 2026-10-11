#!/bin/sh
# Builds dist/shoji_<version>_amd64.deb from the Linux build (dist/linux-stage/shoji, or dist/Shoji-linux-x64.tar.gz).
# Installs to /opt/shoji with /usr/bin/shoji, an app menu entry and the icon. Needs dpkg-deb (Debian / Ubuntu).
#   scripts/build-deb.sh
#   DIST=/some/folder OUT=/other/folder scripts/build-deb.sh      read from / write to other folders
# A copy installed this way doesn't update itself: Settings → About points to the new .deb instead.
set -eu
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIST="${DIST:-$ROOT/dist}"
OUT="${OUT:-$DIST}"
MAINTAINER="${MAINTAINER:-Shoji <shoji@users.noreply.github.com>}"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/src/Shoji.Linux/Shoji.Linux.csproj" | head -n 1)"
[ -n "$VERSION" ] || { echo "No <Version> in src/Shoji.Linux/Shoji.Linux.csproj" >&2; exit 1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
PKG="$WORK/pkg"

# The program: the staged single-file binary, else the one in the tarball (which also has the licence texts).
LICENCES=""
if [ -f "$DIST/Shoji-linux-x64.tar.gz" ]; then
    mkdir -p "$WORK/tar"
    tar -xzf "$DIST/Shoji-linux-x64.tar.gz" -C "$WORK/tar"
    LICENCES="$(find "$WORK/tar" -maxdepth 2 -type f \( -name 'LICENSE*' -o -name 'OFL-*' -o -name 'NOTICE*' -o -name 'THIRD-PARTY*' \))"
fi
if [ -f "$DIST/linux-stage/shoji" ]; then
    BIN="$DIST/linux-stage/shoji"
elif [ -f "$WORK/tar/shoji/shoji" ]; then
    BIN="$WORK/tar/shoji/shoji"
else
    echo "No Linux build: expected $DIST/linux-stage/shoji or $DIST/Shoji-linux-x64.tar.gz" >&2
    exit 1
fi
# No tarball: the licence texts come from the source tree (fonts and Material Icons).
[ -n "$LICENCES" ] || LICENCES="$(ls "$ROOT"/src/Shoji/Fonts/OFL-*.txt "$ROOT"/src/Shoji.Linux/Assets/Fonts/LICENSE-*.txt 2>/dev/null)"
# Shoji's own licence, from the source tree when the tarball doesn't have it.
# shellcheck disable=SC2086
if ! printf '%s\n' $LICENCES | grep -q '/LICENSE$' && [ -f "$ROOT/LICENSE" ]; then LICENCES="$LICENCES $ROOT/LICENSE"; fi

install -Dm755 "$BIN" "$PKG/opt/shoji/shoji"
mkdir -p "$PKG/usr/bin"
ln -s /opt/shoji/shoji "$PKG/usr/bin/shoji"
install -Dm644 "$ROOT/src/Shoji.Linux/Assets/shoji.png" "$PKG/usr/share/icons/hicolor/256x256/apps/shoji.png"

# The same entry the app writes for itself (Platform/Installer.cs), pointing at /opt/shoji.
mkdir -p "$PKG/usr/share/applications"
cat > "$PKG/usr/share/applications/shoji.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Shoji
GenericName=PC dashboard
Comment=Dashboard and control deck for a second screen
Exec=/opt/shoji/shoji --settings
Icon=shoji
Categories=System;Monitor;
Keywords=dashboard;monitor;sensors;deck;weather;
StartupNotify=false
StartupWMClass=shoji
EOF

mkdir -p "$PKG/usr/share/doc/shoji"
for f in $LICENCES; do install -m644 "$f" "$PKG/usr/share/doc/shoji/"; done
cat > "$PKG/usr/share/doc/shoji/copyright" <<EOF
Shoji $VERSION: dashboard and control deck for a second screen.
Shoji is under the MIT License (LICENSE).
Bundled fonts are under the SIL Open Font License (OFL-*.txt); Material Icons under the Apache License 2.0
(LICENSE-MaterialIcons.txt). The program includes the .NET runtime and Avalonia (MIT).
EOF

mkdir -p "$PKG/DEBIAN"
# Self-contained .NET + Avalonia on X11: the C/C++ runtime, ICU (globalization), OpenSSL (https), fontconfig and X11.
cat > "$PKG/DEBIAN/control" <<EOF
Package: shoji
Version: $VERSION
Architecture: amd64
Maintainer: $MAINTAINER
Installed-Size: $(du -sk "$PKG" | cut -f1)
Section: utils
Priority: optional
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libicu78 | libicu77 | libicu76 | libicu74 | libicu72 | libicu70, libssl3t64 | libssl3, libfontconfig1, libx11-6, libice6, libsm6
Recommends: x11-utils, wireplumber
Suggests: ydotool, mangohud
Description: Dashboard and control deck for a second screen
 Shoji turns a small secondary screen (a 1280x480 or 1920x480 touch panel, an
 old monitor, a tablet-sized display) into a live PC dashboard and touch control
 deck: CPU, GPU, memory, network, weather, media, clocks and buttons for apps,
 hotkeys and audio. Settings are in the tray menu and the app menu.
EOF
# A umask of 002 would leave group-writable folders, which dpkg accepts but lintian (rightly) flags.
chmod -R go-w "$PKG"

mkdir -p "$OUT"
DEB="$OUT/shoji_${VERSION}_amd64.deb"
dpkg-deb --build --root-owner-group "$PKG" "$DEB" >/dev/null
echo "Built $DEB"
echo "Install:  sudo apt install $DEB      Remove:  sudo apt remove shoji (settings in ~/.config/Shoji stay)"
