#!/usr/bin/env python3
"""Places scenery props (assets/data/props.json) in zones and writes them to each zone's "decor" list.

    python tools/content/decor.py            # every zone in RECIPES
    python tools/content/decor.py --clear    # remove decor from those zones

Placement is seeded by the zone id, so the same props land in the same places every run. Props come in small
clusters, keep clear of the player spawn, exits, waypoint and boss positions, and keep a margin from the map edge.
Decor is drawn by the client only (it does not block movement), so lanes stay walkable.
"""
import argparse, json, math, os, random

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ZONES = os.path.join(ROOT, "assets", "data", "zones")
PROPS = os.path.join(ROOT, "assets", "data", "props.json")

# zone -> (props per million square pixels, [(prop base, weight, cluster min, cluster max)])
RECIPES = {
    "bloodied_fields": (2.2, [("wheat_sheaves", 5, 2, 4), ("hay_cart", 2, 1, 1), ("split_fence", 4, 2, 3), ("wooden_plough", 2, 1, 1)]),
    "preceptory_of_payns": (1.4, [("burned_beams", 4, 1, 3), ("barrels_crates", 3, 1, 2), ("weapon_rack", 2, 1, 2), ("stone_well", 1, 1, 1),
                                  ("wayside_cross", 1, 1, 1), ("hay_cart", 1, 1, 1)]),
    "catacombs_of_the_rule": (2.4, [("sarcophagus", 4, 1, 3), ("bone_pile", 4, 1, 3), ("candelabrum", 3, 1, 2), ("broken_column", 3, 1, 2)]),
    "charnel_hollow": (2.6, [("bone_pile", 6, 2, 4), ("broken_column", 2, 1, 1), ("candelabrum", 1, 1, 1), ("sarcophagus", 1, 1, 1)]),
    "burned_village_of_payns": (1.8, [("burned_beams", 5, 1, 3), ("barrels_crates", 3, 1, 2), ("hay_cart", 2, 1, 1), ("split_fence", 3, 2, 3),
                                      ("wheat_sheaves", 2, 1, 3)]),
}
GROUND = {"bloodied_fields": "tiles/meshy_fields_mud.png", "preceptory_of_payns": "tiles/meshy_courtyard_cobbles.png",
          "catacombs_of_the_rule": "tiles/meshy_crypt_slabs.png", "charnel_hollow": "tiles/meshy_crypt_bones.png",
          "burned_village_of_payns": "tiles/meshy_courtyard_flags.png"}

def keepouts(z):
    k = [(z["playerSpawn"][0], z["playerSpawn"][1], 420)]
    for e in z.get("exits", []): k.append((e["x"], e["y"], 320))
    for key in ("waypoint", "superuniquePosition", "bossPosition"):
        if z.get(key): k.append((z[key][0], z[key][1], 320))
    for n in z.get("npcs", []):
        if "x" in n and "y" in n: k.append((n["x"], n["y"], 200))
    return k

def place(z, density, recipe, props):
    rng = random.Random(z["id"])
    W, H = z["width"], z["height"]
    target = int(W * H / 1e6 * density)
    bases = [r for r in recipe if f"{r[0]}_1" in props]
    if not bases: return []
    weights = [r[1] for r in bases]
    blocked = keepouts(z)
    placed = []
    def free(x, y, gap):
        if x < 140 or y < 160 or x > W - 140 or y > H - 80: return False
        if any(math.hypot(x - bx, y - by) < r for bx, by, r in blocked): return False
        return all(math.hypot(x - p["x"], y - p["y"]) >= gap for p in placed)
    tries = 0
    while len(placed) < target and tries < target * 60:
        tries += 1
        base, _, cmin, cmax = rng.choices(bases, weights)[0]
        cx, cy = rng.uniform(0, W), rng.uniform(0, H)
        if not free(cx, cy, 260): continue
        n = rng.randint(cmin, cmax)
        for i in range(n):
            x, y = (cx, cy) if i == 0 else (cx + rng.uniform(-170, 170), cy + rng.uniform(-110, 110))
            if i and not free(x, y, 95): continue
            placed.append({"prop": f"{base}_{rng.choice((1, 2))}", "x": round(x), "y": round(y), "flip": rng.random() < 0.5})
    placed.sort(key=lambda p: (p["y"], p["x"]))
    return placed

def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--clear", action="store_true"); a = ap.parse_args()
    props = json.load(open(PROPS)) if os.path.exists(PROPS) else {}
    for zid, (density, recipe) in RECIPES.items():
        p = os.path.join(ZONES, zid + ".json")
        raw = open(p, encoding="utf-8").read()
        indent = 2 if raw.startswith("{\n  ") else 1
        z = json.loads(raw)
        if a.clear: z.pop("decor", None)
        else:
            z["decor"] = place(z, density, recipe, props)
            if zid in GROUND and os.path.exists(os.path.join(ROOT, "assets", GROUND[zid])): z["groundTile"] = GROUND[zid]
        with open(p, "w", encoding="utf-8", newline="\n") as f: json.dump(z, f, indent=indent, ensure_ascii=False); f.write("\n")
        print(f"{zid}: {len(z.get('decor', []))} props")

if __name__ == "__main__":
    main()
