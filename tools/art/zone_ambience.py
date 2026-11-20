"""
Zone atmosphere: writes each zone's "ambience" (light level, colour grade, weather) and "lights", gives the
light-bearing props their light, stands candelabra along the walls of dark interiors, and dresses each zone with
scenery for its biome (trees, rocks, graves, braziers, tents, stalls, grass: tools/art/blender/scenery.py), keeping
exits, NPCs, the waypoint, the arrival point and the paths between them clear.

    python tools/art/zone_ambience.py            (rewrites assets/data/zones/*.json and props.json)

Deterministic: running it twice gives the same files. Hand edits to a zone's ambience survive if you delete the
zone from MOODS below (zones not listed are left alone).
"""
from __future__ import annotations

import json
import math
import os
import random

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ZONES = os.path.join(ROOT, "assets", "data", "zones")
PROPS = os.path.join(ROOT, "assets", "data", "props.json")


def amb(ambient="#FFFFFF", level=1.0, tint="#FFFFFF", sat=1.0, contrast=1.05, vignette=0.35, bloom=0.6,
        particles="", density=1.0, hero=0.0, clouds=0.0, dust="#8A7A64"):
    return {"ambient": ambient, "level": level, "tint": tint, "saturation": sat, "contrast": contrast,
            "vignette": vignette, "bloom": bloom, "particles": particles, "particleDensity": density,
            "heroLight": hero, "clouds": clouds, "dust": dust}


def sound_for(zid, m):
    """Which ambience loop a zone plays (assets/sound/amb_*.ogg)."""
    if m["particles"] == "rain": return "rain"
    if m["particles"] == "snow": return "wind_cold"
    if "forest" in zid: return "forest"
    if zid in ("payns_courtyard", "burned_village_of_payns"): return "fire"
    if zid == "remnant_camp": return "camp"
    if zid == "valois_estate": return "night"
    if zid in ("town_in_shadow", "place_de_greve", "preceptory_of_payns", "royal_garrison", "royal_court_outskirts", "monastery_of_trials"): return "town"
    if m["heroLight"] > 0 and m["level"] < 0.5: return "crypt"
    if m["heroLight"] > 0: return "hall"
    return "wind"


# Interiors are lit by their candles and the heroes' own light; outdoors by the sky.
CRYPT = dict(ambient="#7484A4", level=0.4, tint="#FFF4E8", sat=0.85, contrast=1.1, vignette=0.55, bloom=0.9, particles="dust", hero=400, dust="#6A6660")
HALL = dict(ambient="#D0B490", level=0.66, tint="#FFF0DC", sat=0.95, contrast=1.08, vignette=0.45, bloom=0.8, particles="dust", hero=260, dust="#7A7066")

