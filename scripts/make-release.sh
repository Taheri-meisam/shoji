#!/bin/sh
# Release helper, run once the dist/ files exist (scripts/publish.ps1, the Linux tarball, scripts/build-deb.sh):
# writes dist/SHA256SUMS and dist/latest.json (the manifest Shoji's update check reads), and prints the
# "gh release create" command.
#
#   scripts/make-release.sh OWNER/REPO                 files will be on GitHub release v<version> of OWNER/REPO
#   scripts/make-release.sh https://example.com/shoji  files will be at that URL (any https host)
#   options:  --notes FILE     release notes (Markdown) for latest.json and the GitHub release
#             --repo OWNER/REPO  the GitHub repo for the package manifests when the base URL isn't GitHub
#             --packages       also fill the winget and scoop manifests (packaging/) into dist/packages/
#   DIST=/some/folder picks another dist folder.
#
# Users point Settings → About & install → Update source at "OWNER/REPO", or at the URL of latest.json
# (on GitHub: https://github.com/OWNER/REPO/releases/latest/download/latest.json).
set -eu
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIST="${DIST:-$ROOT/dist}"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/src/Shoji/Shoji.csproj" | head -n 1)"
[ -n "$VERSION" ] || { echo "No <Version> in src/Shoji/Shoji.csproj" >&2; exit 1; }

BASE="" REPO="" NOTES="" PACKAGES=0
while [ $# -gt 0 ]; do
    case "$1" in
        --notes) NOTES="$2"; shift 2 ;;
        --repo) REPO="$2"; shift 2 ;;
        --packages) PACKAGES=1; shift ;;
        -h|--help) sed -n '2,17p' "$0"; exit 0 ;;
        *) BASE="$1"; shift ;;
    esac
done
[ -n "$BASE" ] || { echo "Usage: $0 OWNER/REPO | BASE-URL [--notes FILE] [--repo OWNER/REPO] [--packages]" >&2; exit 1; }

case "$BASE" in
    http://*|https://*)
        BASE="${BASE%/}"
        # A github.com release URL names the repo too.
        if [ -z "$REPO" ]; then
            REPO="$(printf '%s' "$BASE" | sed -n 's|^https://github.com/\([^/]*/[^/]*\)/releases/.*|\1|p')"
        fi
        ;;
    */*)
        REPO="$BASE"
        BASE="https://github.com/$REPO/releases/download/v$VERSION"
        ;;
    *) echo "Not an URL or OWNER/REPO: $BASE" >&2; exit 1 ;;
esac
case "$BASE" in
    https://*) ;;
    *) echo "Note: $BASE isn't https; Shoji only downloads updates over https (or from this PC itself)." >&2 ;;
esac

cd "$DIST"
FILES=""
for f in Shoji-Setup.exe Shoji-portable.zip Shoji-linux-x64.tar.gz "shoji_${VERSION}_amd64.deb"; do
    [ -f "$f" ] && FILES="$FILES $f"
done
[ -n "$FILES" ] || { echo "Nothing to release in $DIST" >&2; exit 1; }
# shellcheck disable=SC2086
sha256sum $FILES > SHA256SUMS
sha() { awk -v f="$1" '$2 == f || $2 == "*" f { print $1 }' SHA256SUMS; }

# JSON string from a text file: backslashes, quotes and tabs escaped, lines joined with \n.
json_text() {
    awk 'BEGIN { ORS = "" } { gsub(/\r/, ""); gsub(/\\/, "\\\\"); gsub(/"/, "\\\""); gsub(/\t/, "\\t"); if (NR > 1) print "\\n"; print }' "$1"
}

entry() { # key file
    [ -n "$(sha "$2")" ] || return 0
    printf ',\n  "%s": { "url": "%s/%s", "sha256": "%s" }' "$1" "$BASE" "$2" "$(sha "$2")"
}

{
    printf '{\n  "version": "%s"' "$VERSION"
    [ -n "$NOTES" ] && printf ',\n  "notes": "%s"' "$(json_text "$NOTES")"
    [ -n "$REPO" ] && printf ',\n  "page": "https://github.com/%s/releases/tag/v%s"' "$REPO" "$VERSION"
    entry windows Shoji-Setup.exe
    entry windowsPortable Shoji-portable.zip
    entry linux Shoji-linux-x64.tar.gz
    entry linuxDeb "shoji_${VERSION}_amd64.deb"
    printf '\n}\n'
} > latest.json

echo "Shoji $VERSION"
cat SHA256SUMS
echo "Wrote $DIST/SHA256SUMS and $DIST/latest.json (files expected at $BASE/)"

if [ "$PACKAGES" = 1 ]; then
    OUT="$DIST/packages"
    mkdir -p "$OUT/winget" "$OUT/scoop"
    rm -f "$OUT"/winget/*.yaml   # an earlier run's (maybe under another owner's name)
    OWNER="${REPO%%/*}"
    [ -n "$REPO" ] || echo "Note: no GitHub repo known (--repo OWNER/REPO), so OWNER/REPO stays in the manifests." >&2
    fill() { # template output
        sed -e "s|https://github.com/OWNER/REPO/releases/download/v<version>|$BASE|g" \
            -e "${REPO:+s|OWNER/REPO|$REPO|g}" \
            -e "${OWNER:+s|OWNER|$OWNER|g}" \
            -e "s|<version>|$VERSION|g" \
            -e "s|<date>|$(date +%Y-%m-%d)|g" \
            -e "s|<sha256-setup>|$(sha Shoji-Setup.exe | tr a-f A-F)|g" \
            -e "s|<sha256-portable>|$(sha Shoji-portable.zip)|g" \
            "$1" > "$2"
    }
    for t in "$ROOT"/packaging/winget/*.yaml; do
        name="$(basename "$t")"
        fill "$t" "$OUT/winget/$(printf '%s' "$name" | sed "s|^OWNER|${OWNER:-OWNER}|")"
    done
    fill "$ROOT/packaging/scoop/shoji.json" "$OUT/scoop/shoji.json"
    echo "Wrote the winget manifests to $OUT/winget/ (submit them to microsoft/winget-pkgs under manifests/<letter>/${OWNER:-OWNER}/Shoji/$VERSION/)"
    echo "  and $OUT/scoop/shoji.json (put it in a Scoop bucket)."
fi

echo
echo "Publish on GitHub with:"
printf '  gh release create v%s --title "Shoji %s"' "$VERSION" "$VERSION"
if [ -n "$NOTES" ]; then printf ' --notes-file "%s"' "$NOTES"; else printf ' --generate-notes'; fi
[ -n "$REPO" ] && printf ' --repo %s' "$REPO"
for f in $FILES SHA256SUMS latest.json; do printf ' "%s"' "$DIST/$f"; done
printf '\n'
