"""
Builds the data for the prologue and Acts II-IV of Templar Knights from the storyline in the plan
(Acts and zones, hubs, NPCs, bosses, choices and endings) and writes it into assets/data.

    python tools/content/acts.py            # (re)write the generated content
    python tools/content/acts.py --check    # report what would change, write nothing

Act I stays hand-authored. Everything this script owns is marked "generated": true so a re-run replaces
it cleanly. Monster numbers come from a few archetypes scaled to each act's level band; the per-act
multipliers at the top are the balance knobs (tuned with `run_bots.bat --simulate all`).
"""
import json, math, os, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DATA = os.path.join(ROOT, "assets", "data")
ART = os.path.join(ROOT, "tools", "art", "placeholder_looks.json")

# ---------------------------------------------------------------- balance knobs
# Monster base health and damage per act (the server also scales by level and party size: see Difficulty).
ACT_HP = {1: 1.0, 2: 1.15, 3: 1.35, 4: 1.6}
ACT_DMG = {1: 1.0, 2: 1.3, 3: 2.1, 4: 2.9}
TIER_LEVEL = {1: 20, 2: 30, 3: 55, 4: 85}

ARCH = {
    #            hp    dmg   speed range  cd   radius detect
    "grunt":     (1.00, 1.00, 110,  36,  1.0,  22, 320),
    "brute":     (1.60, 1.40,  80,  40,  1.5,  26, 300),
    "skirmisher":(0.70, 0.80, 165,  30,  0.7,  18, 360),
    "beast":     (0.60, 0.80, 190,  28,  0.7,  16, 380),
    "swarm":     (0.15, 0.30, 170,  22,  0.8,  12, 300),
    "archer":    (0.60, 0.90,  95, 260,  2.2,  18, 400),
    "caster":    (0.70, 1.20,  95, 240,  1.8,  18, 380),
    "elite":     (1.30, 1.30, 120,  36,  0.9,  24, 340),
    "boss":      (1.80, 1.20, 105,  40,  1.1,  26, 600),
}

def player_damage(level): return 15 + 2 * (level - 1)
def player_health(level): return 100 + 10 * (level - 1)

def base_stats(act, arch):
    """Base health/damage for a monster of this act, so it lands where Act I's late monsters sit relative to a player."""
    L = TIER_LEVEL[act]
    hp_m, dmg_m, speed, rng, cd, radius, detect = ARCH[arch]
    hp = 110 * player_damage(L) / 53 * (1 + 0.05 * 19) / (1 + 0.05 * (L - 1)) * ACT_HP[act] * hp_m
    dmg = 19 * player_health(L) / 290 * (1 + 0.03 * 19) / (1 + 0.03 * (L - 1)) * ACT_DMG[act] * dmg_m
    return dict(health=max(4, round(hp)), damage=max(1, round(dmg)), speed=speed, attackRange=rng,
                detectRange=detect, attackCooldown=cd, radius=radius)

# ---------------------------------------------------------------- monsters: id, name, act, archetype, extra, look
# look: body, trim, head, weapon, emblem, shape (see tools/art/placeholders.py)
MONSTERS = [
    # Act II - the Underground
    ("smuggler_cutthroat", "Smuggler Cutthroat", 2, "skirmisher", {}, dict(body="#4A3B2A", trim="#8A6B3A", head="hood", weapon="dagger")),
    ("smuggler_bruiser", "Smuggler Bruiser", 2, "brute", {}, dict(body="#5B4632", trim="#2E241A", head="bare", weapon="club")),
    ("tunnel_rats", "Tunnel Rats", 2, "swarm", {}, dict(body="#6B6258", shape="swarm")),
    ("garrison_pikeman", "Garrison Pikeman", 2, "grunt", {}, dict(body="#2C4A86", trim="#D9B23A", head="helm", weapon="spear", emblem="fleur")),
    ("garrison_crossbowman", "Garrison Crossbowman", 2, "archer", {}, dict(body="#2C4A86", trim="#D9B23A", head="kettle", weapon="crossbow", emblem="fleur")),
    ("garrison_knight", "Garrison Knight", 2, "elite", {}, dict(body="#23407A", trim="#E6C14A", head="greathelm", weapon="sword", emblem="fleur", shield=True)),
    ("routier", "Routier Mercenary", 2, "grunt", {}, dict(body="#5E5A4E", trim="#8E2F28", head="coif", weapon="axe")),
    ("routier_archer", "Routier Archer", 2, "archer", {}, dict(body="#5E5A4E", trim="#8E2F28", head="hood", weapon="bow")),
    ("cave_crawler", "Pale Crawler", 2, "beast", {"undead": True}, dict(body="#C9C3B4", shape="beast", eyes="#9FE0C0")),
    ("cave_bats", "Cave Bats", 2, "swarm", {}, dict(body="#3A3236", shape="swarm")),
    ("inquisition_jailer", "Inquisition Jailer", 2, "grunt", {}, dict(body="#2A2624", trim="#7A7468", head="hood", weapon="club")),
    ("dominican_inquisitor", "Dominican Inquisitor", 2, "caster", {}, dict(body="#F0ECE2", trim="#1E1C1A", head="hood_black", weapon="book")),
    ("inquisition_executioner", "Inquisition Executioner", 2, "brute", {}, dict(body="#1C1A18", trim="#5A1A16", head="executioner", weapon="axe")),
    ("penitent_zealot", "Penitent Zealot", 2, "skirmisher", {}, dict(body="#8A7A64", trim="#5A1A16", head="bare", weapon="whip")),
    # Act III - Infiltration
    ("royal_huntsman", "Royal Huntsman", 3, "archer", {}, dict(body="#3E5A2E", trim="#C9A227", head="hood", weapon="bow")),
    ("kings_forester", "King's Forester", 3, "grunt", {}, dict(body="#3E5A2E", trim="#6B4A2A", head="kettle", weapon="spear")),
    ("hunting_boar", "Hunting Boar", 3, "beast", {}, dict(body="#4E3A2A", shape="beast", tusk=True)),
    ("grey_wolf", "Grey Wolf", 3, "beast", {}, dict(body="#7A7670", shape="beast")),
    ("corrupt_monk", "Corrupt Monk", 3, "caster", {}, dict(body="#5A4632", trim="#2A2018", head="hood", weapon="book")),
    ("trial_executioner", "Trial Executioner", 3, "brute", {}, dict(body="#1C1A18", trim="#8A1C18", head="executioner", weapon="maul")),
    ("penitent_brother", "Penitent Brother", 3, "skirmisher", {}, dict(body="#8A7A64", trim="#3A2E22", head="hood", weapon="whip")),
    ("court_guard", "Court Guard", 3, "grunt", {"guard": True}, dict(body="#23407A", trim="#E6C14A", head="kettle", weapon="halberd", emblem="fleur")),
    ("court_sergeant", "Court Sergeant", 3, "elite", {"guard": True}, dict(body="#1B3366", trim="#F2D25A", head="greathelm", weapon="sword", emblem="fleur", shield=True)),
    ("court_spy", "Court Informer", 3, "skirmisher", {"guard": True}, dict(body="#4A2E4A", trim="#C9A227", head="hood", weapon="dagger")),
    ("court_hound", "Court Hound", 3, "beast", {}, dict(body="#6A5038", shape="beast")),
    ("apostate_templar", "Apostate Templar", 3, "elite", {}, dict(body="#E6E2D8", trim="#1A1A1A", head="greathelm", weapon="sword", emblem="black_cross", shield=True)),
    ("cathedral_shade", "Cathedral Shade", 3, "caster", {"undead": True}, dict(body="#7A8AA0", trim="#C9D6E8", head="wraith", weapon="none", ghost=True)),
    ("ossuary_knight", "Ossuary Knight", 3, "brute", {"undead": True}, dict(body="#D8D0BC", trim="#5A5448", head="skull", weapon="maul")),
    ("papal_guard", "Papal Guard", 3, "grunt", {}, dict(body="#8E1C1C", trim="#E6C14A", head="helm", weapon="halberd", emblem="keys")),
    ("papal_crossbowman", "Papal Crossbowman", 3, "archer", {}, dict(body="#8E1C1C", trim="#E6C14A", head="kettle", weapon="crossbow", emblem="keys")),
    ("legate_inquisitor", "Legate's Inquisitor", 3, "caster", {}, dict(body="#6E1414", trim="#F0ECE2", head="hood", weapon="book", emblem="keys")),
    ("vault_warden", "Vault Warden", 3, "brute", {}, dict(body="#3A3A40", trim="#E6C14A", head="greathelm", weapon="maul", emblem="keys")),
    # Act IV - the Reckoning
    ("paris_zealot", "Parisian Zealot", 4, "skirmisher", {}, dict(body="#6A5A44", trim="#8A1C18", head="bare", weapon="torch")),
    ("royal_guardsman", "Royal Guardsman", 4, "grunt", {}, dict(body="#1B3366", trim="#F2D25A", head="helm", weapon="halberd", emblem="fleur")),
    ("royal_archer", "Royal Archer", 4, "archer", {}, dict(body="#1B3366", trim="#F2D25A", head="kettle", weapon="crossbow", emblem="fleur")),
    ("provosts_man", "Provost's Man", 4, "brute", {}, dict(body="#1C1A18", trim="#8A1C18", head="executioner", weapon="axe")),
    ("household_knight", "Knight of the Household", 4, "elite", {}, dict(body="#142A55", trim="#F2D25A", head="greathelm", weapon="sword", emblem="fleur", shield=True)),
    ("royal_almoner", "Royal Almoner", 4, "caster", {}, dict(body="#E8E2D2", trim="#1B3366", head="mitre", weapon="book", emblem="fleur")),
    ("vatican_champion", "Champion of the Curia", 4, "elite", {}, dict(body="#8E1C1C", trim="#F2D25A", head="greathelm", weapon="sword", emblem="keys", shield=True)),
    ("cardinal_inquisitor", "Cardinal's Inquisitor", 4, "caster", {}, dict(body="#A01818", trim="#F2D25A", head="cardinal", weapon="book", emblem="keys")),
    ("living_effigy", "Living Effigy", 4, "brute", {"undead": True}, dict(body="#9A968C", trim="#6A665E", head="stone", weapon="none", stone=True)),
    ("tower_warden", "Warden of the Tower", 4, "brute", {}, dict(body="#2A2A30", trim="#9A9080", head="greathelm", weapon="maul", emblem="fleur")),
    ("confession_wraith", "Confession Wraith", 4, "caster", {"undead": True}, dict(body="#A8B4C8", trim="#E8E4DA", head="wraith", weapon="scroll", ghost=True)),
    ("chosen_knight", "Chosen of the King", 4, "elite", {}, dict(body="#E6C14A", trim="#142A55", head="crownhelm", weapon="sword", emblem="fleur", shield=True)),
    # Prologue and allies
    ("payns_soldier", "King's Soldier", 1, "grunt", {}, dict(body="#2C4A86", trim="#D9B23A", head="kettle", weapon="spear", emblem="fleur")),
    ("remnant_brother", "Brother of the Remnant", 4, "elite", {}, dict(body="#F0ECE2", trim="#B3241E", head="greathelm", weapon="sword", emblem="cross", shield=True)),
]