MOODS = {
    # Act I
    "payns_courtyard": amb("#B07A68", 0.6, "#FFF0E6", 1.05, 1.1, 0.48, 1.0, "embers", 2.0, 260, dust="#5A4A40"),
    "burned_village_of_payns": amb("#C8A890", 0.72, "#FFF0E4", 0.85, 1.08, 0.45, 0.8, "ash", 1.2, 0, 0.35, "#6A5A4C"),
    "bloodied_fields": amb("#E4E2E6", 0.95, "#F4F2F4", 0.82, 1.06, 0.4, 0.5, "", 1, 0, 0.6, "#7A6A54"),
    "preceptory_of_payns": amb("#FFF4E4", 1.0, "#FFF8EE", 1.0, 1.06, 0.35, 0.55, "", 1, 0, 0.45, "#8A7A64"),
    "provins_road": amb("#FFF6E8", 1.02, "#FFF8F0", 1.0, 1.05, 0.33, 0.5, "", 1, 0, 0.6, "#8A7A60"),
    "forest_of_orient": amb("#C8DCB8", 0.9, "#F4FFF0", 0.95, 1.06, 0.42, 0.6, "spores", 1.2, 0, 0.5, "#5A5A40"),
    "frozen_vineyards": amb("#C4D8FF", 0.92, "#EEF6FF", 0.75, 1.08, 0.4, 0.7, "snow", 2.2, 0, 0.3, "#C8D0DC"),
    "quarry_of_saint_blaise": amb("#F4E4CC", 1.0, "#FFF6EA", 0.92, 1.06, 0.38, 0.55, "dust", 1.5, 0, 0.3, "#A8987C"),
    "drowned_commandery": amb("#7A92A8", 0.58, "#EEF6FF", 0.8, 1.1, 0.5, 0.7, "rain", 1.8, 240, 0.4, "#5A6670"),
    "catacombs_of_the_rule": amb(**CRYPT),
    "charnel_hollow": amb(**{**CRYPT, "ambient": "#7A8A70", "particles": "spores"}),
    "sealed_vault_of_payns": amb(**{**CRYPT, "level": 0.34}),
    "commandery_halls": amb(**HALL),
    "sanctuary_of_the_broken_seal": amb(**{**HALL, "level": 0.6, "ambient": "#C8B0A8"}),
    # Act II
    "abandoned_castle": amb("#B8B4C8", 0.8, "#F0F0FF", 0.82, 1.08, 0.45, 0.6, "ash", 0.6, 0, 0.5, "#6A6660"),
    "royal_garrison": amb("#FFF4E4", 0.98, "#FFF8EE", 0.95, 1.06, 0.35, 0.55, "", 1, 0, 0.4, "#8A7A64"),
    "catacomb_network_1": amb(**{**CRYPT, "particles": "spores"}),
    "catacomb_network_2": amb(**{**CRYPT, "particles": "spores", "level": 0.27}),
    "catacomb_network_3": amb(**{**CRYPT, "particles": "spores", "level": 0.24}),
    "hermits_cave": amb(**{**CRYPT, "ambient": "#8A7A6A", "level": 0.32}),
    "inquisition_hq_1": amb(**{**CRYPT, "ambient": "#8A6A5A", "level": 0.38, "particles": "embers", "density": 0.5}),
    "inquisition_hq_2": amb(**{**CRYPT, "ambient": "#8A6A5A", "level": 0.35, "particles": "embers", "density": 0.5}),
    "inquisition_hq_3": amb(**{**CRYPT, "ambient": "#8A6A5A", "level": 0.32, "particles": "embers", "density": 0.6}),
    "town_in_shadow": amb("#B0A0C0", 0.68, "#FFF0E8", 0.9, 1.08, 0.45, 0.8, "", 1, 200, 0.2, "#6A6060"),
    # Act III
    "kings_forest_east": amb("#D0DEB4", 0.92, "#F8FFEE", 1.0, 1.05, 0.4, 0.6, "fireflies", 0.8, 0, 0.5, "#5A5A40"),
    "kings_forest_west": amb("#C0D0A8", 0.86, "#F4FFEE", 1.0, 1.06, 0.42, 0.6, "fireflies", 1.2, 0, 0.5, "#5A5A40"),
    "legates_palace_1": amb(**{**HALL, "level": 0.72, "ambient": "#E8DCC8"}),
    "legates_palace_2": amb(**{**HALL, "level": 0.68, "ambient": "#E8DCC8"}),
    "legates_palace_3": amb(**{**HALL, "level": 0.62, "ambient": "#E0D0C0"}),
    "monastery_of_trials": amb("#FFF0DC", 0.95, "#FFF6EA", 0.95, 1.06, 0.38, 0.6, "dust", 0.6, 0, 0.3, "#8A8070"),
    "royal_court_outskirts": amb("#FFF6E8", 1.02, "#FFF8F0", 1.02, 1.05, 0.33, 0.5, "", 1, 0, 0.55, "#8A7A60"),
    "underground_cathedral_1": amb(**CRYPT),
    "underground_cathedral_2": amb(**{**CRYPT, "level": 0.34}),
    "valois_estate": amb("#FFD8A8", 0.9, "#FFF0DC", 1.05, 1.05, 0.38, 0.7, "fireflies", 0.6, 0, 0.3, "#7A6A50"),
    # Act IV
    "notre_dame": amb(**{**HALL, "level": 0.5, "ambient": "#A8B0D0"}),
    "papal_palace": amb(**{**HALL, "level": 0.58}),
    "royal_palace_1": amb(**{**HALL, "level": 0.6}),
    "royal_palace_2": amb(**{**HALL, "level": 0.56}),
    "royal_palace_3": amb(**{**HALL, "level": 0.52}),
    "place_de_greve": amb("#A890A0", 0.66, "#FFEEE4", 0.88, 1.1, 0.5, 0.9, "embers", 1.0, 180, 0.3, "#6A5A50"),
    "remnant_camp": amb("#8A88B0", 0.55, "#FFF0E0", 0.95, 1.08, 0.5, 0.9, "embers", 0.8, 240, 0, "#5A5048"),
    "tower_of_truth_1": amb(**{**CRYPT, "level": 0.34, "particles": "embers", "density": 0.4}),
    "tower_of_truth_2": amb(**{**CRYPT, "level": 0.32, "particles": "embers", "density": 0.4}),
    "tower_of_truth_3": amb(**{**CRYPT, "level": 0.3, "particles": "embers", "density": 0.5}),
    "tower_of_truth_4": amb(**{**CRYPT, "level": 0.28, "particles": "embers", "density": 0.5}),
    "tower_of_truth_5": amb(**{**CRYPT, "level": 0.25, "particles": "embers", "density": 0.6}),
}

