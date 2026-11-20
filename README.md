# Taco Factory Engine — Templar Knights

Taco Factory is our own engine for **Templar Knights**, an online co-op action RPG: groups of up to 6 elite Templars (Knight, Paladin, Wizard, Healer) on a server you host. C# on .NET 8, Silk.NET for window, input and OpenGL, LiteNetLib for UDP networking.

## Status

The whole game is playable from the burning courtyard at Payns to the top of the Tower of Truth, with 3D-rendered art (every figure modelled, animated and rendered in Blender), dynamic lighting and effects (`docs/RENDERING.md`), dressed zones and synthesised sound. What changed in each version: `CHANGELOG.md`.

| Phase | State |
| --- | --- |
| 0 Foundation | Done |
| 1 Core simulation | Done |
| 2 Art pipeline | Done. Every figure (classes, monsters, uniques, bosses, NPCs) is modelled and animated in Blender and rendered to 8-direction sprite sheets; ground tiles, item icons, the title and every cutscene are 3D renders too (`tools/art/blender/`). Meshy models can still replace any figure |
| 3 GUI and systems | Done: HUD, panels, economy windows, drag-and-drop, game menu with saved settings, controller (including menus), first-time hints |
| 4 Act I | Done, with the prologue courtyard as the tutorial |
| 5 Acts II–IV | Done: 3 more towns, 28 more zones (multi-floor dungeons), 45 new monsters, 24 named uniques, 6 scripted bosses, party votes on yielded bosses, disguise in the Royal Court, the Remnant's army at Grève, cutscenes, four endings. Levels 1-100 |
| 7 Endgame (0.8) | Done: 14 gear slots, rune sockets and rune growth, traits and research, item sets and Exalted pieces, Exotic and Mythic gear, the forge room (tempering, salvage, empowering, the Runecarver, the Athanor) with its cutscenes, the Grey Mist, the Proving Pit, La Tarasque world boss for 20, relic arts, daily pledges, weekly instabilities, level sync, armoury loadouts, companions |
| 6 Release | Mostly done: accounts and purchase keys, encrypted login, character select, moderation, update server and launcher, release script and installer script, placeholder sound and music. Still to do: final sound, playtests with people |

## Requirements (Windows)

- .NET 8 SDK — https://dotnet.microsoft.com/download/dotnet/8.0
- Python 3 for the banned-terms check, plus `pip install pillow numpy` for the art and sound tools
- OpenGL 3.3 graphics (any GPU from the last ten years)

## Play

```
build.bat                                  check, build, test
run_local.bat                              a server (open sign-up) and the game on this PC: make an account, then a knight
run_server.bat --open                      dedicated server where anyone may sign up (playtests)
run_server.bat --make-keys 10              print 10 purchase keys; without --open, signing up needs one
run_client.bat --host <server address>     the game, at the sign-in screen
release.bat 0.3.0                          players' build + servers + installer script (docs/RELEASE.md)
```

The first account made on a server is its administrator. Hosting, keys, moderation and updates: `docs/HOSTING.md`. The story, choices and endings: `docs/STORY.md`.

Bots and checks (against a server started with `--open`):

```
run_bots.bat                               6 bots: prologue, party up in town, clear the Bloodied Fields
run_bots.bat --duel-test                   arena (server with --start-zone sanctuary_of_the_broken_seal)
run_bots.bat --economy-test                merchants, Forge, stash, trade, auction, guilds (docs/ECONOMY.md)
run_bots.bat --auth-test <saves>           accounts, keys, bans (server without --open, after --make-keys 2)
run_bots.bat --story-test                  a party vote on Réginald (server: --start-zone town_in_shadow --start-level 34)
run_bots.bat --campaign 4 out.csv          the whole game in the simulation, start to finish (docs/BALANCE.md)
```

| Input | Action |
| --- | --- |
| Left mouse on a foe | Weapon attack (hold to keep attacking); on ground, walk there |
| Right mouse or 1 | Spell in slot 1 |
| 2-7 | Spells in slots 2-7 (heals go to the ally under the cursor, or you) |
| K | Skills: spend build points, read mastery scrolls |
| Q | Quest journal |
| V, or click a waypoint | Waypoints (travel while standing on one) |
| T | Return to town (20 s cooldown) |
| Left mouse on an NPC | Walk over and talk |
| W A S D | Move |
| C / I | Character / inventory panel (Gear, Runes and Wallet tabs; right-click an item for its quick action) |
| J | Book of Deeds: set collection, relic arts, companions |
| F | Revive the fallen brother beside you (world bosses only) |
| Z / X | Drink a health / mana potion |
| G | Guild window |
| Enter | Chat. Commands: `/invite name`, `/accept`, `/decline`, `/leave`, `/party`, `/duel name`, `/trade name`, `/guild create name`, `/guild invite name`, `/guild leave`, `/g text` (guild chat) |
| Tab | Large map |
| Drag an item | Bag to an equipment slot (wear), to the stash, trade offer, Forge anvil or merchant (sell); worn or stashed items back to the bag |
| Space / Esc | Next line of a cutscene / skip it |
| Esc | Close the open window, then panels, then the game menu (settings, sound, characters, leave) |
| F11 | Fullscreen |