# The prologue soldiers are deliberately weak: it is the tutorial.
PROLOGUE_OVERRIDE = {"payns_soldier": dict(health=22, damage=3)}

# ---------------------------------------------------------------- superuniques and bosses
# id, name, base archetype source monster, level, hp mul, dmg mul, escort, count, drop, minRarity, look, boss sprite?
UNIQUES = [
    # Act II
    ("bastien", "Bastien the Ferryman", "smuggler_bruiser", 25, 6, 1.6, "smuggler_cutthroat", 5, "Weapon", "Rare", dict(body="#3A2E22", trim="#C9A227", head="hood", weapon="oar")),
    ("brother_guichard", "Brother Guichard, First of the Fallen", "fallen_templar", 27, 6, 1.7, "fallen_templar", 4, "Armor", "Rare", dict(body="#B8B0A0", trim="#5A1A16", head="greathelm", weapon="sword", emblem="cross", undead=True, shield=True)),
    ("reginald", "Captain Réginald", "garrison_knight", 31, 7, 1.6, "garrison_pikeman", 4, "Shield", "Rare", dict(body="#23407A", trim="#F2D25A", head="bare_beard", weapon="sword", emblem="fleur", shield=True)),
    ("hugues_le_routier", "Hugues le Routier", "routier", 34, 7, 1.7, "routier", 5, "Weapon", "Elite", dict(body="#4E4A40", trim="#B3241E", head="coif", weapon="greataxe")),
    ("pale_thing", "The Pale Thing in the Well", "cave_crawler", 36, 10, 1.4, "cave_crawler", 6, "Helm", "Elite", dict(body="#E0DCCF", shape="beast", eyes="#9FE0C0", big=True)),
    ("warden_of_cells", "Warden of the Cells", "inquisition_jailer", 38, 7, 1.7, "inquisition_jailer", 4, "Armor", "Elite", dict(body="#1E1C1A", trim="#C9A227", head="hood", weapon="keys_ring")),
    ("marcus", "Inquisitor Marcus", "dominican_inquisitor", 40, 12, 1.5, "", 0, "Weapon", "Epic", dict(body="#F0ECE2", trim="#8A1C18", head="hood_black", weapon="book", emblem="eye")),
    # Act III
    ("master_of_the_hunt", "Master of the Royal Hunt", "royal_huntsman", 45, 7, 1.7, "grey_wolf", 5, "Weapon", "Elite", dict(body="#2E4A22", trim="#F2D25A", head="feathered", weapon="bow")),
    ("black_boar", "The Black Boar of Vincennes", "hunting_boar", 48, 12, 1.5, "hunting_boar", 3, "Helm", "Elite", dict(body="#1E1A16", shape="beast", tusk=True, big=True)),
    ("prior_anselme", "Prior Anselme the Examiner", "corrupt_monk", 52, 9, 1.6, "penitent_brother", 5, "Armor", "Elite", dict(body="#3A2A1E", trim="#C9A227", head="tonsure", weapon="book")),
    ("keeper_of_rolls", "Keeper of the Rolls", "court_sergeant", 56, 8, 1.6, "court_guard", 4, "Shield", "Elite", dict(body="#1B3366", trim="#F2D25A", head="hat", weapon="scroll", emblem="fleur")),
    ("ossuary_abbot", "The Ossuary Abbot", "ossuary_knight", 58, 9, 1.6, "cathedral_shade", 4, "Weapon", "Elite", dict(body="#CFC6AE", trim="#5A1A16", head="skull_mitre", weapon="crozier")),
    ("arnaud", "Sir Arnaud de Foix, the Betrayer", "apostate_templar", 62, 9, 1.7, "apostate_templar", 3, "Weapon", "Epic", dict(body="#E6E2D8", trim="#1A1A1A", head="bare_beard", weapon="sword", emblem="black_cross", shield=True)),
    ("keeper_of_the_vault", "Keeper of the Vault", "vault_warden", 66, 9, 1.6, "papal_guard", 4, "Armor", "Epic", dict(body="#3A3A40", trim="#F2D25A", head="greathelm", weapon="keys_ring", emblem="keys")),
    ("inquisitor_general", "The Inquisitor General", "legate_inquisitor", 70, 14, 1.6, "", 0, "Weapon", "Epic", dict(body="#6E1414", trim="#F2D25A", head="cardinal", weapon="crozier", emblem="keys")),
    # Act IV
    ("provost", "The Provost of Paris", "provosts_man", 78, 9, 1.7, "royal_guardsman", 5, "Helm", "Epic", dict(body="#1C1A18", trim="#F2D25A", head="hat", weapon="mace", emblem="fleur")),
    ("captain_of_the_guard", "Captain of the Royal Guard", "household_knight", 81, 9, 1.7, "royal_guardsman", 5, "Shield", "Epic", dict(body="#142A55", trim="#F2D25A", head="greathelm_plume", weapon="sword", emblem="fleur", shield=True)),
    ("nogaret", "Guillaume de Nogaret, Keeper of the Seals", "royal_almoner", 85, 14, 1.6, "", 0, "Armor", "Epic", dict(body="#142A55", trim="#E8E2D2", head="hat", weapon="seal", emblem="fleur")),
    ("cardinals_champion", "The Cardinal's Champion", "vatican_champion", 89, 10, 1.7, "vatican_champion", 3, "Weapon", "Epic", dict(body="#8E1C1C", trim="#F2D25A", head="greathelm_plume", weapon="greatsword", emblem="keys")),
    ("clement", "Pope Clement V", "cardinal_inquisitor", 94, 16, 1.5, "", 0, "Helm", "Legendary", dict(body="#F0ECE2", trim="#F2D25A", head="tiara", weapon="crozier", emblem="keys")),
    ("tower_gaoler", "The Gaoler of the Tower", "tower_warden", 96, 10, 1.7, "tower_warden", 3, "Armor", "Epic", dict(body="#2A2A30", trim="#C9A227", head="executioner", weapon="keys_ring")),
    ("marigny", "Enguerrand de Marigny", "household_knight", 97, 10, 1.7, "chosen_knight", 3, "Shield", "Epic", dict(body="#142A55", trim="#F2D25A", head="hat", weapon="sword", emblem="fleur")),
    ("living_confession", "The Living Confession", "confession_wraith", 98, 12, 1.6, "confession_wraith", 5, "Helm", "Legendary", dict(body="#C8D0DC", trim="#8A1C18", head="wraith", weapon="scroll", ghost=True, big=True)),
    ("philip_iv", "Philip IV, the Iron King", "chosen_knight", 100, 22, 1.6, "", 0, "Weapon", "Legendary", dict(body="#142A55", trim="#F2D25A", head="crown", weapon="greatsword", emblem="fleur", cape="#8E1C1C")),
]

