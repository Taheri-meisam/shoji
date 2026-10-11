#!/usr/bin/env bash
# Screenshot regression test for the themes: renders every theme × every page of a fixed test config with the Linux
# edition's headless renderer and made-up data (SHOJI_DEMO=1, src/Shoji.Linux/Platform/DemoData.cs), and compares each
# picture with tests/regression/baselines/<theme>-p<N>.png. Most themes and tiles are shared logic, so this also catches
# theme breakage that would show on Windows.
#
#   scripts/theme-regression.sh                     build, render, compare; exit 1 when a picture differs
#   scripts/theme-regression.sh --update            render and overwrite the baselines (after an intended change)
#   scripts/theme-regression.sh --themes tokyo,sumi only these themes (also --pages 1,3)
#   scripts/theme-regression.sh --dll path/shoji.dll  use that build instead of building
#   options: --jobs N (default 4)  --out DIR (default a temp folder)  --wait S (default 3)
#
# Needs: the .NET SDK (~/.dotnet or on PATH), gcc or cc (tests/regression/fixedclock.c freezes the wall clock at
# 2026-10-08 14:03:43 so calendars and "5 min ago" match the demo snapshot), python3 with Pillow (compare.py).
# Never opens a window: the renders are headless, each with its own copy of the test config.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REG="$ROOT/tests/regression"

UPDATE=0 THEMES="" PAGES="" DLL="" JOBS=4 OUT="" WAIT=3
while [ $# -gt 0 ]; do
    case "$1" in
        --update) UPDATE=1 ;;
        --themes) THEMES="$2"; shift ;;
        --pages) PAGES="$2"; shift ;;
        --dll) DLL="$2"; shift ;;
        --jobs) JOBS="$2"; shift ;;
        --out) OUT="$2"; shift ;;
        --wait) WAIT="$2"; shift ;;
        -h|--help) sed -n '2,17p' "$0"; exit 0 ;;
        *) echo "Unknown option $1 (see --help)" >&2; exit 2 ;;
    esac
    shift
done

OUT="${OUT:-$(mktemp -d "${TMPDIR:-/tmp}/shoji-regression.XXXXXX")}"
mkdir -p "$OUT/actual" "$OUT/run"
rm -f "$OUT"/actual/*.png "$OUT/render.log"
echo "Output: $OUT"

# .NET: the SDK in ~/.dotnet (as in the README's build notes) or on PATH.
if [ -x "$HOME/.dotnet/dotnet" ]; then
    export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
if [ -z "$DLL" ]; then
    echo "Building the Linux edition…"
    if ! dotnet build "$ROOT/src/Shoji.Linux/Shoji.Linux.csproj" -c Release --artifacts-path "$OUT/build" > "$OUT/build.log" 2>&1; then
        grep -E ' error ' "$OUT/build.log" | sort -u | head -20 >&2
        echo "Build failed (full log: $OUT/build.log)" >&2
        exit 2
    fi
    DLL="$OUT/build/bin/Shoji.Linux/release/shoji.dll"
fi
[ -f "$DLL" ] || { echo "No $DLL" >&2; exit 2; }

CC="$(command -v gcc || command -v cc || true)"
[ -n "$CC" ] || { echo "Needs gcc or cc for tests/regression/fixedclock.c" >&2; exit 2; }
"$CC" -shared -fPIC -O2 -o "$OUT/fixedclock.so" "$REG/fixedclock.c"

# Every theme in src/Shoji/UI/Themes.cs, and every page of the test config.
ALL_THEMES="$(grep -oE 'Id = "[^"]+", (Group = "[^"]*", )?Name =' "$ROOT/src/Shoji/UI/Themes.cs" | sed -E 's/Id = "([^"]+)".*/\1/')"
[ -n "$THEMES" ] && ALL_THEMES="$(echo "$THEMES" | tr ',' '\n')"
PAGE_COUNT="$(python3 -c 'import json,sys; print(len(json.load(open(sys.argv[1]))["Pages"]))' "$REG/config/Shoji/config.json")"
ALL_PAGES="$(seq 1 "$PAGE_COUNT")"
[ -n "$PAGES" ] && ALL_PAGES="$(echo "$PAGES" | tr ',' '\n')"

# The fixed moment, in a fixed time zone and language, so dates, world clocks and number formats never move.
export TZ=UTC LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8
export SHOJI_DEMO=1 SHOJI_FAKE_EPOCH="$(date -u -d '2026-10-08 14:03:43' +%s)"
export DLL OUT REG WAIT

render() { # theme page
    local name="$1-p$2" cfg="$OUT/run/$1-p$2"
    for attempt in 1 2; do
        rm -rf "$cfg" && mkdir -p "$cfg"
        cp -r "$REG/config/Shoji" "$cfg/"
        sed -i "s#{{PHOTOS}}#$REG/photos#" "$cfg/Shoji/config.json"
        if XDG_CONFIG_HOME="$cfg" LD_PRELOAD="$OUT/fixedclock.so" timeout 120 \
               dotnet "$DLL" --screenshot "$OUT/actual/$name.png" --size 1280x480 --page "$2" --theme "$1" --wait "$WAIT" \
               > "$cfg/out.txt" 2>&1 && [ -s "$OUT/actual/$name.png" ]; then
            rm -rf "$cfg"
            return 0
        fi
        { echo "== $name (attempt $attempt)"; cat "$cfg/out.txt"; } >> "$OUT/render.log"
    done
    return 0 # compare.py reports it as missing
}
export -f render

JOBLIST=""
NAMES=""
for t in $ALL_THEMES; do
    for p in $ALL_PAGES; do
        JOBLIST+="$t $p"$'\n'
        NAMES+="$t-p$p.png,"
    done
done
COUNT="$(printf '%s' "$JOBLIST" | grep -c .)"
echo "Rendering $COUNT pictures, $JOBS at a time…"
START=$(date +%s.%N) # (the real clock: only the renders' clock is frozen)
printf '%s' "$JOBLIST" | xargs -P "$JOBS" -L 1 bash -c 'render "$0" "$1"'
echo "Rendered in $(python3 -c "import sys; print(round(float(sys.argv[2]) - float(sys.argv[1])))" "$START" "$(date +%s.%N)") s"

ARGS=(--baselines "$REG/baselines" --actual "$OUT/actual" --out "$OUT" --names "$NAMES")
[ "$UPDATE" = 1 ] && ARGS+=(--update)
python3 "$REG/compare.py" "${ARGS[@]}"
