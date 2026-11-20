#!/usr/bin/env python3
"""Losslessly shrinks the rendered PNGs (sprite sheets, stills, icons, tiles, ui).

    python tools/art/optimize_png.py            # every PNG under assets/
    python tools/art/optimize_png.py assets/sprites

Zeroes the colour of fully transparent pixels (invisible, but noise there defeats compression) and re-saves
with maximum zlib compression. What is drawn on screen is unchanged. Blender's renders shrink by about 60%.
"""
import os, sys
import numpy as np
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

def optimize(path):
    before = os.path.getsize(path)
    img = Image.open(path)
    img.load()
    if img.mode == "RGBA":
        a = np.asarray(img).copy()
        a[a[..., 3] == 0] = 0
        img = Image.fromarray(a, "RGBA")
    tmp = path + ".tmp"
    img.save(tmp, format="PNG", optimize=True)
    after = os.path.getsize(tmp)
    if after < before:
        os.replace(tmp, path)
        return before, after
    os.remove(tmp)
    return before, before

def main():
    roots = sys.argv[1:] or [os.path.join(ROOT, "assets")]
    tb = ta = 0
    for r in roots:
        for d, _, files in os.walk(r):
            for f in files:
                if f.lower().endswith(".png"):
                    b, a = optimize(os.path.join(d, f)); tb += b; ta += a
    print(f"optimize_png: {tb / 1e6:.1f} MB -> {ta / 1e6:.1f} MB")

if __name__ == "__main__":
    main()
