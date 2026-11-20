#!/usr/bin/env python3
"""Checks every sprite and tile against the Templar Knights art rules. Exit code 1 on any failure.

  python tools/art/validate_art.py            # whole assets folder
  python tools/art/validate_art.py --strict   # also fail on warnings (use before a release build)

Rules: 32-bit RGBA PNG; transparent background (corners clear) for sprites; something visible;
feet within the bottom 15% of the frame; sheets match their JSON; tiles are opaque and tile seamlessly.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from PIL import Image, ImageChops

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "assets"


def check_sprite(path: Path, errors: list[str], warnings: list[str]) -> None:
    img = Image.open(path)
    rel = path.relative_to(ROOT)
    if img.mode != "RGBA":
        errors.append(f"{rel}: mode {img.mode}, must be RGBA (32-bit truecolor with alpha)")
        return
    w, h = img.size
    alpha = img.getchannel("A")
    corners = [alpha.getpixel(p) for p in ((0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1))]
    if max(corners) > 8:
        errors.append(f"{rel}: background is not transparent (corner alpha {max(corners)})")
    bbox = alpha.getbbox()
    if not bbox:
        errors.append(f"{rel}: image is fully transparent")
        return
    sheet = path.with_suffix(".json")
    if path.stem.endswith("_sheet"):
        return  # atlases are checked through their still's JSON
    if sheet.exists():
        meta = json.loads(sheet.read_text())
        if meta.get("image") and meta["image"] != path.name:
            atlas = path.with_name(meta["image"])
            if not atlas.exists():
                errors.append(f"{rel}: sheet image {meta['image']} is missing")
                return
            w, h = Image.open(atlas).size
        fw, fh = meta["frameWidth"], meta["frameHeight"]
        rows = max(a["row"] for a in meta["animations"].values()) + meta["directions"]
        cols = max(a["frames"] for a in meta["animations"].values())
        if (w, h) != (cols * fw, rows * fh):
            errors.append(f"{rel}: size {w}x{h} does not match its JSON ({cols * fw}x{rows * fh})")
        if len(meta.get("directionAngles", [])) != meta["directions"]:
            errors.append(f"{sheet.relative_to(ROOT)}: directionAngles must list {meta['directions']} angles")
        return
    if path.name.startswith(("class_", "mon_", "boss_", "npc_")) and bbox[3] < h * 0.85:
        warnings.append(f"{rel}: feet end at {bbox[3]}/{h}px; keep them in the bottom 15% so figures stand on the ground")


def check_tile(path: Path, errors: list[str], warnings: list[str]) -> None:
    img = Image.open(path).convert("RGBA")
    rel = path.relative_to(ROOT)
    if img.getchannel("A").getextrema()[0] < 250:
        errors.append(f"{rel}: ground tiles must be fully opaque")
    w, h = img.size
    if w != h or w & (w - 1):
        warnings.append(f"{rel}: tiles should be square powers of two (128 or 256), got {w}x{h}")
    # Seam check: across the wrap (last column -> first column) the change should look like any other pair of
    # neighbouring columns. Compare the wrap difference with the average neighbour difference inside the tile.
    import numpy as np
    arr = np.asarray(img.convert("RGB"), dtype=np.float32)
    inner_x = np.abs(np.diff(arr, axis=1)).mean()
    inner_y = np.abs(np.diff(arr, axis=0)).mean()
    wrap_x = np.abs(arr[:, -1] - arr[:, 0]).mean()
    wrap_y = np.abs(arr[-1] - arr[0]).mean()
    ratio = max(wrap_x / max(inner_x, 1e-3), wrap_y / max(inner_y, 1e-3))
    if ratio > 3.0:
        warnings.append(f"{rel}: visible seam likely (wrap difference {ratio:.1f}x a normal neighbour difference)")


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--strict", action="store_true")
    a = p.parse_args()
    errors: list[str] = []
    warnings: list[str] = []
    for f in sorted((ASSETS / "sprites").glob("*.png")):
        check_sprite(f, errors, warnings)
    for f in sorted((ASSETS / "tiles").glob("*.png")):
        check_tile(f, errors, warnings)
    for w in warnings:
        print("WARN  " + w)
    for e in errors:
        print("FAIL  " + e)
    print(f"Art check: {len(errors)} errors, {len(warnings)} warnings")
    return 1 if errors or (a.strict and warnings) else 0


if __name__ == "__main__":
    sys.exit(main())