BOSSES = {
    "reginald": dict(title="Réginald's Choice", yieldAt=0.12, phases=[
        dict(below=1.0, abilities=[dict(kind="summon", monster="garrison_crossbowman", name="Garrison Crossbowman", count=2, max=4, every=14, first=6)]),
        dict(below=0.5, announce="\"Hold the line! For the king!\"", damageMul=1.25, abilities=[
            dict(kind="charge", every=9, first=2, radius=110, damageMul=2.5),
            dict(kind="summon", monster="garrison_pikeman", name="Garrison Pikeman", count=2, max=4, every=16, first=4)]),
    ], choice=dict(prompt="Réginald lowers his sword. \"The king lied to me as well. Do what you must.\"", options=[
        dict(id="spare", text="Spare him: he may yet serve the truth", unlock="reginald_spared", outcome="spare",
             line="{#9FD68A}Réginald kneels, then rises. \"Find me when the hour comes. My men will follow.\"{/}"),
        dict(id="kill", text="Kill him: his orders will speak for him", unlock="reginald_killed", outcome="kill",
             line="{#E0A060}Réginald falls. In his satchel: the king's orders, and the lies they were built on.{/}")])),
    "marcus": dict(title="Marcus' Archive", deathCutscene="marcus_archive", phases=[
        dict(below=1.0, abilities=[
            dict(kind="telegraph", every=6, first=3, radius=95, delay=1.3, damageMul=3.0, count=2),
            dict(kind="curse", every=14, first=8, amount=0.25, text="\"Confess.\" Your arms grow heavy.")]),
        dict(below=0.66, announce="He opens the torture logs. The jailers come.", shieldedByAdds=True, abilities=[
            dict(kind="summon", monster="inquisition_jailer", name="Jailer", count=3, max=3, every=40, first=0),
            dict(kind="telegraph", every=7, first=4, radius=95, delay=1.3, damageMul=3.0, count=2)]),
        dict(below=0.33, announce="\"The king's will is God's will!\"", damageMul=1.35, speedMul=1.15, abilities=[
            dict(kind="nova", every=8, first=2, radius=220, delay=1.4, damageMul=3.5, text="He calls down judgement!"),
            dict(kind="telegraph", every=5, first=3, radius=95, delay=1.2, damageMul=3.0, count=3)]),
    ]),
    "arnaud": dict(title="The Betrayer", yieldAt=0.1, phases=[
        dict(below=1.0, abilities=[dict(kind="charge", every=8, first=4, radius=110, damageMul=2.5)]),
        dict(below=0.6, announce="\"You could have joined us!\"", damageMul=1.3, abilities=[
            dict(kind="charge", every=7, first=2, radius=110, damageMul=2.5),
            dict(kind="summon", monster="apostate_templar", name="Apostate Brother", count=2, max=3, every=18, first=3)]),
        dict(below=0.3, announce="He fights like a Templar, because he was one.", damageMul=1.5, speedMul=1.2, abilities=[
            dict(kind="nova", every=7, first=2, radius=180, delay=1.1, damageMul=3.0),
            dict(kind="charge", every=6, first=4, radius=110, damageMul=2.5)]),
    ], choice=dict(prompt="Arnaud drops to one knee, bleeding. \"Finish it, brother. Or don't.\"", options=[
        dict(id="spare", text="Redemption: let him live and make amends", unlock="arnaud_spared", outcome="spare",
             line="{#9FD68A}Arnaud bows his head. \"I will be at the stake when you need me.\"{/}"),
        dict(id="kill", text="Death: the Rule has no place for traitors", unlock="arnaud_killed", outcome="kill",
             line="{#E0A060}Arnaud de Foix dies as he lived, sword in hand.{/}")])),
    "inquisitor_general": dict(title="The Legate's Vault", deathCutscene="legate_vault", phases=[
        dict(below=1.0, abilities=[
            dict(kind="telegraph", every=6, first=3, radius=100, delay=1.2, damageMul=3.2, count=3),
            dict(kind="summon", monster="papal_guard", name="Papal Guard", count=2, max=4, every=18, first=6)]),
        dict(below=0.5, announce="\"Every one of you will confess before the end.\"", shieldedByAdds=True, abilities=[
            dict(kind="summon", monster="legate_inquisitor", name="Inquisitor", count=3, max=3, every=45, first=0),
            dict(kind="curse", every=12, first=5, amount=0.3)]),
        dict(below=0.25, announce="The vault's seals burn.", damageMul=1.4, abilities=[
            dict(kind="nova", every=7, first=1, radius=240, delay=1.4, damageMul=3.5),
            dict(kind="telegraph", every=4, first=3, radius=100, delay=1.1, damageMul=3.2, count=3)]),
    ]),
    "nogaret": dict(title="The King's Council", phases=[
        dict(below=1.0, abilities=[
            dict(kind="summon", monster="household_knight", name="Knight of the Household", count=2, max=4, every=15, first=3),
            dict(kind="telegraph", every=6, first=4, radius=100, delay=1.2, damageMul=3.0, count=2)]),
        dict(below=0.5, announce="\"I arrested your Grand Master myself. I will do the same to you.\"", damageMul=1.3, abilities=[
            dict(kind="curse", every=10, first=2, amount=0.3, text="He reads the charges aloud."),
            dict(kind="nova", every=9, first=4, radius=220, delay=1.3, damageMul=3.2)]),
    ]),
    "clement": dict(title="Clement's Confession", yieldAt=0.1, phases=[
        dict(below=1.0, abilities=[
            dict(kind="summon", monster="cardinal_inquisitor", name="Cardinal's Inquisitor", count=2, max=4, every=14, first=4),
            dict(kind="telegraph", every=6, first=3, radius=110, delay=1.3, damageMul=3.2, count=3)]),
        dict(below=0.6, announce="\"The king forced my hand!\"", shieldedByAdds=True, abilities=[
            dict(kind="summon", monster="vatican_champion", name="Champion of the Curia", count=3, max=3, every=50, first=0),
            dict(kind="heal", every=12, first=6, amount=0.04, text="He prays, and is answered.")]),
        dict(below=0.3, announce="Bells ring across Avignon.", damageMul=1.4, abilities=[
            dict(kind="nova", every=7, first=2, radius=240, delay=1.4, damageMul=3.4),
            dict(kind="telegraph", every=4, first=3, radius=110, delay=1.2, damageMul=3.0, count=3)]),
    ], choice=dict(prompt="Clement falls to his knees. \"I signed what I was given. Mercy, in God's name.\"", options=[
        dict(id="spare", text="Spare him: let him live with what he signed", unlock="clement_spared", outcome="spare",
             line="{#9FD68A}The Pope weeps. \"Go to the Tower. Philip waits. God forgive us both.\"{/}"),
        dict(id="condemn", text="Condemn him: he answers for the pyres", unlock="clement_condemned", outcome="kill",
             line="{#E0A060}\"Within the year,\" you tell him, \"you will stand before God.\" He does not live to see it end.{/}")])),
    "philip_iv": dict(title="The Tower of Truth", phases=[
        dict(below=1.0, abilities=[
            dict(kind="charge", every=8, first=4, radius=120, damageMul=2.6),
            dict(kind="summon", monster="chosen_knight", name="Chosen of the King", count=2, max=4, every=16, first=6)]),
        dict(below=0.7, announce="\"I am the chosen of God!\"", shieldedByAdds=True, abilities=[
            dict(kind="summon", monster="household_knight", name="Royal Guard", count=4, max=4, every=60, first=0),
            dict(kind="telegraph", every=5, first=2, radius=110, delay=1.2, damageMul=3.2, count=3)]),
        dict(below=0.4, announce="The legendary sword burns white.", damageMul=1.35, speedMul=1.1, abilities=[
            dict(kind="nova", every=8, first=2, radius=260, delay=1.5, damageMul=3.6, text="He raises the sword of the kings!"),
            dict(kind="charge", every=6, first=4, radius=120, damageMul=2.6),
            dict(kind="curse", every=15, first=8, amount=0.3, text="\"Kneel.\"")]),
        dict(below=0.15, announce="The Iron King stands alone.", damageMul=1.6, speedMul=1.2, abilities=[
            dict(kind="nova", every=5, first=1, radius=260, delay=1.3, damageMul=3.6),
            dict(kind="telegraph", every=4, first=2, radius=110, delay=1.1, damageMul=3.0, count=4)]),
    ]),
}

