"""
Hand-laid streets for Town in Shadow: rows of townhouses along the north and south edges framing the square, shut-up
houses among them, the prison wagon and the edict board. Rerunnable: replaces only the decor it placed
("layout": "town"); zone_ambience.py then dresses around it.

    python tools/art/town_layout.py && python tools/art/zone_ambience.py
"""
import json
import os

ZONE = os.path.join(os.path.dirname(__file__), "..", "..", "assets", "data", "zones", "town_in_shadow.json")

LAYOUT = [
    ("townhouse_1", 260, 360, False), ("townhouse_b_2", 720, 360, True), ("townhouse_b_1", 1680, 360, False),
    ("boarded_house_2", 2140, 360, True), ("notice_board_1", 2460, 400, False),
    ("townhouse_b_1", 260, 1580, False), ("boarded_house_1", 720, 1580, False), ("townhouse_1", 1680, 1580, True),
    ("townhouse_b_2", 2140, 1580, False),
    ("hay_cart_1", 330, 1150, False), ("barrels_crates_2", 2300, 1150, True),  # low: tall houses here would hide the exits
    ("prison_wagon_2", 950, 1310, False), ("notice_board_2", 1730, 960, False),
    # The square, set in the gaps between the lanes people walk (spawn to every NPC, exit and the waypoint).
    ("stone_well_1", 1377, 440, False), ("gibbet_cage_2", 1022, 440, True),
    ("market_stall_1", 545, 600, False), ("market_stall_2", 580, 1030, True), ("market_stall_1", 1430, 1270, False),
    ("stocks_1", 760, 1250, False), ("inquisition_banner_1", 1010, 230, False), ("inquisition_banner_2", 1390, 230, True),
    ("torch_post_1", 600, 690, False), ("torch_post_2", 620, 950, False), ("torch_post_1", 2050, 680, True),
    ("torch_post_2", 2000, 980, False), ("torch_post_1", 1060, 1500, False), ("torch_post_2", 1340, 1500, True),
]


def main():
    z = json.load(open(ZONE))
    z["decor"] = [d for d in z.get("decor", []) if d.get("layout") != "town"]
    z["decor"] += [{"prop": p, "x": x, "y": y, "flip": f, "layout": "town"} for p, x, y, f in LAYOUT]
    with open(ZONE, "w", newline="\n") as fh:
        json.dump(z, fh, indent=1, ensure_ascii=False); fh.write("\n")


if __name__ == "__main__":
    main()