CANDLES = {"colour": "#FFB060", "radius": 320, "intensity": 1.3, "flicker": 0.6, "height": 1.62, "flame": True, "embers": 0.6}
EMBERS = {"colour": "#FF7A30", "radius": 190, "intensity": 0.8, "flicker": 0.8, "height": 0.35, "flame": False, "embers": 2.5}
FIRE = {"colour": "#FF9A40", "radius": 380, "intensity": 1.5, "flicker": 0.9, "height": 0.45, "flame": True, "embers": 6}
SHAFT = {"colour": "#C8D8FF", "radius": 260, "intensity": 0.55, "flicker": 0.0, "height": 0, "flame": False, "embers": 0}

# Zones that get candelabra along their walls (interiors without many lights of their own).
CANDLELIT = {k for k, v in MOODS.items() if v["level"] < 0.75 and v["heroLight"] > 0
             and k not in ("payns_courtyard", "remnant_camp", "place_de_greve", "town_in_shadow", "drowned_commandery", "hermits_cave")}
# Outdoor fires (camp fires, pyres, burning wreckage).
FIRES = {"payns_courtyard": 9, "remnant_camp": 6, "place_de_greve": 4, "burned_village_of_payns": 5}
SHAFTS = {k: 3 for k in CANDLELIT if "cathedral" in k or "notre_dame" in k or "palace" in k}


def keep_clear(z):
    pts = [tuple(z["playerSpawn"])]
    pts += [(e["x"], e["y"]) for e in z.get("exits", [])]
    pts += [(n["x"], n["y"]) for n in z.get("npcs", [])]
    for k in ("waypoint", "bossPosition", "superuniquePosition"):
        if z.get(k): pts.append(tuple(z[k][:2]))
    pts += [(f["x"], f["y"]) for f in z.get("fixedSpawns", []) if "x" in f]
    return pts


def far(p, pts, d):
    return all(math.hypot(p[0] - q[0], p[1] - q[1]) >= d for q in pts)