# ---------------------------------------------------------------- towns and their NPCs
def npc(id, name, role, services, x, y, lines=(), look=None, **kw):
    d = dict(id=id, name=name, role=role, services=list(services), x=x, y=y, lines=list(lines))
    d.update(kw)
    if look: d["_look"] = look
    return d

TOWNS = {
    "town_in_shadow": dict(name="The Town in Shadow", act=2, levelMin=21, levelMax=40, tile="tiles/act2_cobbles.png",
        description="A market town that keeps its shutters closed and its cellars open to the Order.",
        cutscene="town_in_shadow", arena="The Tanners' Pit", music="town_act2",
        npcs=[
            npc("gerard_act2", "Gerard de Villiers", "Leader of the rebels", ["main_quest"], 1520, 560,
                ["Every brother we save is another blade. Every blade is another night we survive.",
                 "The king wants us erased. Not dead: erased. As if the Temple never was."],
                dict(body="#F0ECE2", trim="#B3241E", head="bare_beard", weapon="sword", emblem="cross")),
            npc("eleanor_of_valois", "Eleanor of Valois", "Spy and merchant", ["buy", "sell", "gamble"], 880, 560,
                ["This is business. Nothing more.", "Gold buys silence in this town. I have a great deal of both."],
                dict(body="#3A2448", trim="#C9A227", head="hood", weapon="none")),
            npc("jean_baptiste", "Jean-Baptiste", "Former monk, healer", ["heal", "potions"], 880, 1060,
                ["I left the Church when I read what it keeps in its cellars.", "Sit. The Church taught me two things: Latin, and how to close a wound."],
                dict(body="#6A5A44", trim="#2A2018", head="tonsure", weapon="book")),
            npc("odette_the_farrier", "Odette the Farrier", "Smith", ["forge"], 1520, 1060,
                ["Aubry taught me the forge. He'd want it lit for you."], dict(body="#5A3A24", trim="#2A1A10", head="bare", weapon="hammer"),
                unlock="forge", lockedLine="My forge stays cold until Master Aubry's is lit again. Find his hammer."),
            npc("strongbox_act2", "The Order's Strongbox", "Your stash", ["stash"], 1200, 1420, [], dict(shape="chest")),
            npc("broker_act2", "Lucas the Broker", "Auction house", ["auction"], 1880, 560,
                ["Same ledger as Jehan's. A sale here is a sale in every town."], dict(body="#2E4A3A", trim="#C9A227", head="hat", weapon="scroll")),
        ]),
    "valois_estate": dict(name="The Estate of Count de Valois", act=3, levelMin=41, levelMax=70, tile="tiles/act3_gardens.png",
        description="Hedges, fountains and a count who smiles at the king and hides his enemies.",
        cutscene="valois_estate", arena="The Tilting Yard", music="town_act3",
        npcs=[
            npc("count_de_valois", "Count de Valois", "Your host", ["main_quest"], 1520, 560,
                ["My brother is the king's brother. Do not mistake that for loyalty.",
                 "Proof, Templar. Without proof, your Grand Master burns and the world shrugs."],
                dict(body="#3A2E6A", trim="#F2D25A", head="hat", weapon="none", emblem="fleur")),
            npc("bishop_of_senlis", "The Bishop of Senlis", "Healer", ["heal", "potions"], 880, 1060,
                ["Not every bishop signed the king's list. Sit down; you are bleeding on my carpet."],
                dict(body="#E8E2D2", trim="#8A1C18", head="mitre", weapon="crozier")),
            npc("marie", "Marie", "Spy in the papal court", ["talk"], 1880, 1060,
                ["A cleric's robe opens more doors than a sword. Don't run in it.",
                 "The Legate keeps his proof in a vault under his palace. The keeper never sleeps."],
                dict(body="#2A1E2A", trim="#C9B88A", head="hood", weapon="none")),
            npc("isabeau_clothier", "Isabeau the Clothier", "Merchant", ["buy", "sell", "gamble"], 880, 560,
                ["Silk, steel or a good lie: I sell all three."], dict(body="#7A2A4A", trim="#E6C14A", head="hood", weapon="none")),
            npc("renier_armourer", "Renier the Armourer", "Smith", ["forge"], 1520, 1060,
                ["The count pays me to make toys. You I'll make weapons."], dict(body="#4A3A2A", trim="#2A1A10", head="bare_beard", weapon="hammer"),
                unlock="forge", lockedLine="No forge for you until Aubry's hammer rings again."),
            npc("strongbox_act3", "The Order's Strongbox", "Your stash", ["stash"], 1200, 1420, [], dict(shape="chest")),
            npc("broker_act3", "Mathilde the Broker", "Auction house", ["auction"], 1880, 560,
                ["The count's servants buy here too. Price accordingly."], dict(body="#2E4A3A", trim="#C9A227", head="hood", weapon="scroll")),
        ]),
    "remnant_camp": dict(name="The Remnant's Camp", act=4, levelMin=71, levelMax=100, tile="tiles/act4_camp.png",
        description="Tents in the frost outside the walls of Paris. Everyone who chose you is here.",
        cutscene="remnant_camp", arena="The Ring of Banners", music="town_act4",
        npcs=[
            npc("gerard_act4", "Gerard de Villiers", "Commander of the Remnant", ["main_quest"], 1520, 560,
                ["Look at them. Every one of them is here because of you.",
                 "Prove yourself worthy of command. Then prove it again tomorrow."],
                dict(body="#F0ECE2", trim="#B3241E", head="bare_beard", weapon="sword", emblem="cross")),
            npc("eleanor_act4", "Eleanor of Valois", "Quartermaster", ["buy", "sell", "gamble"], 880, 560,
                ["Business is very good when the world is ending."], dict(body="#3A2448", trim="#C9A227", head="hood", weapon="none")),
            npc("ysabeau_act4", "Sister Ysabeau", "Infirmarian", ["heal", "potions"], 880, 1060,
                ["I followed you from Payns. I'm not stopping at the walls of Paris."], dict(body="#F0ECE2", trim="#8A1C18", head="hood_white", weapon="none")),
            npc("aubry_act4", "Master Aubry", "Armourer", ["forge"], 1520, 1060,
                ["The last forge of the Temple. Make it count."], dict(body="#4A3A2A", trim="#2A1A10", head="bare_beard", weapon="hammer"),
                unlock="forge", lockedLine="My hammer is still in the quarry, brother."),
            npc("thomas_act4", "Brother Thomas", "Archivist", ["talk"], 1980, 1300,
                ["You came back for me in Payns. I won't forget it.", "I kept every name. When this is over, someone has to remember them."],
                dict(body="#6A5A44", trim="#2A2018", head="tonsure", weapon="book"), hiddenUntil="thomas_trust"),
            npc("reginald_act4", "Captain Réginald", "Turned captain", ["talk"], 420, 1300,
                ["My men are yours. The king lied to all of us."], dict(body="#23407A", trim="#F2D25A", head="bare_beard", weapon="sword", emblem="fleur"),
                hiddenUntil="reginald_spared"),
            npc("arnaud_act4", "Arnaud de Foix", "Penitent knight", ["talk"], 420, 560,
                ["I sold my brothers once. I will not do it twice."], dict(body="#E6E2D8", trim="#1A1A1A", head="bare_beard", weapon="sword", emblem="cross"),
                hiddenUntil="arnaud_spared"),
            npc("marie_act4", "Marie", "Spy", ["talk"], 1980, 820,
                ["Avignon trembles. Clement knows you're coming."], dict(body="#2A1E2A", trim="#C9B88A", head="hood", weapon="none")),
            npc("strongbox_act4", "The Order's Strongbox", "Your stash", ["stash"], 1200, 1420, [], dict(shape="chest")),
            npc("broker_act4", "Jehan the Broker", "Auction house", ["auction"], 1880, 560,
                ["Even here, even now. Someone has to keep the ledger."], dict(body="#2E4A3A", trim="#C9A227", head="hat", weapon="scroll")),
        ]),
}

# ---------------------------------------------------------------- zones
# id, name, act, levelMin, levelMax, size(w,h), packs, spawns [(monster, weight, gmin, gmax)], superunique, boss, extra
def Z(id, name, act, lo, hi, size, packs, spawns, su=None, boss=None, tile=None, **kw):
    return dict(id=id, name=name, act=act, lo=lo, hi=hi, size=size, packs=packs, spawns=spawns, su=su, boss=boss, tile=tile, kw=kw)

