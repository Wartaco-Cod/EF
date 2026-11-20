#!/usr/bin/env python3
"""Turns the Meshy floor images in art_source/world (tools/art/meshy_world.py) into seamless ground tiles.

    python tools/art/world_import.py            # writes assets/tiles/meshy_<name>.png (1024 px, drawn 1:1)

Seamless by offset blending: the image is mixed with a copy rolled by half its size, weighted by a mask that is
1 in the middle and 0 at the edges, so every edge comes from the rolled copy's interior and wraps continuously.
Levels are matched between the two copies first so the blend does not show as a lighter or darker cross.
"""
import json, os
import numpy as np
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SIZE = 1024
TONE = 0.82      # darker than the photo-like source so figures read clearly on top
CONTRAST = 0.85

def seamless(img: Image.Image) -> Image.Image:
    a = np.asarray(img.convert("RGB"), dtype=np.float32)
    h, w = a.shape[:2]
    b = np.roll(a, (h // 2, w // 2), axis=(0, 1))
    y, x = np.mgrid[0:h, 0:w]
    m = (np.sin(np.pi * (y + 0.5) / h) * np.sin(np.pi * (x + 0.5) / w)) ** 0.6
    m = m[..., None]
    out = a * m + b * (1 - m)
    # Blending two textures lowers contrast where both mix: restore the local contrast to the original's.
    mean = out.mean(axis=(0, 1)); std = out.std(axis=(0, 1)); std0 = a.std(axis=(0, 1))
    out = (out - mean) * (std0 / np.maximum(std, 1e-3)) * CONTRAST + a.mean(axis=(0, 1))
    out *= TONE
    return Image.fromarray(np.clip(out, 0, 255).astype(np.uint8))

def main():
    m = json.load(open(os.path.join(ROOT, "art_source", "world", "manifest.json")))
    for name, it in m["items"].items():
        if it["kind"] != "floor" or not it.get("file"): continue
        img = Image.open(os.path.join(ROOT, it["file"]))
        tile = seamless(img).resize((SIZE, SIZE), Image.LANCZOS)
        dest = os.path.join(ROOT, "assets", "tiles", f"meshy_{name}.png")
        tile.save(dest, optimize=True)
        print("tile", os.path.relpath(dest, ROOT))

if __name__ == "__main__":
    main()