def candelabra(z, rng):
    w, h = z["width"], z["height"]
    clear = keep_clear(z)
    have = [(d["x"], d["y"]) for d in z.get("decor", [])]
    if z.get("arena"):
        a = z["arena"]; ax, ay, aw, ah = a["x"], a["y"], a["width"], a["height"]
    else:
        ax = ay = aw = ah = None
    out = []
    inset, step = 150, 620
    edges = []
    for x in range(int(inset + step / 2), int(w - inset), step):
        edges += [(x, inset), (x, h - inset)]
    for y in range(int(inset + step / 2), int(h - inset), step):
        edges += [(inset, y), (w - inset, y)]
    for (x, y) in edges:
        x += rng.uniform(-60, 60); y += rng.uniform(-30, 30)
        if not far((x, y), clear, 340) or not far((x, y), have + [(o["x"], o["y"]) for o in out], 260):
            continue
        if ax is not None and ax - 60 <= x <= ax + aw + 60 and ay - 60 <= y <= ay + ah + 60:
            continue
        out.append({"prop": rng.choice(["candelabrum_1", "candelabrum_2"]), "x": round(x), "y": round(y), "flip": rng.random() < 0.5})
    return out


def scatter_lights(z, rng, n, light, margin=300):
    w, h = z["width"], z["height"]
    clear = keep_clear(z)
    out = []
    for _ in range(n * 30):
        if len(out) >= n: break
        p = (rng.uniform(margin, w - margin), rng.uniform(margin, h - margin))
        if far(p, clear, 260) and far(p, [(o["x"], o["y"]) for o in out], 480):
            out.append({"x": round(p[0]), "y": round(p[1]), "light": dict(light)})
    return out


# ----------------------------------------------------------------------------------------------- scenery

# Recipes: (prop, count per million square world units, where, spacing). where: "edge" hugs the borders (frames the
# play area), "any" anywhere, "cluster" in small groups. Grass and flowers are walk-through and may sit anywhere.
TREES = [("oak_tree", 2.2, "edge", 300), ("oak_tree_b", 2.0, "edge", 300), ("pine_tree", 1.6, "edge", 280)]
FOREST = TREES + [("oak_tree", 1.2, "cluster", 280), ("pine_tree", 1.0, "cluster", 260), ("bush", 3.5, "any", 170),
                  ("tree_stump", 0.8, "any", 160), ("fallen_log", 0.6, "any", 220), ("boulder", 0.5, "any", 220),
                  ("grass_tuft", 9, "any", 90), ("grass_flowers", 4, "any", 90)]
FIELDS = [("oak_tree", 0.9, "edge", 320), ("oak_tree_b", 0.7, "edge", 320), ("dead_tree", 0.4, "any", 300), ("bush", 1.6, "any", 170),
          ("rock_cluster", 1.0, "any", 160), ("boulder", 0.4, "any", 220), ("dry_grass", 7, "any", 90), ("grass_tuft", 5, "any", 90)]
ROAD = [("oak_tree", 1.6, "edge", 300), ("oak_tree_b", 1.4, "edge", 300), ("pine_tree", 0.6, "edge", 280), ("bush", 2.2, "edge", 170),
        ("rock_cluster", 0.8, "any", 160), ("tree_stump", 0.5, "any", 160), ("grass_tuft", 7, "any", 90), ("grass_flowers", 4, "any", 90)]
FROST = [("dead_tree", 1.6, "edge", 300), ("pine_tree", 1.0, "edge", 280), ("boulder", 0.7, "any", 220), ("rock_cluster", 1.0, "any", 160),
         ("dry_grass", 5, "any", 90), ("fallen_log", 0.4, "any", 220)]
QUARRY = [("boulder", 2.2, "any", 220), ("rock_cluster", 2.6, "any", 160), ("dead_tree", 0.4, "edge", 300), ("dry_grass", 3, "any", 90),
          ("torch_post", 0.4, "any", 300)]
BURNT = [("burnt_tree", 1.8, "edge", 300), ("burnt_tree", 0.6, "any", 300), ("rock_cluster", 0.8, "any", 160), ("dry_grass", 2.5, "any", 90)]
MARSH = [("dead_tree", 1.4, "edge", 300), ("fallen_log", 0.8, "any", 220), ("graves", 0.5, "any", 260), ("rock_cluster", 0.7, "any", 160),
         ("dry_grass", 5, "any", 90), ("bush", 1.0, "any", 170)]
