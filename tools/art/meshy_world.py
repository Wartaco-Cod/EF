#!/usr/bin/env python3
"""Meshy world art: floor textures and 3D scenery props for chosen zones, under a hard credit cap.

    set MESHY_API_KEY=...                       (or pass it in the environment some other way)
    python tools/art/meshy_world.py plan        # what would be made and what it costs (free)
    python tools/art/meshy_world.py run --cap 500
    python tools/art/meshy_world.py status

Floors: Meshy Text to Image (nano-banana-2). The images are made tileable afterwards by tools/art/world_import.py.
Props:  Meshy Text to 3D, preview (mesh) + refine (2K textures), downloaded as GLB, then rendered to sprites in the
        game camera by tools/art/blender/props.py.

Every task id and result URL is kept in art_source/world/manifest.json: a stopped run resumes without paying twice.
The cap is checked against the account balance before every paid request, so the run can never spend more than
--cap credits in total (failed tasks are refunded by Meshy and do not count). The key is never printed or saved.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

API = "https://api.meshy.ai"
ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "art_source" / "world"
MANIFEST = OUT / "manifest.json"

IMAGE_MODEL, IMAGE_COST = "nano-banana-2", 6
MESH_MODEL, PREVIEW_COST, REFINE_COST = "latest", 20, 10

FLOOR_STYLE = ("seamless tileable texture, viewed from directly above, orthographic top-down, flat even overcast lighting, "
               "no perspective, no horizon, no cast shadows, no objects standing up, no people, no text, no border, "
               "medieval France 1307, realistic, high detail game ground texture")
PROP_STYLE = ("single medieval game prop, 14th-century France, realistic proportions, weathered and worn, muted earth palette, "
              "hand-painted realistic texture, isolated object, no ground plane, no base, no text")

# zone -> floors (name: prompt) and props (name: (prompt, height in metres))
WORLD = {
    "bloodied_fields": {
        "floors": {
            "fields_mud": "harvested wheat field after rain: dark trampled mud, short cut straw stubble rows, scattered loose straw, a few small stones",
            "fields_trampled": "muddy farm track through a harvested field: wet brown earth with hoof prints and cart ruts, flattened straw, small puddles",
        },
        "props": {
            "hay_cart": ("abandoned two-wheeled wooden hay cart, one wheel broken, a little hay left in the bed, long shafts resting on the ground", 1.6),
            "wheat_sheaves": ("stack of bound wheat sheaves leaning together in a field, tied with twine", 1.2),
            "split_fence": ("section of rustic split-rail wooden fence, three posts and two rails, one rail fallen", 1.2),
            "wooden_plough": ("medieval wooden ox plough with iron share, lying in a field", 0.9),
        },
    },
    "preceptory_of_payns": {
        "floors": {
            "courtyard_flags": "worn courtyard of large irregular limestone flagstones with moss in the joints, scorch marks and ash, a scattered few embers",
            "courtyard_cobbles": "rounded cobblestone yard with dirt between the stones, soot and ash patches, trampled straw",
        },
        "props": {
            "stone_well": ("round stone well with a wooden roof, crank and rope bucket", 2.6),
            "wayside_cross": ("weathered standing stone cross with a carved cross pattee at the top, on a stepped stone base", 2.4),
            "barrels_crates": ("cluster of oak barrels and wooden crates bound with iron bands", 1.3),
            "weapon_rack": ("wooden weapon rack holding spears, a sword and a kite shield", 1.8),
            "burned_beams": ("pile of collapsed charred roof beams and burned timbers, blackened and smouldering", 0.9),
        },
    },
    "catacombs_of_the_rule": {
        "floors": {
            "crypt_slabs": "dark crypt floor of old worn rectangular stone slabs, cracked, dust and grit in the joints, damp stains, candle-wax drips",
            "crypt_bones": "ancient catacomb floor of rough packed earth and broken stone, scattered small bone fragments and dust",
        },
        "props": {
            "sarcophagus": ("stone sarcophagus with a carved effigy of a knight lying on the lid, hands on a sword, weathered", 1.0),
            "bone_pile": ("heap of human skulls and long bones stacked in an ossuary pile", 0.8),
            "candelabrum": ("tall wrought iron standing candelabrum with seven half-melted candles, wax drips", 1.9),
            "broken_column": ("broken romanesque stone column, toppled capital lying beside the stump, rubble", 1.4),
        },
    },
}


def key() -> str:
    k = os.environ.get("MESHY_API_KEY", "").strip()
    if not k:
        sys.exit("MESHY_API_KEY is not set.")
    return k


def call(method: str, path: str, body: dict | None = None) -> dict:
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(API + path, data=data, method=method)
    req.add_header("Authorization", f"Bearer {key()}")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    for attempt in range(6):
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                return json.loads(r.read() or b"{}")
        except urllib.error.HTTPError as e:
            detail = e.read().decode(errors="replace")[:300]
            if e.code in (429, 500, 502, 503) and attempt < 5:
                time.sleep(10 * (attempt + 1)); continue
            raise RuntimeError(f"Meshy {method} {path}: HTTP {e.code} {detail}")
        except urllib.error.URLError as e:
            if attempt < 5:
                time.sleep(10); continue
            raise
    raise RuntimeError("Meshy kept failing")


def load() -> dict:
    OUT.mkdir(parents=True, exist_ok=True)
    return json.loads(MANIFEST.read_text()) if MANIFEST.exists() else {"start_balance": None, "items": {}}


def save(m: dict) -> None:
    MANIFEST.write_text(json.dumps(m, indent=2))


def balance() -> int:
    return int(call("GET", "/openapi/v1/balance")["balance"])


class Budget:
    def __init__(self, m: dict, cap: int):
        self.m, self.cap = m, cap
        if m["start_balance"] is None:
            m["start_balance"] = balance(); save(m)

    def spent(self) -> int:
        return self.m["start_balance"] - balance()

    def committed(self) -> int:
        """Planned cost of every task already submitted (failed ones are refunded)."""
        c = 0
        for it in self.m["items"].values():
            if it.get("failed"): continue
            if it["kind"] == "floor" and "task" in it: c += IMAGE_COST
            if it["kind"] == "prop" and "preview" in it: c += PREVIEW_COST + REFINE_COST
        return c

    def allow(self, cost: int, what: str) -> bool:
        s = max(self.spent(), self.committed())
        if s + cost > self.cap:
            print(f"[budget] stop before {what}: spent {s}, next costs {cost}, cap {self.cap}", flush=True)
            return False
        return True


def jobs():
    for zone, spec in WORLD.items():
        for name, prompt in spec["floors"].items():
            yield zone, "floor", name, prompt, None
        for name, (prompt, h) in spec["props"].items():
            yield zone, "prop", name, prompt, h


def plan(_):
    total = 0
    for zone, kind, name, prompt, h in jobs():
        cost = IMAGE_COST if kind == "floor" else PREVIEW_COST + REFINE_COST
        total += cost
        print(f"{zone:24s} {kind:5s} {name:18s} {cost:3d}")
    print(f"total {total} credits")


def run(a):
    m = load()
    b = Budget(m, a.cap)
    items = m["items"]
    stopped = False
    # 1) Submit everything not yet submitted (floors, then prop previews), budget permitting.
    for zone, kind, name, prompt, h in jobs():
        it = items.setdefault(name, {"zone": zone, "kind": kind, "height": h})
        if kind == "floor" and "task" not in it:
            if not b.allow(IMAGE_COST, name): stopped = True; break
            it["task"] = call("POST", "/openapi/v1/text-to-image",
                              {"ai_model": IMAGE_MODEL, "prompt": f"{prompt}. {FLOOR_STYLE}"[:1000], "aspect_ratio": "1:1"})["result"]
            save(m); print(f"[submit] floor {name}", flush=True)
        elif kind == "prop" and "preview" not in it:
            if not b.allow(PREVIEW_COST + REFINE_COST, name): stopped = True; break
            it["preview"] = call("POST", "/openapi/v2/text-to-3d",
                                 {"mode": "preview", "prompt": f"{prompt}, {PROP_STYLE}"[:600], "ai_model": MESH_MODEL,
                                  "should_remesh": True, "topology": "triangle", "target_polycount": 30000})["result"]
            save(m); print(f"[submit] prop {name}", flush=True)
    # 2) Poll until every submitted task is done; refine props as their previews finish.
    while True:
        busy = 0
        for name, it in items.items():
            try:
                if it["kind"] == "floor" and "task" in it and "url" not in it and not it.get("failed"):
                    t = call("GET", f"/openapi/v1/text-to-image/{it['task']}")
                    if t["status"] == "SUCCEEDED":
                        it["url"] = t["image_urls"][0]; it["credits"] = t.get("consumed_credits"); save(m)
                        print(f"[done] floor {name}", flush=True)
                    elif t["status"] in ("FAILED", "CANCELED"):
                        it["failed"] = str(t.get("task_error")); save(m); print(f"[failed] {name}: {it['failed']}", flush=True)
                    else: busy += 1
                elif it["kind"] == "prop" and "preview" in it and not it.get("failed"):
                    if "refine" not in it:
                        t = call("GET", f"/openapi/v2/text-to-3d/{it['preview']}")
                        if t["status"] == "SUCCEEDED":
                            it["preview_credits"] = t.get("consumed_credits")
                            it["refine"] = call("POST", "/openapi/v2/text-to-3d",
                                                {"mode": "refine", "preview_task_id": it["preview"], "enable_pbr": False,
                                                 "texture_resolution": "2k"})["result"]
                            save(m); print(f"[refine] {name}", flush=True)
                            busy += 1
                        elif t["status"] in ("FAILED", "CANCELED"):
                            it["failed"] = str(t.get("task_error")); save(m); print(f"[failed] {name}: {it['failed']}", flush=True)
                        else: busy += 1
                    elif "url" not in it:
                        t = call("GET", f"/openapi/v2/text-to-3d/{it['refine']}")
                        if t["status"] == "SUCCEEDED":
                            it["url"] = (t.get("model_urls") or {}).get("glb"); it["thumb"] = t.get("thumbnail_url")
                            it["credits"] = t.get("consumed_credits"); save(m); print(f"[done] prop {name}", flush=True)
                        elif t["status"] in ("FAILED", "CANCELED"):
                            it["failed"] = str(t.get("task_error")); save(m); print(f"[failed] {name}: {it['failed']}", flush=True)
                        else: busy += 1
            except RuntimeError as e:
                print(f"[error] {name}: {e}", flush=True); busy += 1
        if not busy: break
        time.sleep(10)
    # 3) Download results.
    for name, it in items.items():
        if it.get("url") and not it.get("file"):
            ext = ".png" if it["kind"] == "floor" else ".glb"
            dest = OUT / it["zone"] / (name + ext); dest.parent.mkdir(parents=True, exist_ok=True)
            with urllib.request.urlopen(it["url"], timeout=300) as r, open(dest, "wb") as f: f.write(r.read())
            it["file"] = str(dest.relative_to(ROOT)); save(m)
    m["spent"] = b.spent(); save(m)
    print(f"[summary] spent {m['spent']} of {a.cap} credits; "
          f"{sum(1 for i in items.values() if i.get('file'))} files, {sum(1 for i in items.values() if i.get('failed'))} failed"
          + ("; stopped at the cap" if stopped else ""), flush=True)


def status(_):
    m = load()
    for n, it in m["items"].items():
        print(f"{n:18s} {it['kind']:5s} {'ok' if it.get('file') else it.get('failed') or 'pending'}")
    print("spent", m.get("spent"))


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("plan").set_defaults(fn=plan)
    r = sub.add_parser("run"); r.add_argument("--cap", type=int, required=True); r.set_defaults(fn=run)
    sub.add_parser("status").set_defaults(fn=status)
    a = p.parse_args(); a.fn(a)


if __name__ == "__main__":
    main()
