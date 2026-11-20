#!/usr/bin/env python3
"""Meshy player hero: the gothic Templar Knight (the Knight class), concept to rigged, animated model in one run.

    python tools/art/meshy_hero.py run --cap 70 --concepts-only   # 2 concept variants (12 credits)
    python tools/art/meshy_hero.py run --cap 70     # then the chosen one as a rigged, animated model
    python tools/art/meshy_hero.py run --cap 70 --pick 1   # build variant 1 instead of the default 0
    python tools/art/meshy_hero.py status

The look: an avenger of the Temple after the arrests of 1307. Blackened gothic plate with fluted ridges under a
scorched white surcoat with the red cross, a dark hooded cloak, a great helm with a cross-shaped visor, a heavy
broad sword and a kite shield. Thin parts get dropped when Meshy rigs a model, so the prompt asks for a broad blade
and a big shield strapped to the arm. All prompts are our own words; no reference images are sent to Meshy.

Results and task ids live in art_source/hero/manifest.json; a stopped run resumes and never pays twice.
The cap is checked against the live account balance before every paid request.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from meshy_world import call, balance  # noqa: E402  (key from MESHY_API_KEY)

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "art_source" / "hero"
MANIFEST = OUT / "manifest.json"
CONCEPT_COST, MODEL_COST, RIG_COST, MOVE_COST = 6, 30, 5, 3
HEIGHT = 1.85
# idle (combat stance), sword slash, hit reaction, dying backwards; the walk comes with the rig
ANIMS = [89, 219, 178, 189]

HERO = ("the hero of a dark gothic action RPG: a Templar knight avenger in 1307, sworn to avenge his burned brothers. "
        "Blackened gothic plate armour with fluted ridges and pointed edges, a scorched and blood-stained white surcoat "
        "with a large red cross pattee on the chest, a long dark hooded cloak torn at the hem, a flat-topped great helm "
        "with a cross-shaped visor slit, a heavy broad-bladed longsword held firmly in the right hand, a large kite shield "
        "with a red cross strapped to the left forearm, grim and determined, heroic but dark")
STYLE = ("realistic painted game character concept, full body head to feet, single figure, standing in a neutral A-pose, "
         "arms slightly away from the body, centred, plain flat dark grey studio background, soft even light, "
         "no text, no logo, no watermark, no border")
VARIANTS = ["", " Weathered, ash on the armour, embers drifting at his feet."]


def load() -> dict:
    OUT.mkdir(parents=True, exist_ok=True)
    return json.loads(MANIFEST.read_text()) if MANIFEST.exists() else {"start_balance": None, "concepts": [], "build": {}}


def save(m: dict) -> None:
    MANIFEST.write_text(json.dumps(m, indent=2))


def spent(m) -> int:
    return m["start_balance"] - balance()


def wait(path: str, done) -> dict:
    while True:
        t = call("GET", path)
        if t["status"] == "SUCCEEDED": return t
        if t["status"] in ("FAILED", "CANCELED"): raise RuntimeError(f"{path}: {t.get('task_error')}")
        time.sleep(8)


def run(a):
    m = load()
    if m["start_balance"] is None:
        m["start_balance"] = balance(); save(m)
    full = MODEL_COST + RIG_COST + MOVE_COST * len(ANIMS)
    # 1) concepts
    for i, extra in enumerate(VARIANTS):
        if i < len(m["concepts"]): continue
        if spent(m) + CONCEPT_COST + full > a.cap:
            print(f"[budget] skipping concept {i}", flush=True); break
        task = call("POST", "/openapi/v1/text-to-image", {"ai_model": "nano-banana-2", "prompt": (HERO + "." + extra + " " + STYLE)[:1000],
                                                            "aspect_ratio": "3:4"})["result"]
        m["concepts"].append({"task": task}); save(m); print(f"[concept] {i}", flush=True)
    for i, c in enumerate(m["concepts"]):
        if c.get("url"): continue
        t = wait(f"/openapi/v1/text-to-image/{c['task']}", None)
        c["url"] = t["image_urls"][0]
        dest = OUT / f"concept_{i}.png"
        with urllib.request.urlopen(c["url"], timeout=120) as r, open(dest, "wb") as f: f.write(r.read())
        c["file"] = str(dest.relative_to(ROOT)); save(m); print(f"[concept done] {i}", flush=True)
    if a.concepts_only:
        m["spent"] = spent(m); save(m); print(f"[summary] concepts done; spent {m['spent']}", flush=True); return
    # 2) model -> rig -> animations for the picked concept
    b = m["build"]
    if not b.get("model_task"):
        if spent(m) + full > a.cap:
            print(f"[budget] stop before the model: spent {spent(m)}, cap {a.cap}", flush=True); return
        b["pick"] = a.pick
        b["model_task"] = call("POST", "/openapi/v1/image-to-3d", {
            "input_task_id": m["concepts"][a.pick]["task"], "ai_model": "latest", "should_texture": True, "enable_pbr": False,
            "should_remesh": True, "topology": "triangle", "target_polycount": 30000, "pose_mode": "a-pose"})["result"]
        save(m); print("[model]", flush=True)
    if not b.get("model_url"):
        t = wait(f"/openapi/v1/image-to-3d/{b['model_task']}", None)
        b["model_url"] = (t.get("model_urls") or {}).get("glb"); save(m)
    if not b.get("rig_task"):
        b["rig_task"] = call("POST", "/openapi/v1/rigging", {"input_task_id": b["model_task"], "height_meters": HEIGHT})["result"]
        save(m); print("[rig]", flush=True)
    if not b.get("walk_url"):
        r = wait(f"/openapi/v1/rigging/{b['rig_task']}", None).get("result") or {}
        b["rigged_url"] = r.get("rigged_character_glb_url")
        b["walk_url"] = (r.get("basic_animations") or {}).get("walking_glb_url"); save(m)
    if not b.get("anim_task"):
        b["anim_task"] = call("POST", "/openapi/v1/animations", {"rig_task_id": b["rig_task"], "action_ids": ANIMS,
                                                                 "post_process": {"operation_type": "change_fps", "fps": 24}})["result"]
        save(m); print("[animate]", flush=True)
    if not b.get("anim_url"):
        b["anim_url"] = (wait(f"/openapi/v1/animations/{b['anim_task']}", None).get("result") or {}).get("animation_glb_url"); save(m)
    m["spent"] = spent(m); save(m)
    print(f"[summary] hero built; spent {m['spent']}", flush=True)


SCENES = {
    "prologue": "night at a Templar preceptory in Champagne, 1307: the chapel burning with its doors barred, a lone Templar knight in "
                "blackened plate and a scorched white surcoat with a red cross kneeling in the ash before the flames, sword point down "
                "like a cross, royal soldiers with torches as dark silhouettes, sparks rising",
    "sanctuary_first": "a crypt beneath a burned chapel lit by tallow candles, the last Templar brothers gathered among stone pillars and "
                       "tombs, an old grey-bearded preceptor in a white mantle speaking to a knight in blackened plate who stands apart",
    "ledger_of_payns": "an inquisitor's ledger lying open in ash and embers on a catacomb floor, a Templar knight's blackened gauntlet "
                       "tearing out a page of names, a dead inquisitor's black and white robe at the edge of the light",
}
SCENE_STYLE = ("dark gothic oil painting, cinematic wide shot, deep shadows and fire light, muted colours with blood red accents, "
               "dramatic, painterly, no text, no logo, no watermark, no border")


def scenes(a):
    """Cutscene paintings (Text to Image, 6 credits each) to assets/ui/cutscene_<id>.png."""
    m = load()
    if m["start_balance"] is None:
        m["start_balance"] = balance(); save(m)
    sc = m.setdefault("scenes", {})
    for sid, text in SCENES.items():
        if sid in sc: continue
        if spent(m) + CONCEPT_COST > a.cap:
            print(f"[budget] stop before {sid}", flush=True); break
        sc[sid] = {"task": call("POST", "/openapi/v1/text-to-image", {"ai_model": "nano-banana-2", "prompt": f"{text}. {SCENE_STYLE}"[:1000],
                                                                     "aspect_ratio": "16:9"})["result"]}
        save(m); print(f"[scene] {sid}", flush=True)
    for sid, it in sc.items():
        if it.get("file"): continue
        it["url"] = wait(f"/openapi/v1/text-to-image/{it['task']}", None)["image_urls"][0]
        dest = OUT / f"scene_{sid}.png"
        with urllib.request.urlopen(it["url"], timeout=120) as r, open(dest, "wb") as f: f.write(r.read())
        it["file"] = str(dest.relative_to(ROOT)); save(m); print(f"[scene done] {sid}", flush=True)
    m["spent"] = spent(m); save(m)
    print(f"[summary] scenes done; spent {m['spent']}", flush=True)


def status(_):
    print(json.dumps(load(), indent=1))


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("run"); r.add_argument("--cap", type=int, required=True); r.add_argument("--pick", type=int, default=0)
    r.add_argument("--concepts-only", action="store_true"); r.set_defaults(fn=run)
    sc = sub.add_parser("scenes"); sc.add_argument("--cap", type=int, required=True); sc.set_defaults(fn=scenes)
    sub.add_parser("status").set_defaults(fn=status)
    a = p.parse_args(); a.fn(a)


if __name__ == "__main__":
    main()