YARD = [("banner", 0.8, "edge", 360), ("brazier", 0.7, "any", 360), ("torch_post", 0.6, "edge", 320), ("rock_cluster", 0.4, "any", 160),
        ("oak_tree", 0.4, "edge", 320), ("grass_tuft", 2.0, "edge", 90)]
RUIN = [("dead_tree", 1.0, "edge", 300), ("boulder", 0.6, "any", 220), ("rock_cluster", 1.0, "any", 160), ("banner", 0.3, "edge", 360),
        ("dry_grass", 5, "any", 90), ("bush", 0.8, "any", 170)]
CAMP = [("tent", 1.6, "edge", 420), ("brazier", 1.0, "any", 360), ("banner", 0.6, "edge", 360), ("fallen_log", 0.6, "any", 220),
        ("pine_tree", 0.8, "edge", 300), ("grass_tuft", 2.5, "any", 90)]
MARKET = [("market_stall", 1.2, "edge", 420), ("torch_post", 0.8, "edge", 320), ("banner", 0.5, "edge", 360), ("brazier", 0.4, "any", 360)]
GARDEN = [("oak_tree", 1.4, "edge", 300), ("oak_tree_b", 1.2, "cluster", 300), ("bush", 3.0, "any", 170), ("grass_flowers", 8, "any", 90),
          ("grass_tuft", 6, "any", 90)]
CLOISTER = [("graves", 0.8, "edge", 280), ("bush", 1.6, "edge", 170), ("oak_tree_b", 0.5, "edge", 320), ("grass_tuft", 5, "any", 90),
            ("grass_flowers", 2, "any", 90)]
GRAVEYARD = [("graves", 1.6, "any", 280)]
HALLS = [("brazier", 0.35, "edge", 420)]
# Act II, underground and Paris (0.8.1).
CATACOMB = [("ossuary_wall", 0.9, "edge", 420), ("coffin", 0.7, "any", 260), ("bone_pile", 0.9, "any", 220), ("rubble", 0.6, "any", 240),
            ("broken_column", 0.4, "any", 260), ("sarcophagus", 0.3, "any", 300), ("mine_timbers", 0.35, "edge", 420), ("puddle", 0.35, "any", 260)]
def dense(recipe, k):
    """The Act II underground maps are wide (5760 x 3240) open floors; they need more per square metre, packed closer, to feel full."""
    return [(p, d * k, w, round(sp * 0.6)) for p, d, w, sp in recipe]


CATACOMB = dense(CATACOMB, 2.5)
SMUGGLERS = CATACOMB + [("wine_casks", 0.5, "edge", 360), ("smuggler_cache", 0.6, "any", 360), ("barrels_crates", 0.4, "any", 300)]
CAVE = dense([("stalagmites", 1.3, "any", 300), ("stalagmites", 1.0, "edge", 300), ("fungus", 1.4, "any", 200), ("rubble", 0.5, "any", 240),
        ("puddle", 0.6, "any", 260), ("bone_pile", 0.3, "any", 220), ("mine_timbers", 0.2, "edge", 420)], 2.5)
INQUISITION = HALLS + dense([("stone_pillar", 0.5, "edge", 420), ("inquisition_banner", 0.5, "edge", 380), ("cell_bars", 0.55, "edge", 420),
                       ("torture_rack", 0.25, "any", 380), ("stocks", 0.2, "any", 360), ("gibbet_cage", 0.25, "any", 380),
                       ("lectern", 0.3, "any", 360), ("coffin", 0.15, "any", 300), ("rubble", 0.2, "any", 240)], 2.5)
# Acts III and IV, interiors and Paris (0.8.2).
PALACE = dense([("tapestry_wall", 0.7, "edge", 420), ("suit_of_armour", 0.6, "edge", 300), ("bookcase", 0.35, "edge", 380),
                ("stone_pillar", 0.5, "edge", 420), ("banquet_table", 0.25, "any", 420), ("carpet", 0.5, "any", 380),
                ("saint_statue", 0.2, "edge", 400), ("banner", 0.3, "edge", 360)], 2.5)