ACT2 = [
    Z("catacomb_network_1", "The Catacomb Network", 2, 21, 23, (5760, 3240), 18,
      [("smuggler_cutthroat", 40, 3, 5), ("smuggler_bruiser", 25, 2, 3), ("tunnel_rats", 25, 6, 9), ("fallen_templar", 10, 3, 4)],
      tile="tiles/act2_crypt.png", floor="Floor 1 of 3", description="The smugglers' roads under the town. Some of the dead down here wore the cross."),
    Z("catacomb_network_2", "The Catacomb Network", 2, 23, 25, (5760, 3240), 18,
      [("smuggler_cutthroat", 35, 3, 5), ("smuggler_bruiser", 30, 2, 3), ("tunnel_rats", 15, 6, 9), ("fallen_templar", 20, 3, 5)],
      su="bastien", tile="tiles/act2_crypt.png", floor="Floor 2 of 3", description="Bastien runs the river crossings. Nothing moves here without his toll."),
    Z("catacomb_network_3", "The Catacomb Network", 2, 25, 27, (5760, 3240), 18,
      [("fallen_templar", 45, 4, 6), ("smuggler_bruiser", 20, 2, 3), ("tunnel_rats", 15, 6, 9), ("chapel_wraith", 20, 3, 5)],
      su="brother_guichard", tile="tiles/act2_crypt.png", floor="Floor 3 of 3", description="The first Fallen Templars. They remember the Rule, and nothing else."),
    Z("royal_garrison", "The Royal Garrison", 2, 26, 31, (7680, 4320), 24,
      [("garrison_pikeman", 40, 4, 6), ("garrison_crossbowman", 30, 3, 4), ("garrison_knight", 15, 1, 2), ("warhound", 15, 3, 4)],
      boss="reginald", tile="tiles/act2_yard.png", description="Captain Réginald holds the road for the king. He has started asking questions."),
    Z("abandoned_castle", "The Abandoned Castle", 2, 30, 35, (7680, 4320), 26,
      [("routier", 45, 4, 6), ("routier_archer", 30, 3, 5), ("smuggler_bruiser", 15, 2, 3), ("warhound", 10, 3, 4)],
      su="hugues_le_routier", tile="tiles/act2_yard.png", cutscene="castle_retaken",
      description="Mercenaries squat in a Templar castle. Retake it and the Order has a home again."),
    Z("hermits_cave", "The Hermit's Cave", 2, 32, 36, (5760, 3240), 18,
      [("cave_crawler", 45, 3, 5), ("cave_bats", 35, 7, 10), ("chapel_wraith", 20, 3, 4)],
      su="pale_thing", tile="tiles/act2_cave.png", cutscene="hermit_secret",
      description="The Hermit's first secret lies past what lives in the well."),
    Z("inquisition_hq_1", "Inquisition Headquarters", 2, 36, 38, (5760, 3240), 20,
      [("inquisition_jailer", 40, 3, 5), ("dominican_inquisitor", 25, 2, 3), ("penitent_zealot", 35, 3, 5)],
      tile="tiles/act2_stone.png", floor="Floor 1 of 3", description="The cells. Some of the screams are still going."),
    Z("inquisition_hq_2", "Inquisition Headquarters", 2, 37, 39, (5760, 3240), 20,
      [("inquisition_jailer", 30, 3, 5), ("dominican_inquisitor", 30, 2, 3), ("inquisition_executioner", 20, 1, 2), ("penitent_zealot", 20, 3, 5)],
      su="warden_of_cells", tile="tiles/act2_stone.png", floor="Floor 2 of 3", description="The question rooms."),
    Z("inquisition_hq_3", "Inquisition Headquarters", 2, 39, 40, (5760, 3240), 16,
      [("dominican_inquisitor", 35, 2, 4), ("inquisition_executioner", 35, 1, 2), ("inquisition_jailer", 30, 3, 5)],
      boss="marcus", tile="tiles/act2_stone.png", floor="Floor 3 of 3", description="Marcus keeps his archive at the bottom of the building, where no one can hear."),
]
ACT3 = [
    Z("kings_forest_west", "The King's Forest", 3, 41, 44, (7680, 4320), 26,
      [("royal_huntsman", 30, 2, 4), ("kings_forester", 35, 3, 5), ("grey_wolf", 35, 4, 6)],
      su="master_of_the_hunt", tile="tiles/act3_forest.png", floor="West", description="Royal hunting grounds. The hunting parties are hunting men today."),
    Z("kings_forest_east", "The King's Forest", 3, 44, 48, (7680, 4320), 26,
      [("royal_huntsman", 25, 2, 4), ("kings_forester", 30, 3, 5), ("hunting_boar", 25, 2, 3), ("grey_wolf", 20, 4, 6)],
      su="black_boar", tile="tiles/act3_forest.png", floor="East", description="Secret paths run east toward the court. The Black Boar has never been taken."),
    Z("monastery_of_trials", "The Monastery of the Trials", 3, 46, 52, (5760, 3240), 22,
      [("corrupt_monk", 35, 2, 4), ("trial_executioner", 25, 1, 2), ("penitent_brother", 40, 3, 5)],
      su="prior_anselme", tile="tiles/act3_cloister.png", description="Corrupt clergy stage trials of the Order here, with verdicts written in advance."),
    Z("royal_court_outskirts", "The Royal Court", 3, 48, 56, (7680, 4320), 28,
      [("court_guard", 45, 3, 5), ("court_sergeant", 15, 1, 2), ("court_spy", 25, 2, 3), ("court_hound", 15, 2, 3)],
      su="keeper_of_rolls", tile="tiles/act3_court.png", cutscene="court_disguise", infiltration=True, disguiseUnlock="cleric_robes",
      description="Walk the court as a cleric. Guards see a robe from afar; up close, they see a Templar."),
    Z("underground_cathedral_1", "The Underground Cathedral", 3, 55, 58, (5760, 3240), 22,
      [("cathedral_shade", 35, 3, 5), ("ossuary_knight", 30, 2, 3), ("apostate_templar", 35, 2, 3)],
      su="ossuary_abbot", tile="tiles/act3_cathedral.png", floor="The Nave", cutscene="de_molay_bars",
      description="A cathedral dug beneath the city. Its cells hold the Grand Master."),
    Z("underground_cathedral_2", "The Underground Cathedral", 3, 58, 62, (5760, 3240), 20,
      [("apostate_templar", 50, 2, 4), ("cathedral_shade", 30, 3, 5), ("ossuary_knight", 20, 2, 3)],
      boss="arnaud", tile="tiles/act3_cathedral.png", floor="The Crypt", description="Sir Arnaud de Foix waits with the brothers he sold."),
    Z("legates_palace_1", "The Papal Legate's Palace", 3, 62, 65, (5760, 3240), 22,
      [("papal_guard", 45, 3, 5), ("papal_crossbowman", 35, 2, 4), ("legate_inquisitor", 20, 1, 3)],
      tile="tiles/act3_marble.png", floor="Floor 1 of 3", description="The Legate's palace, full of Rome's soldiers."),
    Z("legates_palace_2", "The Papal Legate's Palace", 3, 65, 68, (5760, 3240), 22,
      [("papal_guard", 35, 3, 5), ("papal_crossbowman", 25, 2, 4), ("legate_inquisitor", 20, 2, 3), ("vault_warden", 20, 1, 2)],
      su="keeper_of_the_vault", tile="tiles/act3_marble.png", floor="Floor 2 of 3", description="The vault. Marie says the proof is here, if the keeper falls."),
    Z("legates_palace_3", "The Papal Legate's Palace", 3, 68, 70, (5760, 3240), 18,
      [("legate_inquisitor", 35, 2, 4), ("vault_warden", 30, 1, 2), ("papal_guard", 35, 3, 5)],
      boss="inquisitor_general", tile="tiles/act3_marble.png", floor="Floor 3 of 3", description="The Inquisitor General hears no confession he did not write."),
]
ACT4 = [
    Z("place_de_greve", "The Place de Grève", 4, 71, 78, (7680, 4320), 30,
      [("paris_zealot", 40, 4, 6), ("royal_guardsman", 35, 3, 5), ("royal_archer", 25, 2, 4)],
      su="provost", tile="tiles/act4_square.png", cutscene="stake_at_greve", allies=2,
      allyUnlocks=["thomas_trust", "reginald_spared", "arnaud_spared", "castle_base", "hermit_secret"],
      description="The execution square. The stake is being built. Your army stands with you."),
    Z("royal_palace_1", "The Royal Palace", 4, 78, 80, (5760, 3240), 24,
      [("royal_guardsman", 45, 3, 5), ("royal_archer", 30, 2, 4), ("household_knight", 25, 1, 2)],
      tile="tiles/act4_palace.png", floor="Floor 1 of 3", description="The palace on the Île de la Cité."),
    Z("royal_palace_2", "The Royal Palace", 4, 80, 83, (5760, 3240), 24,
      [("royal_guardsman", 35, 3, 5), ("royal_archer", 25, 2, 4), ("household_knight", 25, 1, 2), ("royal_almoner", 15, 1, 2)],
      su="captain_of_the_guard", tile="tiles/act4_palace.png", floor="Floor 2 of 3", description="The Royal Guard's own halls."),
    Z("royal_palace_3", "The Royal Palace", 4, 83, 85, (5760, 3240), 20,
      [("household_knight", 40, 2, 3), ("royal_almoner", 30, 1, 3), ("royal_guardsman", 30, 3, 5)],
      boss="nogaret", tile="tiles/act4_palace.png", floor="Floor 3 of 3", description="Philip's war council sits above the river."),
    Z("notre_dame", "Notre Dame Cathedral", 4, 85, 90, (7680, 4320), 28,
      [("vatican_champion", 30, 1, 3), ("cardinal_inquisitor", 30, 2, 3), ("living_effigy", 40, 2, 3)],
      su="cardinals_champion", tile="tiles/act4_cathedral.png", description="The Vatican's finest came north to see the Order end."),
    Z("papal_palace", "The Papal Palace", 4, 88, 94, (7680, 4320), 26,
      [("vatican_champion", 35, 1, 3), ("cardinal_inquisitor", 35, 2, 3), ("papal_guard", 30, 3, 5)],
      boss="clement", tile="tiles/act4_palace.png", description="Clement waits among his cardinals. He knows why you have come."),
    Z("tower_of_truth_1", "The Tower of Truth", 4, 95, 96, (4800, 2700), 18,
      [("tower_warden", 40, 1, 2), ("confession_wraith", 30, 3, 4), ("chosen_knight", 30, 1, 2)],
      su="tower_gaoler", tile="tiles/act4_tower.png", floor="Floor 1 of 5", cutscene="tower_of_truth", description="De Molay and de Charney are held at the top."),
    Z("tower_of_truth_2", "The Tower of Truth", 4, 96, 97, (4800, 2700), 18,
      [("chosen_knight", 40, 1, 3), ("confession_wraith", 35, 3, 4), ("living_effigy", 25, 2, 3)],
      tile="tiles/act4_tower.png", floor="Floor 2 of 5", description="Every floor is a confession."),
    Z("tower_of_truth_3", "The Tower of Truth", 4, 97, 98, (4800, 2700), 18,
      [("chosen_knight", 45, 1, 3), ("tower_warden", 30, 1, 2), ("royal_almoner", 25, 1, 2)],
      su="marigny", tile="tiles/act4_tower.png", floor="Floor 3 of 5", description="The king's chamberlain bars the stair."),
    Z("tower_of_truth_4", "The Tower of Truth", 4, 98, 99, (4800, 2700), 18,
      [("confession_wraith", 45, 3, 5), ("chosen_knight", 35, 1, 3), ("tower_warden", 20, 1, 2)],
      su="living_confession", tile="tiles/act4_tower.png", floor="Floor 4 of 5", description="The confessions the Order never made have taken a shape."),
    Z("tower_of_truth_5", "The Tower of Truth", 4, 99, 100, (4800, 2700), 10,
      [("chosen_knight", 60, 2, 3), ("household_knight", 40, 1, 2)],
      boss="philip_iv", tile="tiles/act4_tower.png", floor="Floor 5 of 5", description="The top of the tower. The king of France, a legendary sword, and his royal guard."),
]

