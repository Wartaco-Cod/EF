#!/usr/bin/env python3
"""Packs frames from the Blender render rig into one Taco Factory sprite sheet.

Input:  a folder of <anim>_d<dir>_<frame>.png files (what templar_render_rig.py render_sprites() writes)
Output: assets/sprites/<name>.png  (rows = animation x direction, columns = frames)
        assets/sprites/<name>.json (frame size, foot anchor, directions, animations)

  python tools/art/pack_sheet.py renders/templar_sergeant --name class_knight
  python tools/art/pack_sheet.py renders/royal_sergeant --name mon_royal_sergeant --fps attack=14 --hit-frame attack=5

The game picks the sheet up automatically (hot reload) and falls back to the single PNG if no sheet exists.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
FRAME_RE = re.compile(r"^(?P<anim>[a-z0-9_]+)_d(?P<dir>\d+)_(?P<frame>\d{4})\.png$")
DEFAULT_FPS = {"idle": 8, "walk": 12, "run": 14, "attack": 14, "cast": 12, "hit": 12, "death": 10}
LOOPING = {"idle", "walk", "run"}


def parse_kv(items: list[str]) -> dict[str, int]:
    out = {}
    for it in items:
        k, _, v = it.partition("=")
        out[k.strip()] = int(v)
    return out


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("frames", type=Path)
    p.add_argument("--name", required=True, help="output name, e.g. class_knight or mon_royal_sergeant")
    p.add_argument("--out", type=Path, default=ROOT / "assets" / "sprites")
    p.add_argument("--fps", nargs="*", default=[], help="anim=fps overrides")
    p.add_argument("--hit-frame", nargs="*", default=[], help="anim=frame index where the blow lands")
    p.add_argument("--dir0-angle", type=float, default=90.0,
                   help="screen angle in degrees of direction 0 (0 = right, 90 = down/towards camera)")
    p.add_argument("--clockwise", action="store_true", help="directions advance clockwise on screen")
    a = p.parse_args()

    frames: dict[str, dict[int, dict[int, Path]]] = defaultdict(lambda: defaultdict(dict))
    for f in sorted(a.frames.glob("*.png")):
        m = FRAME_RE.match(f.name)
        if m:
            frames[m["anim"]][int(m["dir"])][int(m["frame"])] = f
    if not frames:
        sys.exit(f"No <anim>_d<dir>_<frame>.png files in {a.frames}")

    dirs = max(max(d.keys()) for d in frames.values()) + 1
    first_path = next(path for anim in frames.values() for d in anim.values() for path in d.values())
    fw, fh = Image.open(first_path).size
    cols = max(len(fr) for anim in frames.values() for fr in anim.values())
    anims = sorted(frames, key=lambda n: list(DEFAULT_FPS).index(n) if n in DEFAULT_FPS else 99)
    rows = len(anims) * dirs
    if cols * fw > 8192 or rows * fh > 8192:
        sys.exit(f"Sheet would be {cols * fw}x{rows * fh}; split it or lower SPRITE_W/H (max 8192).")

    sheet = Image.new("RGBA", (cols * fw, rows * fh), (0, 0, 0, 0))
    fps, hits = parse_kv(a.fps), parse_kv(a.hit_frame)
    meta_anims = {}
    lowest_foot = 0
    for ai, anim in enumerate(anims):
        n = 0
        for d in range(dirs):
            for i, path in sorted(frames[anim].get(d, {}).items()):
                img = Image.open(path).convert("RGBA")
                if img.size != (fw, fh):
                    sys.exit(f"{path.name} is {img.size}, expected {(fw, fh)}")
                bbox = img.getchannel("A").getbbox()
                if bbox:
                    lowest_foot = max(lowest_foot, bbox[3])
                sheet.paste(img, (i * fw, (ai * dirs + d) * fh))
                n = max(n, i + 1)
        meta_anims[anim] = {"row": ai * dirs, "frames": n, "fps": fps.get(anim, DEFAULT_FPS.get(anim, 12)),
                            "loop": anim in LOOPING, **({"hitFrame": hits[anim]} if anim in hits else {})}

    step = 360.0 / dirs * (1 if a.clockwise else -1)
    meta = {
        "image": f"{a.name}.png", "frameWidth": fw, "frameHeight": fh, "directions": dirs,
        "directionAngles": [round((a.dir0_angle + d * step) % 360, 2) for d in range(dirs)],
        "anchor": [fw // 2, lowest_foot or fh], "animations": meta_anims,
    }
    a.out.mkdir(parents=True, exist_ok=True)
    sheet.save(a.out / f"{a.name}.png")
    (a.out / f"{a.name}.json").write_text(json.dumps(meta, indent=2))

    # Contact sheet with direction numbers, to check the direction order once per subject.
    preview = sheet.copy()
    draw = ImageDraw.Draw(preview)
    for ai, anim in enumerate(anims):
        for d in range(dirs):
            draw.text((4, (ai * dirs + d) * fh + 4), f"{anim} d{d} {meta['directionAngles'][d]:.0f}°", fill=(255, 255, 0, 255))
    preview.save(a.frames / f"{a.name}_contact.png")
    print(f"Packed {len(anims)} animations x {dirs} directions, frame {fw}x{fh}, sheet {sheet.size[0]}x{sheet.size[1]}")
    print(f"  -> {a.out / (a.name + '.png')}\n  -> check direction order in {a.frames / (a.name + '_contact.png')}")


if __name__ == "__main__":
    main()