UNDERCHURCH = dense([("pews", 0.5, "any", 360), ("tomb_effigy", 0.5, "any", 320), ("saint_statue", 0.35, "edge", 380),
                     ("ossuary_wall", 0.4, "edge", 420), ("stone_pillar", 0.5, "edge", 420), ("coffin", 0.3, "any", 260),
                     ("reliquary", 0.2, "any", 320), ("font", 0.15, "any", 320), ("rubble", 0.3, "any", 240)], 2.5)
CATHEDRAL = dense([("stained_glass_wall", 0.8, "edge", 420), ("stone_pillar", 0.6, "edge", 420), ("pews", 0.8, "any", 340),
                   ("saint_statue", 0.4, "edge", 380), ("tomb_effigy", 0.25, "any", 320), ("font", 0.1, "any", 320),
                   ("carpet", 0.4, "any", 380), ("reliquary", 0.1, "any", 320)], 2.5)
TOWER = dense([("wall_shackles", 0.8, "edge", 420), ("cell_bars", 0.6, "edge", 420), ("stone_pillar", 0.4, "edge", 420),
               ("torture_rack", 0.25, "any", 380), ("gibbet_cage", 0.3, "any", 380), ("rubble", 0.4, "any", 240),
               ("lectern", 0.15, "any", 360), ("coffin", 0.15, "any", 300), ("bone_pile", 0.3, "any", 220)], 2.5)
GREVE = [("townhouse", 0.35, "edge", 520), ("townhouse_b", 0.35, "edge", 520), ("boarded_house", 0.2, "edge", 520),
         ("market_stall", 0.6, "edge", 420), ("scaffold", 0.15, "any", 600), ("gibbet_cage", 0.3, "any", 460),
         ("stocks", 0.3, "any", 420), ("inquisition_banner", 0.4, "edge", 380), ("torch_post", 0.8, "any", 340),
         ("notice_board", 0.2, "any", 420), ("prison_wagon", 0.15, "any", 520), ("barrels_crates", 0.6, "any", 300),
         ("puddle", 0.5, "any", 260)]
SHADOW_TOWN = [("market_stall", 1.6, "edge", 420), ("barrels_crates", 1.4, "edge", 300), ("stone_well", 0.3, "any", 500), ("hay_cart", 0.5, "edge", 420),
               ("wayside_cross", 0.3, "edge", 420), ("stocks", 0.4, "edge", 420), ("gibbet_cage", 0.3, "edge", 460), ("inquisition_banner", 0.6, "edge", 380),
               ("torch_post", 1.0, "edge", 320), ("puddle", 0.6, "any", 260)]

BIOMES = {
    "forest_of_orient": FOREST, "kings_forest_east": FOREST, "kings_forest_west": FOREST,
    "bloodied_fields": FIELDS, "provins_road": ROAD, "royal_court_outskirts": ROAD + [("banner", 0.3, "edge", 360)],
    "frozen_vineyards": FROST, "quarry_of_saint_blaise": QUARRY,
    "burned_village_of_payns": BURNT, "payns_courtyard": BURNT[:1] + [("rock_cluster", 0.6, "any", 160)],
    "drowned_commandery": MARSH, "preceptory_of_payns": YARD, "royal_garrison": YARD + [("tent", 0.6, "edge", 420)],
    "abandoned_castle": RUIN + [("rubble", 0.6, "any", 240), ("stone_pillar", 0.2, "any", 340), ("gibbet_cage", 0.15, "edge", 420)],
    "remnant_camp": CAMP + [("weapon_rack", 0.6, "edge", 360), ("barrels_crates", 0.6, "edge", 300), ("hay_cart", 0.3, "edge", 420)], "town_in_shadow": SHADOW_TOWN, "place_de_greve": GREVE,
    "catacomb_network_1": SMUGGLERS, "catacomb_network_2": CATACOMB, "catacomb_network_3": CATACOMB + [("coffin", 0.4, "any", 260)],
    "hermits_cave": CAVE,
    "valois_estate": GARDEN, "monastery_of_trials": CLOISTER + [("lectern", 0.3, "any", 360), ("saint_statue", 0.25, "edge", 400), ("stocks", 0.15, "any", 380)], "charnel_hollow": GRAVEYARD,
    "sanctuary_of_the_broken_seal": [("brazier", 0.6, "edge", 420)],
    "inquisition_hq_1": INQUISITION, "inquisition_hq_2": INQUISITION, "inquisition_hq_3": INQUISITION,
    "royal_palace_1": HALLS + PALACE, "royal_palace_2": HALLS + PALACE, "royal_palace_3": HALLS + PALACE,
    "papal_palace": HALLS + PALACE + dense([("altar", 0.15, "any", 420), ("reliquary", 0.2, "any", 320)], 2.5),
    "legates_palace_1": PALACE, "legates_palace_2": PALACE, "legates_palace_3": PALACE,
    "underground_cathedral_1": UNDERCHURCH, "underground_cathedral_2": UNDERCHURCH, "notre_dame": CATHEDRAL,
    "tower_of_truth_1": HALLS + TOWER, "tower_of_truth_2": HALLS + TOWER, "tower_of_truth_3": HALLS + TOWER,
    "tower_of_truth_4": HALLS + TOWER, "tower_of_truth_5": HALLS + TOWER,
}
WALKABLE = {"grass_tuft", "grass_flowers", "dry_grass", "carpet"}