PROLOGUE = Z("payns_courtyard", "The Courtyard at Payns", 1, 1, 1, (2600, 1700), 0, [], tile="tiles/act1_burning.png",
             cutscene="prologue", description="13 October 1307. The preceptory burns. Get to the crypt stair.")

# ---------------------------------------------------------------- quests
QUESTS = [
    dict(id="q0_out_of_the_fire", name="Out of the Fire", zone="sanctuary_of_the_broken_seal", act=1,
         objective=dict(text="Fight through the courtyard to the crypt stair", type="EnterZone", target="sanctuary_of_the_broken_seal"),
         reward=dict(buildPoints=0, unlock="prologue_done"), giver=None, prologue=True),
    # Act II
    dict(id="q7_smugglers_road", name="The Smugglers' Road", zone="catacomb_network_2", act=2, giver="gerard_act2",
         briefing="The network runs on the smugglers' roads under the town, and Bastien the Ferryman owns them. Make him an ally or make him gone.",
         objective=dict(text="Defeat Bastien the Ferryman in the Catacomb Network", type="KillUnique", target="bastien"),
         reward=dict(buildPoints=1, gold=1500)),
    dict(id="q8_reginalds_choice", name="Réginald's Choice", zone="royal_garrison", act=2, giver="gerard_act2", after="q7_smugglers_road",
         briefing="Captain Réginald holds the garrison road. They say he doubts the king's story. Beat him, then decide what he is worth.",
         objective=dict(text="Defeat Captain Réginald at the Royal Garrison, then spare or kill him", type="KillUnique", target="reginald"),
         reward=dict(buildPoints=1)),
    dict(id="q9_castle_retaken", name="The Castle Retaken", zone="abandoned_castle", act=2, giver="gerard_act2", after="q8_reginalds_choice",
         briefing="There is a Templar castle on the ridge, full of routiers who think it's theirs. Clear it. Prove yourself worthy of command first.",
         objective=dict(text="Clear the Abandoned Castle", type="ClearZone", target="abandoned_castle"),
         reward=dict(buildPoints=1, flags=["castle_base"])),
    dict(id="q10_hermits_secret", name="The Hermit's First Secret", zone="hermits_cave", act=2, optional=True,
         objective=dict(text="Kill the Pale Thing in the Hermit's Cave", type="KillUnique", target="pale_thing"),
         reward=dict(buildPoints=1, item="Elite", flags=["hermit_secret"])),
    dict(id="q11_marcus_archive", name="Marcus' Archive", zone="inquisition_hq_3", act=2, giver="gerard_act2", after="q9_castle_retaken",
         briefing="Inquisitor Marcus keeps the torture logs. With them, no one can say the confessions were true. Bring them out.",
         objective=dict(text="Defeat Inquisitor Marcus beneath the Inquisition Headquarters", type="KillUnique", target="marcus"),
         reward=dict(buildPoints=1, unlock="act3")),
    # Act III
    dict(id="q12_through_the_forest", name="Through the King's Forest", zone="kings_forest_east", act=3, giver="count_de_valois",
         briefing="The only road to court that the king doesn't watch runs through his own forest. Take it, and take the Black Boar's head while you're there. Marie will have robes for you when you return.",
         objective=dict(text="Kill the Black Boar of Vincennes in the King's Forest", type="KillUnique", target="black_boar"),
         reward=dict(buildPoints=1, unlock="cleric_robes")),
    dict(id="q13_court_records", name="Disguised at Court", zone="royal_court_outskirts", act=3, giver="count_de_valois", after="q12_through_the_forest",
         briefing="Walk the court in Marie's robes. The Keeper of the Rolls carries the arrest orders. Don't run, don't draw steel until you must.",
         objective=dict(text="Take the court records from the Keeper of the Rolls", type="KillUnique", target="keeper_of_rolls"),
         reward=dict(buildPoints=1, flags=["court_records"])),
    dict(id="q14_the_betrayer", name="The Betrayer", zone="underground_cathedral_2", act=3, giver="count_de_valois", after="q13_court_records",
         briefing="The man who sold your Grand Master to the king was a Templar. Sir Arnaud de Foix. He's under the cathedral. Redemption or death: it's personal now.",
         objective=dict(text="Defeat Sir Arnaud de Foix in the Underground Cathedral", type="KillUnique", target="arnaud"),
         reward=dict(buildPoints=1)),
    dict(id="q15_the_proof", name="The Legate's Vault", zone="legates_palace_2", act=3, optional=True,
         objective=dict(text="Defeat the Keeper of the Vault so Marie can carry out the proof", type="KillUnique", target="keeper_of_the_vault"),
         reward=dict(buildPoints=1, flags=["proof_secured"])),
    dict(id="q16_inquisitor_general", name="The Inquisitor General", zone="legates_palace_3", act=3, giver="count_de_valois", after="q14_the_betrayer",
         briefing="The Inquisitor General wrote half the confessions himself. End him, and the papal court loses its voice.",
         objective=dict(text="Defeat the Inquisitor General atop the Legate's Palace", type="KillUnique", target="inquisitor_general"),
         reward=dict(buildPoints=1, unlock="act4")),
    dict(id="q_trial_by_ordeal", name="Trial by Ordeal", zone="monastery_of_trials", act=3, optional=True,
         objective=dict(text="Defeat Prior Anselme the Examiner", type="KillUnique", target="prior_anselme"),
         reward=dict(buildPoints=1, item="Epic")),
    # Act IV
    dict(id="q17_stake_at_greve", name="The Stake at Grève", zone="place_de_greve", act=4, giver="gerard_act4",
         briefing="They are building the pyre in the Place de Grève. Take the square. Everyone you saved marches with you.",
         objective=dict(text="Defeat the Provost of Paris in the Place de Grève", type="KillUnique", target="provost"),
         reward=dict(buildPoints=1)),
    dict(id="q18_kings_council", name="The King's Council", zone="royal_palace_3", act=4, giver="gerard_act4", after="q17_stake_at_greve",
         briefing="Nogaret arrested the Grand Master with his own hands. He sits on Philip's council in the palace. Climb to him.",
         objective=dict(text="Defeat Guillaume de Nogaret in the Royal Palace", type="KillUnique", target="nogaret"),
         reward=dict(buildPoints=1)),
    dict(id="q19_clements_confession", name="Clement's Confession", zone="papal_palace", act=4, giver="gerard_act4", after="q18_kings_council",
         briefing="The Pope signed the bull that dissolved us. He'll say the king forced his hand. He may even be telling the truth. Find him.",
         objective=dict(text="Confront Pope Clement V, then condemn or spare him", type="KillUnique", target="clement"),
         reward=dict(buildPoints=1)),
    dict(id="q_vaticans_finest", name="The Vatican's Finest", zone="notre_dame", act=4, optional=True,
         objective=dict(text="Defeat the Cardinal's Champion in Notre Dame", type="KillUnique", target="cardinals_champion"),
         reward=dict(buildPoints=1, item="Legendary")),
    dict(id="q20_tower_of_truth", name="The Tower of Truth", zone="tower_of_truth_5", act=4, giver="gerard_act4", after="q19_clements_confession",
         briefing="De Molay and de Charney are at the top of the Tower of Truth. So is Philip. Five floors. Go.",
         objective=dict(text="Climb the Tower of Truth and defeat Philip IV", type="KillUnique", target="philip_iv"),
         reward=dict(buildPoints=2)),
]

