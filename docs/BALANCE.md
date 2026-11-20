# Balance

Everything here is measured with bots in the real simulation, without a server:

```
run_bots.bat --simulate all [group]      each zone on its own, bots at the zone's level
run_bots.bat --campaign [group] out.csv  the whole game in order, keeping levels, skills and mastery
```

## Knobs

| Where | What |
| --- | --- |
| `tools/content/acts.py` `ACT_HP`, `ACT_DMG` | Monster health and damage per act (then run the script) |
| `tools/content/acts.py` `ARCH` | Archetypes: grunt, brute, skirmisher, beast, swarm, archer, caster, elite |
| `src/TacoFactory.Core/Sim/Progression.cs` `Difficulty` | Base multipliers, growth per level, party scaling, party experience |
| `tools/content/level_curve.py` | Fits `assets/data/levels.json` from a campaign CSV |
| `assets/data/bosses.json` | Boss phases and abilities (via acts.py) |

Experience per kill depends on the monster, not the player, so a campaign run measures what each zone gives;
`level_curve.py` then sets the cost of each level so a party ends Act I near 20, Act II near 40, Act III near 70
and Act IV at 100. Players near a kill each get its full experience plus 5% per companion.

## Last results (bots)

| Party | Act I | Act II | Act III | Act IV |
| --- | --- | --- | --- | --- |
| Solo (Knight) | ends level 20, 48 deaths | 39, 172 deaths | 69, 137 deaths | 99, 57 deaths |
| 4 | 20, 25 deaths | 40, 20 | 72, 17 | 99, 52 |
| 6 | 21, 11 deaths | 40, 29 | 75, 16 | 100, 58 |

Every zone was cleared by parties of 4 and 6; the solo Knight failed only the Hermit's Cave and the nave of the
Underground Cathedral within the 40-minute limit, and still finished the game. Bots play the whole campaign in
1-4.5 hours; people will take several times longer. Bots die less than people, so expect players to find it harder.
