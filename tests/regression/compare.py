#!/usr/bin/env python3
"""Compares the theme regression screenshots with the baselines and writes an HTML report.

Used by scripts/theme-regression.sh (run that, not this). A picture passes when at most --percent of its pixels
differ from the baseline by more than --channel in any colour channel (anti-aliasing and font hinting can wobble a
little; a theme colour, layout or text change cannot hide under that).

  compare.py --baselines DIR --actual DIR --out DIR --names a-p1.png,b-p1.png [--update] [--channel 8] [--percent 0.3]

--update copies the new pictures over the baselines instead (re-saved as optimized PNG to keep the repo small).
Exit code: 0 all passed (or updated), 1 something failed or is missing.
Needs Pillow (python3-pil on Ubuntu).
"""
import argparse
import html
import os
import shutil
import sys

try:
    from PIL import Image, ImageChops
except ImportError:
    sys.exit("compare.py needs Pillow: sudo apt install python3-pil")


def compare(baseline_path, actual_path, diff_path, channel):
    """Returns (differing pixels, total pixels, note); writes a diff picture when anything differs."""
    base = Image.open(baseline_path).convert("RGB")
    act = Image.open(actual_path).convert("RGB")
    if base.size != act.size:
        return None, act.size[0] * act.size[1], f"size {act.size[0]}x{act.size[1]}, baseline {base.size[0]}x{base.size[1]}"
    r, g, b = ImageChops.difference(base, act).split()
    worst = ImageChops.lighter(ImageChops.lighter(r, g), b)
    mask = worst.point(lambda v: 255 if v > channel else 0)
    differing = mask.histogram()[255]
    total = act.size[0] * act.size[1]
    if differing:
        # The new picture, dimmed and grey, with every differing pixel in red.
        dim = act.convert("L").point(lambda v: v * 0.35).convert("RGB")
        red = Image.new("RGB", act.size, (255, 0, 64))
        Image.composite(red, dim, mask).save(diff_path, optimize=True)
    return differing, total, "" if differing == 0 else f"max channel delta {worst.getextrema()[1]}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--baselines", required=True)
    ap.add_argument("--actual", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--names", required=True, help="comma-separated file names that should have been rendered")
    ap.add_argument("--update", action="store_true")
    ap.add_argument("--channel", type=int, default=8, help="per-channel delta that still counts as equal")
    ap.add_argument("--percent", type=float, default=0.3, help="share of pixels allowed to differ")
    a = ap.parse_args()

    names = [n for n in a.names.split(",") if n]
    os.makedirs(os.path.join(a.out, "diff"), exist_ok=True)
    os.makedirs(os.path.join(a.out, "baseline"), exist_ok=True)
    rows, failed = [], 0
    for name in names:
        actual = os.path.join(a.actual, name)
        baseline = os.path.join(a.baselines, name)
        if not os.path.exists(actual):
            rows.append((name, "missing", "not rendered (see render.log)", None))
            failed += 1
            continue
        if a.update:
            os.makedirs(a.baselines, exist_ok=True)
            Image.open(actual).save(baseline, optimize=True)
            rows.append((name, "updated", "", None))
            continue
        if not os.path.exists(baseline):
            rows.append((name, "new", "no baseline yet (run with --update)", None))
            failed += 1
            continue
        diff = os.path.join(a.out, "diff", name)
        differing, total, note = compare(baseline, actual, diff, a.channel)
        share = 100.0 if differing is None else differing * 100.0 / total
        ok = differing is not None and share <= a.percent
        if not ok:
            failed += 1
            shutil.copyfile(baseline, os.path.join(a.out, "baseline", name))
        rows.append((name, "pass" if ok else "FAIL", f"{share:.3f}% differ {note}".strip(), share))

    write_report(a, rows, failed)
    word = "updated" if a.update else "passed"
    print(f"{len(names) - failed}/{len(names)} {word}; report: {os.path.join(a.out, 'report.html')}")
    for name, state, note, _ in rows:
        if state not in ("pass", "updated"):
            print(f"  {state:7} {name}  {note}")
    return 1 if failed else 0


def write_report(a, rows, failed):
    esc = html.escape
    bad = [r for r in rows if r[1] not in ("pass", "updated")]
    out = [
        "<!doctype html><meta charset=utf-8><title>Shoji theme regression</title>",
        "<style>body{font:14px system-ui,sans-serif;background:#111;color:#ddd;margin:16px}"
        "img{width:100%;max-width:640px;border:1px solid #333}td{vertical-align:top;padding:4px 8px}"
        ".FAIL,.missing,.new{color:#ff5c7a}.pass,.updated{color:#7bd88f}h2{margin-top:28px}</style>",
        f"<h1>Shoji theme regression: {len(rows) - failed}/{len(rows)} {'updated' if a.update else 'passed'}</h1>",
        f"<p>Tolerance: channel delta &le; {a.channel}, at most {a.percent}% of pixels differing.</p>",
    ]
    for name, state, note, _ in bad:
        out.append(f"<h2 class='{state}'>{esc(name)}: {esc(state)} <small>{esc(note)}</small></h2><table><tr>")
        if os.path.exists(os.path.join(a.out, "baseline", name)):
            out.append(f"<td>baseline<br><img src='baseline/{esc(name)}'></td>")
        if os.path.exists(os.path.join(a.actual, name)):
            out.append(f"<td>now<br><img src='{esc(os.path.relpath(os.path.join(a.actual, name), a.out))}'></td>")
        if os.path.exists(os.path.join(a.out, "diff", name)):
            out.append(f"<td>difference (red)<br><img src='diff/{esc(name)}'></td>")
        out.append("</tr></table>")
    out.append("<h2>All pictures</h2><table>")
    for name, state, note, _ in rows:
        out.append(f"<tr><td class='{state}'>{esc(state)}</td><td>{esc(name)}</td><td>{esc(note)}</td></tr>")
    out.append("</table>")
    with open(os.path.join(a.out, "report.html"), "w", encoding="utf-8") as f:
        f.write("\n".join(out))


if __name__ == "__main__":
    sys.exit(main())