# ---------------------------------------------------------------- cutscenes
def cs(id, title, *lines):
    return dict(id=id, title=title, image=f"ui/cutscene_{id}.png", lines=[dict(speaker=s, text=t) for s, t in lines])

CUTSCENES = [
    cs("prologue", "Payns, 13 October 1307",
       ("", "A bell rung wrong in the dark. Torchlight under the dormitory door."),
       ("A royal sergeant", "\"By order of Philip, by the grace of God King of the French: every brother of the Temple is under arrest. God is not pleased.\""),
       ("", "The Preceptor is in chains. The chapel is burning. Brother Thomas is dragged away across the yard."),
       ("", "Three soldiers stand between you and the crypt stair. Move with the mouse or W A S D; strike with the left button; drink a potion with Z.")),
    cs("sanctuary_first", "The Sanctuary of the Broken Seal",
       ("Gerard de Villiers", "\"They called us heretics. They burned our brothers. But we will survive.\""),
       ("", "A crypt beneath the burned chapel of Sainte-Croix, lit by tallow. What is left of the Order gathers here.")),
    cs("ledger_of_payns", "The Ledger of Payns",
       ("", "Imbert's ledger lies open in the ash. Every confession in it is in the same hand: his."),
       ("Gerard de Villiers", "\"Proof. The first proof that they were forced. Take it south; there are people in the Town in Shadow who will hide it, and you.\"")),
    cs("town_in_shadow", "The Town in Shadow",
       ("Eleanor of Valois", "\"This is business. Nothing more.\""),
       ("", "A market town that closes its shutters at dusk and opens its cellars to the Order. The network has a home.")),
    cs("castle_retaken", "The Castle Retaken",
       ("", "A Templar castle on the ridge, its banners torn down, its hall full of routiers."),
       ("Gerard de Villiers", "\"Take it back, and the Order has walls again. Prove yourself worthy of command first.\"")),
    cs("hermit_secret", "The Hermit's First Secret",
       ("The Hermit", "\"You want to know what the Temple guarded? Not gold. Debts. The king owed us everything, and a dead creditor sends no bills.\""),
       ("", "Somewhere below, something pale moves in the well.")),
    cs("marcus_archive", "Marcus' Archive",
       ("", "Inquisitor Marcus falls among his records. The torture logs are all here: dates, methods, the names of the dead."),
       ("Gerard de Villiers", "\"This is not one man's cruelty. It's a plan. The king means to erase every Templar from the earth.\""),
       ("", "The road north, to the court of France, is open.")),
    cs("valois_estate", "The Estate of Count de Valois",
       ("Count de Valois", "\"My brother is the king's brother, and my house is yours. Do not ask me why.\""),
       ("Marie", "\"I carry letters for the papal court. They trust me. They shouldn't.\"")),
    cs("court_disguise", "Disguised at Court",
       ("Marie", "\"Walk, don't run. Bow to the clerks. The guards see the robe from across a hall; up close, they see your face.\""),
       ("", "If the disguise breaks, every guard nearby will come for you.")),
    cs("de_molay_bars", "De Molay Through the Bars",
       ("Jacques de Molay", "\"I confessed nothing that was true. Nogaret signed the orders; the Pope signed what he was given; the king signed us away.\""),
       ("", "The Grand Master of the Temple, imprisoned, defiant, and alive. For now.")),
    cs("legate_vault", "The Legate's Vault",
       ("", "The Inquisitor General falls. The vault beneath him is full of letters between Paris and the Curia."),
       ("Marie", "\"If we carried out the proof, de Molay has a defence. If not... then we fight with what we have.\""),
       ("", "The road to Paris is open. They are building the pyre.")),
    cs("remnant_camp", "The Remnant's Camp",
       ("", "Tents in the frost outside the walls of Paris. Everyone you recruited, spared or saved is here."),
       ("Gerard de Villiers", "\"Look at them. Every one of them is here because of you.\"")),
    cs("stake_at_greve", "The Stake at Grève",
       ("", "The pyre is being built in the Place de Grève. The crowd is already gathering."),
       ("", "Your army is at your back. The size of it is the story of everything you have done.")),
    cs("tower_of_truth", "The Tower of Truth",
       ("", "Five floors. De Molay and de Charney at the top. And Philip IV, with a legendary sword and his royal guard."),
       ("Philip IV", "\"I am the chosen of God!\"")),
    cs("ending_rescue", "The Rescue",
       ("", "De Molay walks out of the Tower of Truth on his own feet. The proof goes to every court in Christendom."),
       ("Jacques de Molay", "\"The Order lives, because you would not let it die.\""),
       ("", "The Temple is restored. Brother Thomas writes down every name.")),
    cs("ending_vengeance", "The Vengeance",
       ("", "You were too late for the Grand Master. The pyre burns on the Île aux Juifs."),
       ("Jacques de Molay", "\"Pope Clement, King Philip: before the year is out, I summon you to the judgement of God.\""),
       ("", "Within the year, both are dead. The Temple has its vengeance, and nothing else.")),
    cs("ending_darkness", "The Darkness",
       ("", "Every enemy who knelt, you killed. Every door, you burned."),
       ("Gerard de Villiers", "\"We fought so long against the king's methods that we became them.\""),
       ("", "The Remnant scatters. Vengeance has consumed you, and the Temple with you.")),
    cs("ending_redemption", "The Redemption",
       ("", "The Order of the Temple ends. Its brothers do not."),
       ("Gerard de Villiers", "\"In Portugal, the king will take us in under a new name. The Order of Christ.\""),
       ("", "The ideals live on. Somewhere in Tomar, a white mantle with a red cross is folded and put away, and kept.")),
]

ACT1_EXTRA_CUTSCENES = {"sanctuary_of_the_broken_seal": "sanctuary_first"}
ACT1_DEATH_CUTSCENES = {"imbert": "ledger_of_payns"}

# ---------------------------------------------------------------- build
def zone_json(z, prev_id, next_id, back_to_town=None):
    w, h = z["size"]
    mid = h // 2
    d = dict(id=z["id"], name=z["name"], act=z["act"], levelMin=z["lo"], levelMax=z["hi"], width=w, height=h,
             playerSpawn=[320, mid], safeRadius=700, packCount=z["packs"],
             rarityCap={1: "Rare", 2: "Epic", 3: "Legendary", 4: "Legendary"}[z["act"]],
             goldMin=round(1.4 * z["lo"]), goldMax=round(5.5 * z["hi"]),
             spawns=[dict(monster=m, weight=wt, groupMin=a, groupMax=b) for m, wt, a, b in z["spawns"]],
             superunique=z["su"], superuniquePosition=[w - 700, mid] if z["su"] else None,
             groundTile=z["tile"] or "tiles/ground_placeholder.png", description=z["kw"].get("description", ""),
             exits=[], waypoint=[620, mid + 260] if z["packs"] > 0 else None, isTown=False, generated=True,
             music=f"act{z['act']}_field" if z["packs"] else "prologue")
    if prev_id: d["exits"].append(dict(to=prev_id, x=120, y=mid))
    if next_id: d["exits"].append(dict(to=next_id, x=w - 140, y=mid))
    if z["boss"]:
        d["boss"] = z["boss"]
        d["bossPosition"] = [w - 700, mid]
    for k in ("cutscene", "floor", "infiltration", "disguiseUnlock", "allies", "allyUnlocks"):
        if k in z["kw"]: d[k] = z["kw"][k]
    return d