# Set pieces behind each act's great fights: (prop, dx, dy) from the boss or superunique, placed before the scenery.
SETPIECES = {
    "royal_palace_3": [("throne_dais_1", 0, -420), ("suit_of_armour_1", -420, -300), ("suit_of_armour_2", 420, -300), ("carpet_1", 0, 260)],
    "papal_palace": [("throne_dais_2", 0, -420), ("altar_1", -520, -260), ("reliquary_1", 520, -260), ("carpet_2", 0, 260)],
    "notre_dame": [("altar_2", 0, -420), ("stained_glass_wall_1", -380, -560), ("stained_glass_wall_2", 380, -560), ("pews_1", -340, 380), ("pews_2", 340, 380)],
    "place_de_greve": [("pyre_stake_1", 0, -380), ("scaffold_2", -520, -200), ("inquisition_banner_1", 360, -420)],
    "legates_palace_3": [("throne_dais_1", 0, -420), ("tapestry_wall_1", -480, -480), ("tapestry_wall_2", 480, -480)],
    "underground_cathedral_2": [("altar_1", 0, -420), ("tomb_effigy_1", -420, -200), ("tomb_effigy_2", 420, -200)],
    "tower_of_truth_5": [("wall_shackles_1", -300, -420), ("wall_shackles_2", 300, -420), ("brazier_1", 0, -300)],
}


def setpieces(z, props):
    c = z.get("bossPosition") or z.get("superuniquePosition")
    if not c: return []
    return [{"prop": p, "x": round(c[0] + dx), "y": round(c[1] + dy), "flip": False, "auto": "setpiece"}
            for p, dx, dy in SETPIECES.get(z["id"], []) if p in props and 60 < c[1] + dy < z["height"] - 30]


def seg_dist(p, a, b):
    ax, ay = a; bx, by = b; px, py = p
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / max(1e-6, dx * dx + dy * dy)))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def paths(z):
    """The ways heroes walk: arrival point to every exit, the waypoint and every NPC."""
    s = tuple(z["playerSpawn"])
    ends = [(e["x"], e["y"]) for e in z.get("exits", [])] + [(n["x"], n["y"]) for n in z.get("npcs", [])]
    if z.get("waypoint"): ends.append(tuple(z["waypoint"][:2]))
    return [(s, e) for e in ends]


