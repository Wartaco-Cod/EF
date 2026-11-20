#!/usr/bin/env python3
"""Adds "metres" (world height covered by one frame) to rendered sheet JSONs that predate the field.
Same rules as frame_size() in tools/art/blender/figures.py. Stdlib only.

    python tools/art/sheet_metres.py
"""
import json, os

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SPAN = 2.6

def metres(name, look):
    if name.startswith("class_") or name.startswith("npc_"): return SPAN
    if name.startswith("boss_") or look.get("boss"): return SPAN * 1.2 * (1.15 if look.get("big") else 1.0)
    return SPAN * (1.12 if look.get("big") else 1.0) * (0.8 if look.get("shape") == "swarm" else 1.0)

def main():
    looks = json.load(open(os.path.join(ROOT, "tools", "art", "placeholder_looks.json"), encoding="utf-8"))
    sprites = os.path.join(ROOT, "assets", "sprites")
    n = 0
    for name, look in looks.items():
        p = os.path.join(sprites, name + ".json")
        if not os.path.exists(p): continue
        d = json.load(open(p))
        if d.get("metres"): continue
        d["metres"] = round(metres(name, look), 3)
        with open(p, "w", newline="\n") as f: json.dump(d, f, indent=1)
        n += 1
    print(f"sheet_metres: updated {n} sheet(s)")

if __name__ == "__main__":
    main()