def town_json(tid, t, exits):
    d = dict(id=tid, name=t["name"], act=t["act"], levelMin=t["levelMin"], levelMax=t["levelMax"], width=2600, height=1700,
             playerSpawn=[1200, 820], safeRadius=0, packCount=0, rarityCap="Common", goldMin=0, goldMax=0, spawns=[],
             superunique=None, superuniquePosition=None, groundTile=t["tile"], description=t["description"], isTown=True,
             waypoint=[1200, 1180], arena=dict(name=t["arena"], x=180, y=1150, width=560, height=420),
             exits=exits, cutscene=t["cutscene"], music=t["music"], generated=True,
             npcs=[{k: v for k, v in n.items() if not k.startswith("_")} | {"sprite": f"sprites/npc_{n['id']}.png"} for n in t["npcs"]])
    return d

def main(check=False):
    changes = []
    def write(path, obj):
        text = json.dumps(obj, indent=1, ensure_ascii=False) + "\n"
        old = open(path, encoding="utf-8").read() if os.path.exists(path) else None
        if old != text:
            changes.append(os.path.relpath(path, ROOT))
            if not check:
                with open(path, "w", encoding="utf-8", newline="\n") as f: f.write(text)

    looks = json.load(open(ART, encoding="utf-8")) if os.path.exists(ART) else {}

    # Monsters: keep Act I's hand-made ones, replace generated ones.
    monsters = [m for m in json.load(open(os.path.join(DATA, "monsters.json"), encoding="utf-8")) if not m.get("generated")]
    known = {m["id"] for m in monsters}
    for mid, name, act, arch, extra, look in MONSTERS:
        d = dict(id=mid, name=name) | base_stats(act, arch) | dict(undead=extra.get("undead", False), sprite=f"sprites/mon_{mid}.png", generated=True)
        if extra.get("guard"): d["guard"] = True
        d.update(PROLOGUE_OVERRIDE.get(mid, {}))
        if mid in known: raise SystemExit(f"monster id clash with Act I: {mid}")
        monsters.append(d)
        looks[f"mon_{mid}"] = look | ({"undead": True} if extra.get("undead") else {})
    # Each unique gets its own monster entry so the game can show its name and sprite.
    base_index = {m["id"]: m for m in monsters}
    for uid, name, base, level, hpm, dmgm, escort, count, drop, rarity, look in UNIQUES:
        b = base_index[base]
        d = {k: b[k] for k in ("health", "damage", "speed", "attackRange", "detectRange", "attackCooldown", "radius", "undead")}
        d = dict(id=f"su_{uid}", name=name) | d | dict(sprite=f"sprites/boss_{uid}.png", generated=True)
        monsters.append(d)
        looks[f"boss_{uid}"] = look | {"boss": True}
    write(os.path.join(DATA, "monsters.json"), monsters)

    uniques = [u for u in json.load(open(os.path.join(DATA, "superuniques.json"), encoding="utf-8")) if not u.get("generated")]
    for u in uniques:
        if u["id"] in ACT1_DEATH_CUTSCENES: u["deathCutscene"] = ACT1_DEATH_CUTSCENES[u["id"]]
    for uid, name, base, level, hpm, dmgm, escort, count, drop, rarity, look in UNIQUES:
        uniques.append(dict(id=uid, name=name, base=f"su_{uid}", level=level, healthMul=hpm, damageMul=dmgm, speedMul=1.0,
                            escort=escort, escortCount=count, guaranteedDrop=drop, minRarity=rarity, generated=True))
    write(os.path.join(DATA, "superuniques.json"), uniques)

    bosses = []
    for bid, b in BOSSES.items():
        d = dict(id=bid, title=b["title"], phases=b["phases"])
        if "yieldAt" in b: d["yieldAt"] = b["yieldAt"]
        if "choice" in b: d["choice"] = b["choice"] | {"seconds": 30}
        if "deathCutscene" in b: d["deathCutscene"] = b["deathCutscene"]
        bosses.append(d)
    write(os.path.join(DATA, "bosses.json"), bosses)
    write(os.path.join(DATA, "cutscenes.json"), CUTSCENES)

    # Zones: chain each act's fields from its town; the last zone of an act leads to the next act's town.
    zdir = os.path.join(DATA, "zones")
    towns = {2: "town_in_shadow", 3: "valois_estate", 4: "remnant_camp"}
    acts = {2: ACT2, 3: ACT3, 4: ACT4}
    for act, zones in acts.items():
        for i, z in enumerate(zones):
            prev_id = towns[act] if i == 0 else zones[i - 1]["id"]
            next_id = zones[i + 1]["id"] if i + 1 < len(zones) else None
            write(os.path.join(zdir, z["id"] + ".json"), zone_json(z, prev_id, next_id))
    town_exits = {
        2: [dict(to="sanctuary_of_the_broken_seal", x=120, y=820), dict(to="catacomb_network_1", x=2480, y=820),
            dict(to="valois_estate", x=1200, y=120, requiresUnlock="act3", lockedText="The road north to court opens when Marcus' archive is yours.")],
        3: [dict(to="town_in_shadow", x=120, y=820), dict(to="kings_forest_west", x=2480, y=820),
            dict(to="remnant_camp", x=1200, y=120, requiresUnlock="act4", lockedText="Paris waits until the Inquisitor General falls.")],
        4: [dict(to="valois_estate", x=120, y=820), dict(to="place_de_greve", x=2480, y=820)],
    }
    for act, tid in towns.items():
        write(os.path.join(zdir, tid + ".json"), town_json(tid, TOWNS[tid], town_exits[act]))
        for n in TOWNS[tid]["npcs"]:
            if "_look" in n: looks[f"npc_{n['id']}"] = n["_look"]
    # The prologue courtyard leads into the Sanctuary.
    pz = zone_json(PROLOGUE, None, None)
    pz.update(dict(exits=[dict(to="sanctuary_of_the_broken_seal", x=2440, y=850)], waypoint=None, safeRadius=0, packCount=0,
                   playerSpawn=[300, 850], prologue=True,
                   spawns=[], fixedSpawns=[dict(monster="payns_soldier", x=1000, y=780), dict(monster="payns_soldier", x=1500, y=900),
                                           dict(monster="payns_soldier", x=2000, y=820)]))
    write(os.path.join(zdir, "payns_courtyard.json"), pz)

    # Act I touch-ups: the Sanctuary links to Act II and gets its first cutscene; Thomas's rescue earns his trust.
    sanct_path = os.path.join(zdir, "sanctuary_of_the_broken_seal.json")
    sanct = json.load(open(sanct_path, encoding="utf-8"))
    sanct["exits"] = [e for e in sanct["exits"] if e["to"] != "town_in_shadow"] + [
        dict(to="town_in_shadow", x=1200, y=1600, requiresUnlock="act2", lockedText="The road south opens when Imbert's ledger is yours.")]
    sanct["cutscene"] = "sanctuary_first"
    sanct.setdefault("music", "town_act1")
    write(sanct_path, sanct)

    quests = [q for q in json.load(open(os.path.join(DATA, "quests.json"), encoding="utf-8")) if not q.get("generated")]
    for q in quests:
        if q["id"] == "q4_last_witness": q["reward"]["flags"] = ["thomas_trust"]
    out = []
    for q in QUESTS:
        d = dict(id=q["id"], name=q["name"], zone=q["zone"], act=q["act"], optional=q.get("optional", False),
                 objective=q["objective"], reward=q["reward"], generated=True)
        for k in ("giver", "briefing", "after"):
            if q.get(k): d[k] = q[k]
        out.append(d)
    prologue_q = [q for q in out if q["id"] == "q0_out_of_the_fire"]
    write(os.path.join(DATA, "quests.json"), prologue_q + quests + [q for q in out if q["id"] != "q0_out_of_the_fire"])

    # Placeholder looks for the art tool.
    looks.setdefault("mon_payns_soldier", dict(body="#2C4A86", trim="#D9B23A", head="kettle", weapon="spear", emblem="fleur"))
    if not check:
        with open(ART, "w", encoding="utf-8", newline="\n") as f: json.dump(looks, f, indent=1, ensure_ascii=False)
    print(("Would change" if check else "Wrote") + f" {len(changes)} file(s)")
    for c in changes: print("  " + c)

if __name__ == "__main__":
    main(check="--check" in sys.argv)