## Controller

Any controller with a standard layout (Xbox, PlayStation, Switch Pro through Steam or their PC drivers).

| Control | Action |
| --- | --- |
| Left stick | Move (and choose which nearby foe to face) |
| A | Attack the nearest foe in front of you; next to an NPC with no foe near, talk |
| X, Y, B, LB, RB, LT, RT | Spells in slots 1-7 (heals go to the most hurt ally nearby) |
| D-pad up / down | Health / mana potion |
| D-pad left / right | Return to town / bag |
| Start / Back | Game menu / map |

In menus, windows and the sign-in screens the d-pad moves the highlight and A presses; B or Start closes. Typing (names, chat) still needs a keyboard.

## Difficulty and levels

Monster threat lives in `Difficulty` (src/TacoFactory.Core/Sim/Progression.cs) and the per-act multipliers at the top of `tools/content/acts.py`; the level curve in `assets/data/levels.json` is fitted to the whole campaign so a party ends Act I near 20, Act II near 40, Act III near 70 and Act IV at 100. How it was tuned and how to retune: `docs/BALANCE.md`.

## Town economy

Merchants (Eleanor: gear, selling, gambling; Sister Ysabeau: potions), Master Aubry's Forge (two items become one), the Order's Strongbox (60-slot stash), trade windows between players, Jehan the Broker's auction house (a sortable, searchable table), and guilds called Commanderies with a hall behind the Sanctuary's north door and a shared 100-slot strongbox. Rules and numbers in `docs/ECONOMY.md`.

## Classes and skills

Four classes — Knight, Paladin, Wizard, Healer — and 11 skills: Heavy Two-Handed (mauls, clubs), Light Two-Handed (swords), One-Handed Sword, Dagger, Throwing, Shield, and five spell fields: Liturgy, Alchemy, Hermetica, Warding, Grave Lore. Skills go to level 10 with build points; mastery goes to 100 with use. Full tables in `docs/SKILLS.md`.

## Story

Four acts: the Flight from Payns (1-20), the Underground (21-40), Infiltration (41-70) and the Reckoning (71-100), each with its own town, arena and services. Bosses can yield; the party votes to spare or kill, and each player keeps the choice they made. Those choices, Thomas's trust and the proof from the Legate's vault decide which of four endings you see. Details: `docs/STORY.md`.

## Sound

Effects, ambience and music are synthesised by our own scripts (numpy and scipy; ffmpeg for Ogg): `tools/audio/placeholder_audio.py` (music and the first effects) and `tools/audio/sound_pass.py` (ambience loops `amb_*.ogg` for wind, cold wind, rain, fire, crypt, forest, camp, night, town and hall; combat and spell effects; footsteps). Each zone names its ambience in `ambience.sound`; standing near a fire adds its crackle. Replace any file in `assets/sound` (16-bit WAV) or `assets/music` (Ogg Vorbis or WAV) with the real thing under the same name. Volumes are in the game menu.

## Art pipeline

All the art is made by our own Blender scripts in `tools/art/blender/` (Blender 4.2+ or the `bpy` module; a GPU makes it quick):

```
blender -b --python tools/art/blender/figures.py -- --all --loot --gpu      # every figure + loot (about an hour on an RTX 4080)
blender -b --python tools/art/blender/figures.py -- --names mon_grey_wolf --gpu
blender -b --python tools/art/blender/tiles.py   -- --gpu                     # ground tiles, seamless by construction
blender -b --python tools/art/blender/scenes.py  -- --gpu                     # title screen and cutscene illustrations
blender -b --python tools/art/blender/icons.py   -- --gpu                     # item icons
python tools/art/optimize_png.py                                         # lossless shrink (about 60%), before a release
python tools/art/validate_art.py
```

