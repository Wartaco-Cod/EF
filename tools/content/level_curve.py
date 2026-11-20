"""
Fits the experience curve to the campaign, so a party ends each zone near the top of its level band
(Act I near 20, Act II near 40, Act III near 70, Act IV at 100).

    run_bots.bat --campaign 4 campaign.csv
    python tools/content/level_curve.py campaign.csv

Experience per kill depends on the monster, not the player, so the experience a zone gives is the same
whatever curve is in force: the campaign measures it once, and this writes assets/data/levels.json.
"""
import csv, json, math, os, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

def main(path):
    rows = list(csv.DictReader(open(path)))
    # Anchors at the end of each act: the experience a party has earned by then sets the level it should be.
    # Act I is a single chain of short zones, so each zone's top level is an anchor too.
    act_end = {1: 20, 2: 40, 3: 70, 4: 100}
    cum, points = 0.0, [(1, 0.0)]
    for act in (1, 2, 3, 4):
        for r in rows:
            if int(r["act"]) != act: continue
            cum += float(r["xpPerPlayer"])
            top = min(int(r["levelMax"]), act_end[act])
            if act == 1 and top <= 16 and top > points[-1][0] and r["zone"] != "sealed_vault_of_payns":
                points.append((top, cum * 0.92))
        points.append((act_end[act], cum * 0.92))  # a little slack: players skip some packs
    # Cumulative experience needed for each level: within an act it rises a little faster than linearly,
    # so each level costs a bit more than the last (Act I starts gently).
    need = {1: 0.0}
    for (l0, c0), (l1, c1) in zip(points, points[1:]):
        q = 1.0 if l1 <= 20 else 1.25
        for L in range(l0 + 1, l1 + 1):
            need[L] = c0 + (c1 - c0) * ((L - l0) / (l1 - l0)) ** q
    per = [max(100, round(need[L + 1] - need[L])) for L in range(1, 100)]
    # Never let a level cost less than the one before it.
    for i in range(1, len(per)): per[i] = max(per[i], per[i - 1])
    out = os.path.join(ROOT, "assets", "data", "levels.json")
    json.dump({"comment": "Experience from each level to the next (index 0 = level 1 to 2). Made by tools/content/level_curve.py.",
               "xpToNext": per}, open(out, "w"), indent=1)
    for L in (2, 5, 10, 20, 30, 40, 50, 60, 70, 80, 90, 99):
        print(f"level {L:3}: {per[L - 1]:>12,} to next")

if __name__ == "__main__":
    main(sys.argv[1])