def scenery(z, rng, recipe, props):
    w, h = z["width"], z["height"]
    clear = keep_clear(z)
    ways = paths(z)
    def reach(d):  # hand-placed decor keeps auto scenery off its whole footprint (a house is far wider than a crate)
        fp = props.get(d["prop"], {}).get("footprint") or [0, 0]
        return max(120, fp[0] + 60)
    placed = [(d["x"], d["y"], reach(d)) for d in z.get("decor", [])]
    arena = z.get("arena")
    area = w * h / 1e6
    out = []
    for prop, density, where, spacing in recipe:
        walk = prop in WALKABLE
        n = int(round(density * area * rng.uniform(0.85, 1.15)))
        made, tries = 0, 0
        cluster_c = None
        while made < n and tries < n * 60:
            tries += 1
            if where == "edge":
                band = min(520, 0.22 * min(w, h))
                side = rng.randrange(4)
                x = rng.uniform(60, w - 60) if side < 2 else (rng.uniform(60, band) if side == 2 else rng.uniform(w - band, w - 60))
                y = (rng.uniform(80, band) if side == 0 else rng.uniform(h - band, h - 40)) if side < 2 else rng.uniform(80, h - 40)
            elif where == "cluster":
                if cluster_c is None or rng.random() < 0.3:
                    cluster_c = (rng.uniform(300, w - 300), rng.uniform(300, h - 300))
                x, y = cluster_c[0] + rng.gauss(0, 260), cluster_c[1] + rng.gauss(0, 180)
            else:
                x, y = rng.uniform(80, w - 80), rng.uniform(100, h - 60)
            if not (40 < x < w - 40 and 60 < y < h - 30): continue
            if not far((x, y), clear, 150 if walk else 270): continue
            if not walk and any(seg_dist((x, y), a, b) < 180 for a, b in ways): continue
            if arena and arena["x"] - 80 <= x <= arena["x"] + arena["width"] + 80 and arena["y"] - 80 <= y <= arena["y"] + arena["height"] + 80: continue
            if any(math.hypot(x - px, y - py) < max(spacing, ps) * (0.5 if walk else 1.0) for px, py, ps in placed): continue
            view = rng.choice(["_1", "_2"])
            if prop + view not in props: continue
            out.append({"prop": prop + view, "x": round(x), "y": round(y), "flip": rng.random() < 0.5, "auto": "scenery"})
            placed.append((x, y, spacing if not walk else 60))
            made += 1
    return out


def main():
    props = json.load(open(PROPS))
    for k in ("candelabrum_1", "candelabrum_2"): props[k]["light"] = dict(CANDLES)
    for k in ("burned_beams_1", "burned_beams_2"): props[k]["light"] = dict(EMBERS)
    json.dump(props, open(PROPS, "w"), indent=1, ensure_ascii=False); open(PROPS, "a").write("\n")

    for name in sorted(os.listdir(ZONES)):
        zid = name[:-5]
        if zid not in MOODS: continue
        path = os.path.join(ZONES, name)
        z = json.load(open(path))
        rng = random.Random(zid)
        z["ambience"] = dict(MOODS[zid], sound=sound_for(zid, MOODS[zid]))
        z["decor"] = [d for d in z.get("decor", []) if not d.get("auto")]  # candelabra and scenery from earlier runs
        z["decor"] += setpieces(z, props)
        if zid in CANDLELIT and sum(1 for d in z["decor"] if d["prop"].startswith("candelabrum")) < 6:
            for d in candelabra(z, rng):
                d["auto"] = True
                z["decor"].append(d)
        if zid in BIOMES:
            z["decor"] += scenery(z, random.Random(zid + "/scenery"), BIOMES[zid], props)
        lights = []
        if zid in FIRES: lights += scatter_lights(z, rng, FIRES[zid], FIRE)
        if zid in SHAFTS: lights += scatter_lights(z, rng, SHAFTS[zid], SHAFT)
        z["lights"] = lights
        with open(path, "w") as f:
            json.dump(z, f, indent=1, ensure_ascii=False)
            f.write("\n")
        print(f"{zid:32} level {z['ambience']['level']:.2f}  candelabra {sum(1 for d in z['decor'] if d.get('auto') is True):2}  "
              f"scenery {sum(1 for d in z['decor'] if d.get('auto') == 'scenery'):3}  lights {len(lights)}")


if __name__ == "__main__":
    main()