- **Figures** (`figures.py`) are built from each look in `tools/art/placeholder_looks.json`: body and trim colours, head (helms, hoods, mitres, crowns...), dress (plate, mail, robe, tunic, gown, apron), weapon, shield, emblem, cape and flags (boss, big, undead, ghost, stone), or a creature shape (beast, swarm, bats, crow, chest). Each is a jointed model with modelled armour, cloth, weapons and heraldic decals (`emblems/`, drawn by `make_emblems.py`), animated procedurally (idle, walk, attack, hit, death) and rendered with the locked camera and lights to `sprites/<name>_sheet.png` + `<name>.json`, plus a 256 px still `<name>.png` for portraits and menus. Players, bosses and NPCs get 8 directions; minor monsters 4.
- **Tiles** (`tiles.py`) are stones, planks, bones, leaves and puddles laid out on a periodic grid over a periodic-noise ground, so they wrap with no seams, shadows included. 512 px; the client draws a tile at its texture size.
- **Scenes** (`scenes.py`) stage the title and the cutscenes from a procedural set kit (vaults, round church, houses, towers, tents, pyre, bars, shelves, volumetric fire, smoke and fog) with the game's own figures, posed from their animations.
- **Icons** (`icons.py`) are rendered per item kind (sword, dagger, polearm, great weapon, throwing axe, armour, helm, shield, potions).

**Meshy world art.** `tools/art/meshy_world.py` makes floor textures (Text to Image) and scenery props (Text to 3D) for chosen zones under a hard credit cap, resuming from `art_source/world/manifest.json`:

```
python tools/art/meshy_world.py plan                    # list and cost (free)
python tools/art/meshy_world.py run --cap 500           # needs MESHY_API_KEY
python tools/art/world_import.py                        # floors -> seamless 512 px tiles (assets/tiles/meshy_*.png)
blender -b --python tools/art/blender/props.py -- --gpu # props -> sprites + assets/data/props.json
python tools/content/decor.py                           # place props in zones ("decor" in each zone file)
```

**Procedural scenery.** `tools/art/blender/scenery.py` builds trees (oak, pine, dead, burnt), rocks, bushes, stumps, logs, graves, braziers, torch posts, tents, banners, market stalls and grass in Blender and renders them with the prop camera. `tools/art/zone_ambience.py` then gives every zone its light, weather and sound, and scatters scenery by biome (forest, fields, road, frost, quarry, burnt, marsh, yard, ruin, camp, market, garden, cloister, graveyard, halls), keeping exits, NPCs, the waypoint and the paths to them clear (`SceneryTests`). Tall scenery turns see-through while it hides a hero.

```
python tools/art/blender/scenery.py --samples 48   # scenery sprites + props.json
python tools/art/zone_ambience.py                   # atmosphere and scenery in every zone
```

Zones list their scenery under `"decor"` (`prop`, `x`, `y`, `flip`); the client draws it depth-sorted with the figures. Each prop has a solid ground `footprint` (computed from its model by `props.py`), and the server keeps players and monsters out of it: they slide along its edge and steer round it (`World.Obstacles.cs`).

To add a monster, give it a look in `placeholder_looks.json` and render it by name. A Meshy model can still replace any figure: `tools/art/meshy_pipeline.py`, `blender/templar_render_rig.py` and `tools/art/pack_sheet.py` produce the same sheet format. `tools/art/placeholders.py` only fills gaps for content that has no render yet.

## Layout

| Path | What it is |
| --- | --- |
| `src/TacoFactory.Core` | Shared simulation, game data, mastery, saves, bot brain |
| `src/TacoFactory.Net` | Wire protocol and client connection |
| `src/TacoFactory.Server` | Dedicated server (see `docs/HOSTING.md`) |
| `src/TacoFactory.Client` | The game |
| `src/TacoFactory.Bots` | Network playtest bots, network tests, `--simulate` and `--campaign` balance runs |
| `src/TacoFactory.Launcher` | Players' launcher: updates the game from your server, then starts it |
| `assets/data` | Monsters, uniques, bosses, zones, towns, quests, cutscenes, level curve (JSON) |
| `assets/sprites`, `assets/tiles`, `assets/ui` | Art: rendered sprite sheets and stills, ground tiles, title, cutscenes, item icons |
| `assets/sound`, `assets/music`, `assets/fonts` | Sound (placeholders) and OFL fonts |
| `tools/` | Banned-terms check, content generator for Acts II-IV, level-curve fitter, Blender art generators (figures, tiles, scenes, icons), sound placeholder maker, Meshy pipeline, release script |
| `installer/` | Inno Setup script for the players' installer |
| `docs/` | Clean-room rules and log, hosting, story, balance, economy, skills, release |

Fonts: Cinzel and EB Garamond, SIL Open Font License (licence files beside them in `assets/fonts`).
