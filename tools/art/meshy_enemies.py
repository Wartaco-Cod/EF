#!/usr/bin/env python3
"""Meshy enemy designs: concept images first (cheap), then 3D models for the approved ones.

    python tools/art/meshy_enemies.py plan                 # what each stage costs (free)
    python tools/art/meshy_enemies.py concepts --cap 150   # Text to Image concept per enemy (nano-banana-2, 6 credits)
    python tools/art/meshy_enemies.py build --cap 400       # textured model + skeleton + animations for BUILD
    python tools/art/meshy_enemies.py status

The look: dark, battle-worn medieval enemies in blackened, jagged plate with grim skull-like helms, torn cloth and
cruel weapons, each still wearing its faction (the king's blue and gold, the Inquisition's black and white, the
undead brothers' torn white and red). Bosses are bigger, heavier and more menacing. All prompts are our own words;
no reference images are sent to Meshy.

Results and task ids live in art_source/enemies/manifest.json; a stopped run resumes and never pays twice.
The cap is enforced against committed task costs and the live account balance before every paid request.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from meshy_world import call, balance  # noqa: E402  (same API helpers; key from MESHY_API_KEY)

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "art_source" / "enemies"
MANIFEST = OUT / "manifest.json"
CONCEPT_MODEL, CONCEPT_COST = "nano-banana-2", 6

STYLE = ("dark medieval fantasy enemy for a top-down action RPG set in France in 1307, battle-worn and grim, "
         "blackened steel with sharp angular jagged edges and layered plates, scratched and dented metal, torn dark cloth, "
         "realistic painted game character concept, full body head to feet, single figure, standing in a neutral A-pose, "
         "centred, plain flat dark grey studio background, soft even light, no text, no logo, no watermark, no border")
BOSS = ("menacing boss: towering and broad, heavier and more ornate armour, spiked and horned silhouette, "
        "faintly glowing eyes in the shadow of the helm, long ragged cloak, cruel oversized weapon")

ENEMIES = {
    # Act I rank and file (humanoids)
    "royal_sergeant": "a king's sergeant: blackened half-plate under a torn blue surcoat with a faded gold fleur-de-lis, kettle helm with a skull-like face guard, long spear",
    "crossbowman": "a crossbowman of the royal guard: blackened brigandine, ragged blue and gold tabard, deep kettle hat shading a grim face, heavy steel crossbow",
    "man_at_arms": "a man-at-arms: blackened mail and angular plate, nasal helm with a grim iron face mask, arming sword and a dented kite shield",
    "bailiffs_enforcer": "a hulking bailiff's enforcer: scarred leather and bolted iron plates, mail coif, a brutal two-handed maul",
    "flagellant": "an emaciated inquisition flagellant: hooded rags, scarred bare back and arms, iron collar, a barbed scourge",
    "confessors_acolyte": "a confessor's acolyte of the Inquisition: black and white robes, deep hood shadowing a pale face, iron collar, a chained book of confessions",
    "iron_sergeant": "an iron sergeant: a massive brute entirely in blackened riveted plate, closed great helm with a narrow eye slit, a great war hammer",
    "sworn_blade": "a sworn blade of the king's secret service: lean, black and violet leathers with jagged plate pauldrons, masked hood, a long narrow blade",
    "torturer": "a torturer of the Question: huge and scarred, black leather executioner's hood, leather apron, bare arms, a hooked hammer and a branding iron",
    "drowned_penitent": "a drowned penitent: undead monk in waterlogged rotting robes, bloated grey-green skin, river weed and rusted chains",
    "quarry_ghoul": "a quarry ghoul: emaciated, hunched, grey-green skin caked in stone dust, long clawed hands, ragged loincloth",
    "chapel_wraith": "a chapel wraith: a floating spectral figure in a tattered translucent shroud, skeletal hands, pale glowing eyes under a hood",
    "fallen_templar": "a fallen undead Templar knight: rusted blackened plate, torn white surcoat with a faded red cross pattee, a skull showing through a broken great helm, notched sword",
    "risen_marshal": "a risen undead Templar marshal: tall, heavy blackened plate, skull-faced great helm with a torn plume, torn white surcoat with a red cross, a war maul",
    # Act I champions and boss
    "boss_corbin": "Corbin the Bailiff: gaunt and cruel in black and blue plate, iron face mask, chain of office, a flanged mace and a ring of keys",
    "boss_sir_odo": "Sir Odo the Turncoat: a traitor knight in blackened jagged plate with a skull-faced helm, a torn surcoat whose cross is slashed through, a serrated greatsword",
    "boss_drowned_commander": "the Drowned Commander: undead knight in barnacle-crusted armour and a waterlogged cloak, dripping weed, a rusted greatsword",
    "boss_foreman_girauld": "Foreman Girauld: a hulking quarry foreman, iron plates bolted over leather, stone-dusted, a giant sledgehammer",
    "boss_brother_anselm": "Brother Anselm the Broken: champion of the fallen Templars, shattered plate wrapped in chains, cracked great helm with burning eyes, a notched greatsword",
    "boss_mere_aveline": "Mere Aveline, the Whispering Widow: a tall gaunt woman in a black mourning veil and tattered gown, long pale fingers, rats swarming at her hem",
    "boss_imbert": "Guillaume Imbert, the Grand Inquisitor: tall and severe, black and white Dominican robes over blackened plate, an iron mask, an eye sigil, a chained tome and an iron staff",
}


# Build stage: concept -> textured model (Image to 3D, 30) -> skeleton (Rigging, 5, includes a walk cycle)
# -> animations (3 per move). Attack picked per weapon; undead lurch with a slow walk.
MODEL_COST, RIG_COST, MOVE_COST = 30, 5, 3
IDLE, COMBAT_IDLE, HIT, DEATH, SLOW_WALK = 0, 89, 178, 189, 119
BUILD = {
    # name: (height m, attack action, idle, extra walk or None)
    "royal_sergeant": (1.85, 240, COMBAT_IDLE, None),      # thrust (spear)
    "torturer": (2.0, 128, COMBAT_IDLE, None),             # heavy hammer swing
    "fallen_templar": (1.9, 219, IDLE, SLOW_WALK),         # sword slash
    "risen_marshal": (2.05, 128, IDLE, SLOW_WALK),         # maul
    "iron_sergeant": (2.1, 128, COMBAT_IDLE, None),
    "sworn_blade": (1.85, 219, COMBAT_IDLE, None),
    "confessors_acolyte": (1.8, 4, IDLE, None),
    "boss_brother_anselm": (2.3, 242, COMBAT_IDLE, SLOW_WALK),  # charged slash
}


def build_cost(name: str) -> int:
    walk = BUILD[name][3]
    return MODEL_COST + RIG_COST + MOVE_COST * (4 + (1 if walk else 0))


def build(a):
    m = load()
    if m["start_balance"] is None:
        m["start_balance"] = balance(); save(m)
    items = m["items"]
    names = [n for n in (a.names.split(",") if a.names else BUILD) if n in BUILD]
    base_spent = m["start_balance"] - balance()
    committed = sum(build_cost(n) for n in names if items.get(n, {}).get("model_task") and not items[n].get("build_failed"))
    start = base_spent
    # 1) textured models from the approved concepts
    for n in names:
        it = items[n]
        if it.get("model_task") or not it.get("concept_task"): continue
        if (start - base_spent) + committed + build_cost(n) > a.cap:
            print(f"[budget] stop before {n}: committed {committed}, cap {a.cap}", flush=True); break
        it["model_task"] = call("POST", "/openapi/v1/image-to-3d", {
            "input_task_id": it["concept_task"], "ai_model": "latest", "should_texture": True, "enable_pbr": False,
            "should_remesh": True, "topology": "triangle", "target_polycount": 30000, "pose_mode": "a-pose"})["result"]
        committed += build_cost(n); save(m); print(f"[model] {n}", flush=True)
    # 2) poll: model -> rig -> animations
    while True:
        busy = 0
        for n in names:
            it = items[n]
            if not it.get("model_task") or it.get("build_failed") or it.get("anim_url"): continue
            try:
                if not it.get("model_done"):
                    t = call("GET", f"/openapi/v1/image-to-3d/{it['model_task']}")
                    if t["status"] == "SUCCEEDED":
                        it["model_done"] = True; it["model_url"] = (t.get("model_urls") or {}).get("glb"); save(m)
                        it["rig_task"] = call("POST", "/openapi/v1/rigging", {"input_task_id": it["model_task"], "height_meters": BUILD[n][0]})["result"]
                        save(m); print(f"[rig] {n}", flush=True)
                    elif t["status"] in ("FAILED", "CANCELED"):
                        it["build_failed"] = f"model: {t.get('task_error')}"; save(m); print(f"[failed] {n} {it['build_failed']}", flush=True); continue
                    busy += 1; continue
                if not it.get("rig_done"):
                    t = call("GET", f"/openapi/v1/rigging/{it['rig_task']}")
                    if t["status"] == "SUCCEEDED":
                        r = t.get("result") or {}
                        it["rig_done"] = True; it["rigged_url"] = r.get("rigged_character_glb_url")
                        it["walk_url"] = (r.get("basic_animations") or {}).get("walking_glb_url")
                        h, atk, idle, walk = BUILD[n]
                        ids = [idle, atk, HIT, DEATH] + ([walk] if walk else [])
                        it["anim_ids"] = ids
                        it["anim_task"] = call("POST", "/openapi/v1/animations", {"rig_task_id": it["rig_task"], "action_ids": ids,
                                                                                 "post_process": {"operation_type": "change_fps", "fps": 24}})["result"]
                        save(m); print(f"[animate] {n}", flush=True)
                    elif t["status"] in ("FAILED", "CANCELED"):
                        it["build_failed"] = f"rig: {t.get('task_error')}"; save(m); print(f"[failed] {n} {it['build_failed']}", flush=True); continue
                    busy += 1; continue
                t = call("GET", f"/openapi/v1/animations/{it['anim_task']}")
                if t["status"] == "SUCCEEDED":
                    it["anim_url"] = (t.get("result") or {}).get("animation_glb_url"); save(m); print(f"[done] {n}", flush=True)
                elif t["status"] in ("FAILED", "CANCELED"):
                    it["build_failed"] = f"animation: {t.get('task_error')}"; save(m); print(f"[failed] {n} {it['build_failed']}", flush=True)
                else: busy += 1
            except RuntimeError as e:
                print(f"[error] {n}: {e}", flush=True)
                if "HTTP 4" in str(e):  # a client error will not fix itself (refused model, bad pose): stop that one
                    it["build_failed"] = str(e)[:300]; save(m)
                else: busy += 1
        if not busy: break
        time.sleep(10)
    m["spent"] = m["start_balance"] - balance(); save(m)
    print(f"[summary] build done; total spent this design run {m['spent']}", flush=True)


def load() -> dict:
    OUT.mkdir(parents=True, exist_ok=True)
    return json.loads(MANIFEST.read_text()) if MANIFEST.exists() else {"start_balance": None, "items": {}}


def save(m: dict) -> None:
    MANIFEST.write_text(json.dumps(m, indent=2))


def prompt_for(name: str) -> str:
    return f"{ENEMIES[name]}. {BOSS + '. ' if name.startswith('boss_') else ''}{STYLE}"[:1000]


def plan(_):
    n = len(ENEMIES)
    print(f"{n} enemies; concepts {n * CONCEPT_COST} credits; full animated models about 50 credits each ({n * 50})")


def concepts(a):
    m = load()
    if m["start_balance"] is None:
        m["start_balance"] = balance(); save(m)
    items = m["items"]
    committed = sum(CONCEPT_COST for it in items.values() if "concept_task" in it and not it.get("concept_failed"))
    for name in ENEMIES:
        it = items.setdefault(name, {})
        if "concept_task" in it and not it.get("concept_failed"): continue
        spent = max(committed, m["start_balance"] - balance())
        if spent + CONCEPT_COST > a.cap:
            print(f"[budget] stop before {name}: spent {spent}, cap {a.cap}", flush=True); break
        it["concept_task"] = call("POST", "/openapi/v1/text-to-image",
                                  {"ai_model": CONCEPT_MODEL, "prompt": prompt_for(name), "aspect_ratio": "3:4"})["result"]
        it.pop("concept_failed", None)
        committed += CONCEPT_COST
        save(m); print(f"[submit] {name}", flush=True)
    while True:
        busy = 0
        for name, it in items.items():
            if "concept_task" not in it or it.get("concept_url") or it.get("concept_failed"): continue
            t = call("GET", f"/openapi/v1/text-to-image/{it['concept_task']}")
            if t["status"] == "SUCCEEDED":
                it["concept_url"] = t["image_urls"][0]; save(m); print(f"[done] {name}", flush=True)
            elif t["status"] in ("FAILED", "CANCELED"):
                it["concept_failed"] = str(t.get("task_error")); save(m); print(f"[failed] {name}", flush=True)
            else: busy += 1
        if not busy: break
        time.sleep(8)
    for name, it in items.items():
        if it.get("concept_url") and not it.get("concept_file"):
            dest = OUT / "concepts" / f"{name}.png"; dest.parent.mkdir(parents=True, exist_ok=True)
            with urllib.request.urlopen(it["concept_url"], timeout=120) as r, open(dest, "wb") as f: f.write(r.read())
            it["concept_file"] = str(dest.relative_to(ROOT)); save(m)
    m["spent"] = m["start_balance"] - balance(); save(m)
    print(f"[summary] spent {m['spent']} of {a.cap}; {sum(1 for i in items.values() if i.get('concept_file'))} concepts", flush=True)


def status(_):
    m = load()
    for n, it in m["items"].items():
        print(f"{n:26s} {'concept' if it.get('concept_file') else it.get('concept_failed') or 'pending'}")
    print("spent", m.get("spent"))


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("plan").set_defaults(fn=plan)
    c = sub.add_parser("concepts"); c.add_argument("--cap", type=int, required=True); c.set_defaults(fn=concepts)
    b = sub.add_parser("build"); b.add_argument("--cap", type=int, required=True, help="credits for this build run")
    b.add_argument("--names", default=""); b.set_defaults(fn=build)
    sub.add_parser("status").set_defaults(fn=status)
    a = p.parse_args(); a.fn(a)


if __name__ == "__main__":
    main()
