# Skills

Every character has 11 skills. Each has a **level from 0 to 10**, bought with build points, and a **mastery from 0 to 100**, earned by using it. Tracking and lock picking are not in the game.

- **Build points:** 1 per character level gained (quests will add more). Each skill level costs 4 points × the class multiplier, rounded up.
- **Starting skills:** each class starts with three skills at level 1 and gear to match.
- **Mastery:** every successful damaging hit with a weapon or spell counts towards that skill's mastery; Shield mastery counts blocks. Level L takes 10 × L hits and adds 0.5% + (L − 1) × 1%/99, so mastery 100 = +100%. Arena scrolls add 1 mastery level each, at most 5 per skill.
- **Untrained weapons** deal 30% less damage. Better gear only auto-equips if you have at least level 1 in its skill. Two-handed weapons take the shield off.

## Weapon and shield skills

| Skill | Weapons | How it fights | Per level |
| --- | --- | --- | --- |
| Heavy Two-Handed | Great mauls, war clubs | Slow, 1.6× damage, hits everything around the target | +6% damage |
| Light Two-Handed | Longswords, greatswords | 1.35× damage, long reach | +5% damage |
| One-Handed Sword | Arming swords, falchions | Balanced, leaves a hand for a shield | +5% damage |
| Dagger | Rondels, misericordes | Very fast, 0.7× damage | +4% damage, +2% critical chance |
| Throwing | Throwing axes, knives, javelins | Ranged (360), 0.85× damage | +5% damage |
| Shield | Heater, kite shields, bucklers | Blocked hits lose 75% of their damage | +2.5% block chance (5% base, cap 40%), +10% shield defense |

## Spell fields

Each level adds 8% spell power. A new spell unlocks at field levels 1, 3, 5, 7 and 9. Known spells fill bar slots 1–7 (keys 1–7; right mouse = slot 1).

### Liturgy

The rites of the Order: healing, blessing, holy fire against the dead.

| Level | Spell | Mana | Cooldown | Effect |
| --- | --- | --- | --- | --- |
| 1 | Mend | 10 | 1.2 s | Heals one ally (or you) for a quarter of their health. |
| 3 | Smite | 6 | 0.9 s | Holy fire at one foe. Far stronger against the undead. |
| 5 | Holy Light | 18 | 6 s | Heals every ally around you. |
| 7 | Sanctuary | 24 | 30 s | Allies around you take 20% less damage. |
| 9 | Resurrection | 40 | 60 s | Raises a fallen ally where they lie, at half health. |

### Alchemy

Fire, frost and lightning drawn from the alchemist's furnace.

| Level | Spell | Mana | Cooldown | Effect |
| --- | --- | --- | --- | --- |
| 1 | Firebolt | 4 | 0.8 s | A bolt of fire at one foe. |
| 3 | Frost Nova | 12 | 4 s | Frost bursts from you, slowing everything it touches. |
| 5 | Greek Fire | 16 | 3 s | A flask of liquid fire bursts on the target and those near it. |
| 7 | Thunderbolt | 14 | 1.4 s | A single blinding strike of lightning. |
| 9 | Inferno | 34 | 8 s | The ground erupts in flame over a wide area. |

### Hermetica

The Hermit's secret texts: arcane force, confusion and the folding of distance.

| Level | Spell | Mana | Cooldown | Effect |
| --- | --- | --- | --- | --- |
| 1 | Arcane Dart | 3 | 0.6 s | A fast dart of raw force. |
| 3 | Confound | 10 | 6 s | Foes around the target stagger, confused and slowed. |
| 5 | Mirror Step | 12 | 6 s | Step instantly to the cursor. |
| 7 | Mana Siphon | 8 | 2 s | Strikes a foe and draws power back into you. |
| 9 | Revelation | 30 | 10 s | Hidden truth tears through everything around you. |

### Warding

Protective seals and consecrations that harden flesh and steel.

| Level | Spell | Mana | Cooldown | Effect |
| --- | --- | --- | --- | --- |
| 1 | Iron Skin | 10 | 20 s | You take 15% less damage. |
| 3 | Consecrated Blade | 12 | 20 s | Your attacks deal 20% more damage. |
| 5 | Aegis | 20 | 30 s | Allies around you take 15% less damage. |
| 7 | Repel | 16 | 8 s | A seal flares and throws foes back. |
| 9 | Bulwark | 36 | 45 s | Allies take 25% less damage and deal 10% more. |

### Grave Lore

Forbidden arts taken from the Order's enemies: withering, leeching, plague.

| Level | Spell | Mana | Cooldown | Effect |
| --- | --- | --- | --- | --- |
| 1 | Wither | 5 | 1 s | Rot at one foe, slowing it. |
| 3 | Leech | 10 | 1.6 s | Strikes a foe and heals you for half the damage. |
| 5 | Plague Cloud | 18 | 5 s | A sickening cloud bursts over the target. |
| 7 | Bone Ward | 16 | 30 s | A cage of bone: 20% less damage taken, 10% more dealt. |
| 9 | Death's Toll | 38 | 12 s | Every foe around you bleeds; you drink a quarter of it. |

## Classes

| Class | Starting skills | Cheaper (×1) | Dearer |
| --- | --- | --- | --- |
| Knight | One-Handed Sword, Shield, Throwing | weapons, Shield | Liturgy ×2, Alchemy ×2, Hermetica ×2, Warding ×1.5, Grave Lore ×2 |
| Paladin | One-Handed Sword, Shield, Liturgy | weapons, Shield, Liturgy, Warding | Alchemy ×2, Hermetica ×2, Grave Lore ×2 |
| Wizard | Alchemy, Hermetica, Dagger | Dagger, Alchemy, Hermetica, Grave Lore | weapons ×1.5, Shield ×2, Liturgy ×2, Warding ×1.5 |
| Healer | Liturgy, Warding, Dagger | Dagger, Liturgy, Warding | weapons ×2, Shield ×1.5, Alchemy ×1.5, Hermetica ×1.5, Grave Lore ×1.5 |

All numbers live in `assets/data/skills.json` and `assets/data/spells.json`; change them there, no rebuild needed.
