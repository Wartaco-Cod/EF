using TacoFactory.Core.Data;
using TacoFactory.Core.Math;

namespace TacoFactory.Core.Sim;

/// <summary>
/// One running instance of a zone for one group of up to 6 players. The server owns it; clients only see snapshots.
/// Everything that decides an outcome (hits, damage, loot, XP, mastery) happens here.
/// </summary>
public sealed partial class World
{
    public const int MaxGroupSize = 6;
    public const float PickupRadius = 56;
    public const float RespawnSeconds = 5;
    public const float XpShareRange = 1400;

    private readonly GameData _data;
    private readonly Random _rng;
    private readonly Dictionary<int, Entity> _entities = new();
    private readonly List<Entity> _scratch = [];
    private int _nextId = 1;

    public ZoneDef Zone { get; }
    public List<SimEvent> Events { get; } = [];
    public double Time { get; private set; }
    public long TickCount { get; private set; }
    public bool Cleared { get; private set; }
    public double? ClearedAt { get; private set; }
    public int GroupSizeAtSpawn { get; private set; }
    public bool Populated { get; private set; }

    public IReadOnlyCollection<Entity> Entities => _entities.Values;
    public IEnumerable<Entity> Players => _entities.Values.Where(e => e.Kind == EntityKind.Player);
    public IEnumerable<Entity> Monsters => _entities.Values.Where(e => e.Kind == EntityKind.Monster);
    public int MonstersAlive => _entities.Values.Count(e => e.Kind == EntityKind.Monster && !e.Dead && (e.Flags & EntityFlags.Ally) == 0);

    public World(GameData data, string zoneId, int seed)
    {
        _data = data;
        _rng = new Random(seed);
        Zone = data.Zones[zoneId];
    }

    public Entity? Get(int id) => _entities.GetValueOrDefault(id);

    // ------------------------------------------------------------------ players

    public GameData Data => _data;

    public Entity AddPlayer(string name, ClassId cls, int level = 1, Vec2? at = null)
    {
        if (Players.Count() >= Zone.PlayerLimit) throw new InvalidOperationException($"This place is full ({Zone.PlayerLimit} players).");
        var def = Classes.Get(cls);
        var p = new Entity
        {
            Id = _nextId++, Kind = EntityKind.Player, Name = name, Class = cls, Level = level, Radius = 22,
            Pos = at is Vec2 a ? ClampToZone(a, 22) : SpawnPoint(), BuildPoints = System.Math.Max(0, level - 1) * _data.SkillRules.BuildPointsPerLevelUp,
        };
        p.MaxHp = Progression.MaxHealth(def, level);
        p.Hp = p.MaxHp;
        p.MaxMana = Progression.MaxMana(def, level);
        p.Mana = p.MaxMana;

        // Class starting skills at level 1, and starter gear to match them.
        if (_data.ClassSkills.TryGetValue(cls.ToString(), out var info))
            foreach (var sk in info.Starting) p.Skills[sk] = _data.SkillRules.StartingSkillLevel;
        var starter = info?.Starting.Select(id => _data.Skills[id]).FirstOrDefault(s => s.Kind == SkillKind.Weapon);
        var w = WeaponType.None;
        if (starter?.WeaponType is string wt && Enum.TryParse(wt, out w))
            p.Equipped[ItemSlot.Weapon] = Loot.MakeItem(_rng, Loot.NewId(), 1, Rarity.Common, weapon: w);
        if (p.SkillLevel("shield") > 0 && !Weapons.Of(w).TwoHanded)
            p.Equipped[ItemSlot.Shield] = Loot.MakeItem(_rng, Loot.NewId(), 1, Rarity.Common, ItemSlot.Shield);

        _entities[p.Id] = p;
        RefreshPlayer(p);
        return p;
    }

    /// <summary>Recomputes what depends on skills (the spell bar). Call after loading a save onto a player.</summary>
    public void RefreshPlayer(Entity p)
    {
        RecalcStats(p);
        p.SpellBar.Clear();
        foreach (var sp in _data.KnownSpells(p.Skills, p.Extras.Relics).Take(Entity.BarSlots - 1)) p.SpellBar.Add(sp.Id);
    }

    public enum LearnResult { Learned, UnknownSkill, AtMaximum, NotEnoughPoints }

    /// <summary>Spends build points to raise a skill by one level (max 10). New spells join the bar at once.</summary>
    public LearnResult LearnSkill(Entity p, string skillId)
    {
        if (!_data.Skills.TryGetValue(skillId, out var skill)) return LearnResult.UnknownSkill;
        int level = p.SkillLevel(skill.Id);
        if (level >= _data.SkillRules.MaxSkillLevel) return LearnResult.AtMaximum;
        int cost = _data.LearnCost(p.Class.ToString(), skill.Id);
        if (p.BuildPoints < cost) return LearnResult.NotEnoughPoints;
        p.BuildPoints -= cost;
        p.Skills[skill.Id] = level + 1;
        RefreshPlayer(p);
        Events.Add(new SimEvent(SimEventType.SkillLearned, p.Id, p.Id, level + 1, skill.Id));
        return LearnResult.Learned;
    }

    public void RemovePlayer(int id)
    {
        if (!_entities.TryGetValue(id, out var e) || e.Kind != EntityKind.Player) return;
        ForfeitDuel(e);
        _entities.Remove(id);
    }

    private Vec2 ClampToZone(Vec2 p, float r) =>
        new(System.Math.Clamp(p.X, r, Zone.Width - r), System.Math.Clamp(p.Y, r, Zone.Height - r));

    public void SetInput(int playerId, PlayerInput input)
    {
        if (_entities.TryGetValue(playerId, out var p) && p.Kind == EntityKind.Player) p.Input = input;
    }

    private Vec2 SpawnPoint() =>
        ClampAndPush(new Vec2(Zone.PlayerSpawn[0], Zone.PlayerSpawn[1]) + new Vec2(_rng.Next(-60, 61), _rng.Next(-60, 61)), 22);

    public WeaponType WeaponOf(Entity p) => p.Equipped.TryGetValue(ItemSlot.Weapon, out var w) ? w.Weapon : WeaponType.None;

    /// <summary>Damage multiplier from the skill that trains this weapon: +5-6% per level, −30% if untrained.</summary>
    public double WeaponSkillMultiplier(Entity p, out SkillDef? skill)
    {
        skill = _data.SkillForWeapon(WeaponOf(p));
        if (skill is null) return 1;
        int lvl = p.SkillLevel(skill.Id);
        return lvl == 0 ? 1 - _data.SkillRules.UntrainedDamagePenalty : 1 + skill.Per("damage") * lvl;
    }

    public double BlockChance(Entity p)
    {
        if (!p.Equipped.ContainsKey(ItemSlot.Shield) || Weapons.Of(WeaponOf(p)).TwoHanded) return 0;
        var shield = _data.Skills["shield"];
        double chance = 0.05 + shield.Per("blockChance") * p.SkillLevel("shield") + PropertyTotal(p, "block") / 100.0;
        chance *= p.MasteryFor("shield").DamageMultiplier;
        return System.Math.Min(_data.SkillRules.MaxBlockChance, chance);
    }

    public int Defense(Entity p)
    {
        double def = 0;
        foreach (var (slot, item) in p.Equipped)
        {
            if (slot == ItemSlot.Shield)
            {
                if (Weapons.Of(WeaponOf(p)).TwoHanded) continue;
                def += item.Defense * (1 + _data.Skills["shield"].Per("shieldDefense") * p.SkillLevel("shield"));
            }
            else def += item.Defense;
        }
        return (int)(def * p.GearScale * (1 + PropertyTotal(p, "armor") / 100.0));
    }

    // ------------------------------------------------------------------ monsters

    /// <summary>Spawns the zone's packs. Health and damage scale with the group size at this moment (see <see cref="Difficulty"/>).</summary>
    public void Populate(int? groupSize = null, int allyBonus = 0)
    {
        if (Populated) return;
        Populated = true;
        if (Zone.IsTown)
        {
            for (int i = 0; i < Zone.Npcs.Count; i++)
            {
                var n = Zone.Npcs[i];
                _entities[_nextId] = new Entity
                {
                    Id = _nextId++, Kind = EntityKind.Npc, TypeIndex = i, DisplayName = n.Name, Pos = new Vec2(n.X, n.Y),
                    Radius = 22, Hp = 1, MaxHp = 1, Flags = EntityFlags.Invulnerable,
                };
            }
            return;
        }
        if (Zone.ProvingPit) { GroupSizeAtSpawn = System.Math.Clamp(groupSize ?? Players.Count(), 1, MaxGroupSize); StartPitFloor(1); return; }
        GroupSizeAtSpawn = System.Math.Clamp(groupSize ?? Players.Count(), 1, Zone.WorldBoss ? Zone.PlayerLimit : MaxGroupSize);
        float groupHp = 1f + Difficulty.GroupHealthPerPlayer * (GroupSizeAtSpawn - 1);
        // A world boss's blows are already shared between everyone (World.Endgame SharedBlow), so they don't grow here too.
        _groupDamage = Zone.WorldBoss ? 1f : 1f + Difficulty.GroupDamagePerPlayer * (GroupSizeAtSpawn - 1);
        var spawn = new Vec2(Zone.PlayerSpawn[0], Zone.PlayerSpawn[1]);
        int totalWeight = Zone.Spawns.Sum(s => s.Weight);

        for (int pack = 0; pack < Zone.PackCount; pack++)
        {
            int roll = _rng.Next(totalWeight);
            var entry = Zone.Spawns.First(s => (roll -= s.Weight) < 0);
            Vec2 center;
            int guard = 0;
            do
            {
                center = new Vec2(_rng.NextSingle() * (Zone.Width - 400) + 200, _rng.NextSingle() * (Zone.Height - 400) + 200);
            } while (Vec2.Distance(center, spawn) < Zone.SafeRadius && ++guard < 50);

            // Packs further from the entrance are higher level.
            float t = System.Math.Clamp(Vec2.Distance(center, spawn) / new Vec2(Zone.Width, Zone.Height).Length, 0, 1);
            int level = System.Math.Clamp(Zone.LevelMin + (int)System.Math.Round(t * (Zone.LevelMax - Zone.LevelMin)), Zone.LevelMin, Zone.LevelMax);
            int count = _rng.Next(entry.GroupMin, entry.GroupMax + 1);
            for (int i = 0; i < count; i++)
                SpawnMonster(_data.Monsters[entry.Monster], level, center + new Vec2(_rng.Next(-90, 91), _rng.Next(-90, 91)), groupHp);
        }

        if (Zone.Superunique is string suId && Zone.SuperuniquePosition is { Length: 2 } sp)
        {
            var su = _data.Superuniques[suId];
            var at = new Vec2(sp[0], sp[1]);
            var boss = SpawnMonster(_data.Monsters[su.Base], su.Level, at, groupHp * su.HealthMul, su.DamageMul, su.SpeedMul);
            boss.DisplayName = su.Name;
            boss.UniqueId = su.Id;
            boss.Flags |= EntityFlags.Superunique;
            boss.Radius *= 1.3f;
            var minRarity = Enum.Parse<Rarity>(su.MinRarity, true);
            var slot = Enum.TryParse<ItemSlot>(su.GuaranteedDrop, true, out var s) ? s : (ItemSlot?)null;
            boss.GuaranteedDrop = Loot.MakeItem(_rng, Loot.NewId(), su.Level, Loot.RollRarity(_rng, RarityCap(), minRarity), slot);
            for (int i = 0; i < su.EscortCount; i++)
                SpawnMonster(_data.Monsters[su.Escort], su.Level, at + new Vec2(_rng.Next(-110, 111), _rng.Next(-110, 111)), groupHp);
        }
        if (Zone.Boss is string bossId && Zone.BossPosition is { Length: 2 } bp)
        {
            if (_data.Bosses.TryGetValue(bossId, out var scripted)) SpawnScriptedBoss(scripted, new Vec2(bp[0], bp[1]), groupHp);
            else SpawnBoss(bossId, new Vec2(bp[0], bp[1]), groupHp);
        }
        SpawnAllies(Zone.Allies + allyBonus);
        foreach (var f in Zone.FixedSpawns) SpawnMonster(_data.Monsters[f.Monster], Zone.LevelMin, new Vec2(f.X, f.Y), groupHp);
        if (MistActive)
        {
            foreach (var m in Monsters.Where(m => (m.Flags & EntityFlags.Ally) == 0)) Mistify(m);
            PlaceCoffers();
        }
    }

    private float _groupDamage = 1f;

    private Entity SpawnMonster(MonsterDef def, int level, Vec2 pos, float hpMul, float dmgMul = 1, float speedMul = 1)
    {
        pos = ClampAndPush(pos, System.Math.Max(40, def.Radius));
        int hp = (int)(def.Health * Difficulty.HealthBase * (1 + Difficulty.HealthPerLevel * (level - 1)) * hpMul);
        var m = new Entity
        {
            Id = _nextId++, Kind = EntityKind.Monster, Def = def, TypeIndex = _data.IndexOfMonster(def.Id),
            DisplayName = def.Name, Level = level, Pos = pos, Home = pos, Radius = def.Radius,
            MaxHp = System.Math.Max(1, hp), Hp = System.Math.Max(1, hp),
            Damage = System.Math.Max(1, (int)System.Math.Round(def.Damage * Difficulty.DamageBase * (1 + Difficulty.DamagePerLevel * (level - 1)) * dmgMul * _groupDamage)),
            Speed = def.Speed * speedMul,
            Flags = def.Undead ? EntityFlags.Undead : EntityFlags.None,
            AttackTimer = (float)_rng.NextDouble() * def.AttackCooldown,
        };
        _entities[m.Id] = m;
        return m;
    }

    private Rarity RarityCap() => Enum.TryParse<Rarity>(Zone.RarityCap, true, out var r) ? r : Rarity.Legendary;

    // ------------------------------------------------------------------ tick

    public void Tick(float dt)
    {
        Events.Clear();
        Events.AddRange(_deferred);
        _deferred.Clear();
        Time += dt;
        TickCount++;
        if (!Populated) Populate();

        _scratch.Clear();
        _scratch.AddRange(_entities.Values);
        foreach (var e in _scratch)
        {
            switch (e.Kind)
            {
                case EntityKind.Player: TickPlayer(e, dt); break;
                case EntityKind.Monster: TickMonster(e, dt); break;
                case EntityKind.Loot: TickLoot(e); break;
            }
        }
        SeparateMonsters();
        TickDuels(dt);
        TickBoss(dt);
        TickScript(dt);
        TickPit(dt);
        TickInstabilities(dt);

        if (!Cleared && !Zone.IsTown && !Zone.ProvingPit && MonstersAlive == 0 && Populated)
        {
            Cleared = true;
            ClearedAt = Time;
            Events.Add(new SimEvent(SimEventType.ZoneCleared, 0, 0, (int)Time, Zone.Name));
        }
    }

    private void TickPlayer(Entity p, float dt)
    {
        var cls = Classes.Get(p.Class);
        if (p.Dead)
        {
            p.RespawnTimer -= dt;
            if (p.RespawnTimer <= 0) Revive(p, p.MaxHp, SpawnPoint(), null);
            return;
        }

        p.Mana = System.Math.Min(p.MaxMana, p.Mana + p.MaxMana * 0.02f * dt);
        p.PotionCooldown = System.Math.Max(0, p.PotionCooldown - dt);
        for (int i = 0; i < p.SkillCooldowns.Length; i++) p.SkillCooldowns[i] = System.Math.Max(0, p.SkillCooldowns[i] - dt);
        for (int i = p.Buffs.Count - 1; i >= 0; i--)
            if ((p.Buffs[i].Remaining -= dt) <= 0) p.Buffs.RemoveAt(i);

        var input = p.Input;
        if (input.Move.LengthSquared > 0.0001f)
        {
            MoveClamped(p, input.Move.Normalized() * cls.Speed * dt);
        }
        else if (input.Action == PlayerAction.UseSkill)
        {
            if (input.SkillSlot == 0) WeaponAttack(p, cls, input.TargetId, dt);
            else if (input.SkillSlot - 1 < p.SpellBar.Count && _data.Spells.TryGetValue(p.SpellBar[input.SkillSlot - 1], out var spell))
                CastSpell(p, cls, spell, input.SkillSlot, input.TargetId, input.TargetPos, dt);
        }
    }

    private void Revive(Entity p, int hp, Vec2 at, Entity? by)
    {
        p.Flags &= ~EntityFlags.Dead;
        p.Hp = System.Math.Max(1, hp);
        if (by is null) p.Mana = p.MaxMana;
        p.Pos = ClampAndPush(at, p.Radius);
        p.RespawnTimer = 0;
        Events.Add(by is null ? new SimEvent(SimEventType.Respawn, p.Id, p.Id, 0) : new SimEvent(SimEventType.Revived, by.Id, p.Id, p.Hp));
    }

    /// <summary>True when in reach; otherwise steps towards the target and returns false.</summary>
    private bool Approach(Entity p, ClassDef cls, Entity target, float range, float dt)
    {
        float reach = range + target.Radius + p.Radius;
        if (Vec2.Distance(p.Pos, target.Pos) <= reach) return true;
        MoveClamped(p, (target.Pos - p.Pos).Normalized() * cls.Speed * dt);
        return false;
    }

    private Entity? LiveMonster(int id) =>
        Get(id) is { Kind: EntityKind.Monster, Dead: false } m && (m.Flags & EntityFlags.Ally) == 0 ? m : null;

    /// <summary>Something this player may damage: a hostile monster, or their duel opponent once the duel has begun.</summary>
    private Entity? LiveTarget(Entity attacker, int id) => LiveMonster(id) ?? DuelOpponent(attacker, id);

    /// <summary>Hostile monsters in the area, plus the attacker's duel opponent if they stand in it.</summary>
    private List<Entity> TargetsNear(Entity attacker, Vec2 at, float radius)
    {
        var list = MonstersNear(at, radius).ToList();
        if (ActiveOpponent(attacker) is { } o && Vec2.Distance(o.Pos, at) <= radius + o.Radius) list.Add(o);
        return list;
    }

    private int Damage(Entity p, Entity target, double raw, double crit, string? skill, float undeadBonus, SpellDef? spell = null) =>
        target.Kind == EntityKind.Player ? DuelHit(p, target, raw, crit, skill, spell) : Hit(p, target, raw, crit, skill, undeadBonus, spell);

    private void WeaponAttack(Entity p, ClassDef cls, int targetId, float dt)
    {
        if (LiveTarget(p, targetId) is not { } target) return;
        var weapon = WeaponOf(p);
        var ws = Weapons.Of(weapon);
        if (!Approach(p, cls, target, ws.Range, dt) || p.SkillCooldowns[0] > 0) return;
        p.SkillCooldowns[0] = ws.Cooldown * (1 - System.Math.Min(0.4f, 0.02f * PropertyTotal(p, "dexterity")));
        double elements = Loot.ElementProperties.Sum(k => PropertyTotal(p, k)) / 100.0;
        double mult = WeaponSkillMultiplier(p, out var skill) * ws.DamageMul * (1 + elements);
        double crit = 0.05 + (skill?.Per("critChance") ?? 0) * (skill is null ? 0 : p.SkillLevel(skill.Id));
        float baseDmg = Progression.Damage(cls, p.EffectiveLevel) + p.GearDamage;
        Events.Add(new SimEvent(SimEventType.SkillUsed, p.Id, target.Id, 0, skill?.Id ?? "fists"));
        if (ws.AreaRadius > 0)
            foreach (var m in TargetsNear(p, target.Pos, ws.AreaRadius)) Damage(p, m, baseDmg * mult, crit, skill?.Id, 0);
        else Damage(p, target, baseDmg * mult, crit, skill?.Id, 0);
    }

    private double SpellPower(Entity p, SpellDef spell) =>
        _data.Skills.TryGetValue(spell.Field, out var field)
            ? 1 + field.Per("spellPower") * p.SkillLevel(spell.Field)
            : 1 + 0.01 * p.EffectiveLevel + (Weapons.Of(WeaponOf(p)).DamageMul - 1) * 0.5; // relic arts grow with the knight and the weapon

    private void CastSpell(Entity p, ClassDef cls, SpellDef spell, int slot, int targetId, Vec2 targetPos, float dt)
    {
        Entity? target = null;
        if (spell.NeedsMonsterTarget)
        {
            target = LiveTarget(p, targetId);
            if (target is null || !Approach(p, cls, target, spell.Range, dt)) return;
        }
        else if (spell.Kind == SpellKind.Heal)
        {
            target = Get(targetId) is { Kind: EntityKind.Player, Dead: false } ally ? ally : p;
            if (target != p && !Approach(p, cls, target, spell.Range, dt)) return;
        }
        else if (spell.Kind == SpellKind.Revive)
        {
            if (Get(targetId) is not { Kind: EntityKind.Player, Dead: true } fallen) return;
            target = fallen;
            if (!Approach(p, cls, target, spell.Range, dt)) return;
        }

        if (p.SkillCooldowns[slot] > 0 || p.Mana < spell.ManaCost) return;
        p.SkillCooldowns[slot] = spell.Cooldown;
        p.Mana -= spell.ManaCost;
        Events.Add(new SimEvent(SimEventType.SkillUsed, p.Id, target?.Id ?? 0, slot, spell.Id));

        double power = SpellPower(p, spell);
        float baseDmg = Progression.Damage(cls, p.EffectiveLevel) * spell.Power;
        switch (spell.Kind)
        {
            case SpellKind.Bolt:
                Damage(p, target!, baseDmg * power, 0.05, spell.Field, spell.BonusVsUndead, spell);
                break;
            case SpellKind.Drain:
            {
                int dealt = Damage(p, target!, baseDmg * power, 0.05, spell.Field, spell.BonusVsUndead, spell);
                if (spell.HealReturn > 0) Heal(p, p, (int)(dealt * spell.HealReturn));
                if (spell.ManaReturn > 0) p.Mana = System.Math.Min(p.MaxMana, p.Mana + dealt * spell.ManaReturn);
                break;
            }
            case SpellKind.Burst:
                foreach (var m in TargetsNear(p, target!.Pos, spell.Radius)) Damage(p, m, baseDmg * power, 0.05, spell.Field, spell.BonusVsUndead, spell);
                break;
            case SpellKind.Nova:
            {
                int total = 0;
                foreach (var m in TargetsNear(p, p.Pos, spell.Radius))
                {
                    total += Damage(p, m, baseDmg * power, 0.05, spell.Field, spell.BonusVsUndead, spell);
                    if (spell.Knockback > 0 && !m.Dead)
                    {
                        var away = (m.Pos - p.Pos).Normalized();
                        MoveClamped(m, (away.LengthSquared > 0 ? away : new Vec2(1, 0)) * spell.Knockback);
                    }
                }
                if (spell.HealReturn > 0) Heal(p, p, (int)(total * spell.HealReturn));
                break;
            }
            case SpellKind.Heal:
                Heal(p, target!, (int)(target!.MaxHp * spell.Power * power + p.Level * 2));
                break;
            case SpellKind.HealArea:
                foreach (var ally in AlliesNear(p.Pos, spell.Radius)) Heal(p, ally, (int)(ally.MaxHp * spell.Power * power + p.Level * 2));
                break;
            case SpellKind.BuffSelf:
                ApplyBuff(p, p, spell, power);
                break;
            case SpellKind.BuffArea:
                foreach (var ally in AlliesNear(p.Pos, spell.Radius)) ApplyBuff(p, ally, spell, power);
                break;
            case SpellKind.Blink:
            {
                var to = targetPos - p.Pos;
                if (to.Length > spell.Range) to = to.Normalized() * spell.Range;
                MoveClamped(p, to);
                break;
            }
            case SpellKind.Revive:
                Revive(target!, (int)(target!.MaxHp * spell.Power), target.Pos, p);
                break;
        }
    }

    private List<Entity> AlliesNear(Vec2 at, float radius) =>
        _entities.Values.Where(e => e.Kind == EntityKind.Player && !e.Dead && Vec2.Distance(e.Pos, at) <= radius).ToList();

    /// <summary>Buff strength scales a little with spell power; recasting refreshes the same buff.</summary>
    private void ApplyBuff(Entity caster, Entity target, SpellDef spell, double power)
    {
        target.Buffs.RemoveAll(b => b.SpellId == spell.Id);
        float scale = (float)System.Math.Min(1.5, 0.85 + 0.15 * power);
        target.Buffs.Add(new Buff { SpellId = spell.Id, Damage = spell.Damage * scale, Defense = spell.Defense * scale, Remaining = spell.Duration });
        Events.Add(new SimEvent(SimEventType.Buffed, caster.Id, target.Id, (int)spell.Duration, spell.Id));
    }

    private IEnumerable<Entity> MonstersNear(Vec2 at, float radius) =>
        _entities.Values.Where(e => e.Kind == EntityKind.Monster && !e.Dead && (e.Flags & EntityFlags.Ally) == 0
                                    && Vec2.Distance(e.Pos, at) <= radius + e.Radius).ToList();

    /// <summary>
    /// One successful damaging hit on one monster. Returns the damage dealt.
    /// The hit counts towards the mastery of the skill that made it (weapon skill or spell field).
    /// </summary>
    private int Hit(Entity p, Entity m, double rawDamage, double critChance, string? masterySkill, float bonusVsUndead, SpellDef? spell = null)
    {
        if (m.Dead) return 0;
        if ((m.Flags & EntityFlags.Invulnerable) != 0)
        {
            Events.Add(new SimEvent(SimEventType.Damage, p.Id, m.Id, 0, "immune"));
            return 0;
        }
        double mult = System.Math.Max(0.1, 1 + p.BuffDamage) * BossDamageTakenMultiplier(m);
        if (masterySkill is not null) mult *= p.MasteryFor(masterySkill).DamageMultiplier;
        if ((m.Flags & EntityFlags.Undead) != 0) mult *= 1 + bonusVsUndead + PropertyTotal(p, "undead") / 100.0;
        bool crit = _rng.NextDouble() < critChance + PropertyTotal(p, "crit") / 100.0;
        if (crit) mult *= 1.5;
        int dmg = System.Math.Max(1, (int)System.Math.Round(rawDamage * mult * (0.9 + _rng.NextDouble() * 0.2)));
        m.Hp -= dmg;
        if ((p.Flags & EntityFlags.Disguised) != 0) BreakDisguise(p, "You strike, and the court sees a Templar. The guards are coming!");
        Events.Add(new SimEvent(SimEventType.Damage, p.Id, m.Id, dmg, crit ? "crit" : ""));
        // Runes on the weapon learn from every blow they strike; a leech rune from every drop it drinks.
        RuneXp(p, 1, s => s == ItemSlot.Weapon);
        int leech = PropertyTotal(p, "leech");
        if (leech > 0 && p.Hp < p.MaxHp) Heal(p, p, System.Math.Max(1, dmg * leech / 100));

        if (masterySkill is not null && p.MasteryFor(masterySkill).AddHit())
            Events.Add(new SimEvent(SimEventType.MasteryUp, p.Id, p.Id, p.MasteryFor(masterySkill).Level, masterySkill));

        if (spell is not null && spell.Slow > 0 && m.Hp > 0) { m.Flags |= EntityFlags.Slowed; m.SlowTimer = spell.Slow; }
        if (m.State == MonsterState.Idle) { m.State = MonsterState.Chase; m.TargetId = p.Id; }
        if (m.Hp <= 0 && !TryYield(m)) KillMonster(m, p);
        return dmg;
    }

    private void Heal(Entity healer, Entity target, int amount)
    {
        int before = target.Hp;
        target.Hp = System.Math.Min(target.MaxHp, target.Hp + amount);
        if (target.Hp > before) Events.Add(new SimEvent(SimEventType.Heal, healer.Id, target.Id, target.Hp - before));
    }

    private void KillMonster(Entity m, Entity killer)
    {
        m.Hp = 0;
        m.Flags |= EntityFlags.Dead;
        killer.Kills++;
        Events.Add(new SimEvent(SimEventType.Death, killer.Id, m.Id, 0, m.DisplayName));
        if (m.UniqueId is string uid) Events.Add(new SimEvent(SimEventType.UniqueKilled, killer.Id, m.Id, 0, uid));
        foreach (var extra in m.ExtraDrops) DropItem(m.Pos, extra);

        // XP scales with how tough the monster is (a rat is worth far less than a torturer) and its level.
        double toughness = System.Math.Clamp((m.Def?.Health ?? 40) / 40.0, 0.25, 4.0);
        long xp = (long)(Progression.XpPerEnemyBase * Progression.XpScale * toughness * m.Level * ((m.Flags & EntityFlags.Superunique) != 0 ? 5 : 1));

        if ((m.Flags & EntityFlags.Ally) != 0) xp = 0;
        if (Unstable("bloodthirst")) xp = xp * 5 / 4;
        var sharers = Players.Where(p => !p.Dead && Vec2.Distance(p.Pos, m.Pos) <= XpShareRange).ToList();
        if (sharers.Count > 0)
        {
            // Everyone near gets the kill's full experience, plus a little for each companion: monsters are tougher
            // in a party, so this keeps a party levelling at the solo pace or slightly faster.
            long share = (long)System.Math.Ceiling(xp * (1 + Difficulty.GroupXpPerPlayer * (sharers.Count - 1)));
            foreach (var p in sharers)
            {
                // Synced knights are rewarded nearer their own level, so old zones stay worth the walk.
                double sync = p.SyncLevel > 0 && m.Level > 0 ? System.Math.Max(1, p.Level * 0.6 / m.Level) : 1;
                GrantXp(p, (long)(share * sync));
            }
        }
        if ((m.Flags & EntityFlags.Ally) == 0)
        {
            OnMonsterKilled(m, killer, sharers);
            bool eliteKill = (m.Flags & EntityFlags.Superunique) != 0;
            if (killer.Kind == EntityKind.Player && killer.Extras.Runes.Count < Runes.PouchLimit && _rng.NextDouble() < (eliteKill ? 0.25 : 0.02))
            {
                // Every rune is found at Common tier, level 1.
                var rune = Runes.Make(_rng);
                killer.Extras.Runes.Add(rune);
                Events.Add(new SimEvent(SimEventType.RuneFound, killer.Id, m.Id, 0, Runes.Name(rune)));
            }
        }

        if (m.GuaranteedDrop is Item g) DropItem(m.Pos, g);
        else if (_rng.NextDouble() < Loot.DropChance)
        {
            var cap = MistActive && RarityCap() < Rarity.Legendary ? RarityCap() + 1 : RarityCap();
            DropItem(m.Pos, ItemSets.MaybeSet(_rng, Loot.MakeItem(_rng, Loot.NewId(), m.Level, Loot.RollRarity(_rng, cap)), 0.08));
        }
        if (_rng.NextDouble() < 0.5)
            DropGold(m.Pos, _rng.Next(Zone.GoldMin, Zone.GoldMax + 1));
    }

    private void GrantXp(Entity p, long xp)
    {
        p.LifetimeXp += xp;
        if (p.Level >= Progression.MaxLevel) return;
        p.Xp += xp;
        while (p.Level < Progression.MaxLevel && p.Xp >= Progression.XpToNext(p.Level))
        {
            p.Xp -= Progression.XpToNext(p.Level);
            p.Level++;
            p.BuildPoints += _data.SkillRules.BuildPointsPerLevelUp;
            RecalcStats(p);
            p.Hp = p.MaxHp;
            p.Mana = p.MaxMana;
            Events.Add(new SimEvent(SimEventType.LevelUp, p.Id, p.Id, p.Level));
        }
    }

    private void DropItem(Vec2 at, Item item)
    {
        var e = new Entity { Id = _nextId++, Kind = EntityKind.Loot, Item = item, Pos = Scatter(at), Radius = 12, TypeIndex = (int)item.Rarity };
        _entities[e.Id] = e;
    }

    private void DropGold(Vec2 at, int amount)
    {
        var e = new Entity { Id = _nextId++, Kind = EntityKind.Loot, GoldAmount = amount, Pos = Scatter(at), Radius = 10, TypeIndex = 255 };
        _entities[e.Id] = e;
    }

    private Vec2 Scatter(Vec2 at) => ClampAndPush(at + new Vec2(_rng.Next(-30, 31), _rng.Next(-30, 31)), 14);

    private void TickLoot(Entity loot)
    {
        foreach (var p in Players)
        {
            if (p.Dead || Vec2.Distance(p.Pos, loot.Pos) > PickupRadius) continue;
            if (loot.Item is Item item)
            {
                // Empty slot and usable: wear it. Otherwise into the bag, if there's room.
                if (!p.Equipped.ContainsKey(item.Slot) && item.Rarity < Rarity.Exotic && ShouldEquip(p, item, out _))
                {
                    p.Equipped[item.Slot] = item;
                    RecalcStats(p);
                }
                else if (p.Inventory.Count < Gear.BagLimit(p)) p.Inventory.Add(item);
                else continue; // bag full: leave it on the ground for someone else
                Events.Add(new SimEvent(SimEventType.LootPicked, p.Id, loot.Id, (int)item.Rarity, $"{item.Rarity} {item.DisplayName}"));
                p.Extras.Note(item);
            }
            else
            {
                int amount = loot.GoldAmount * (100 + PropertyTotal(p, "gold")) / 100;
                p.Gold += amount;
                Events.Add(new SimEvent(SimEventType.LootPicked, p.Id, loot.Id, amount, "gold"));
            }
            _entities.Remove(loot.Id);
            return;
        }
    }

    private bool ShouldEquip(Entity p, Item item, out Item? replaced)
    {
        p.Equipped.TryGetValue(item.Slot, out replaced);
        if (item.Slot == ItemSlot.Weapon)
        {
            var skill = _data.SkillForWeapon(item.Weapon);
            if (skill is null || p.SkillLevel(skill.Id) == 0 || item.ItemLevel > p.Level) return false;
            // A two-handed weapon only replaces a sword-and-shield set if the character has no shield skill.
            if (Weapons.Of(item.Weapon).TwoHanded && p.SkillLevel("shield") > 0 && p.Equipped.ContainsKey(ItemSlot.Shield)) return false;
            if (replaced is null) return true;
            var curSkill = _data.SkillForWeapon(replaced.Weapon);
            if (curSkill is not null && p.SkillLevel(curSkill.Id) > p.SkillLevel(skill.Id)) return false;
            return item.Damage * Weapons.Of(item.Weapon).DamageMul > replaced.Damage * Weapons.Of(replaced.Weapon).DamageMul;
        }
        if (item.ItemLevel > p.Level) return false;
        if (item.Slot == ItemSlot.Shield && (p.SkillLevel("shield") == 0 || Weapons.Of(WeaponOf(p)).TwoHanded)) return false;
        return replaced is null || Loot.Score(item) > Loot.Score(replaced);
    }

    private void TickMonster(Entity m, float dt)
    {
        if (m.Dead) return;
        if ((m.Flags & EntityFlags.Ally) != 0) { if (_boss is not null) TickAlly(m, dt); else TickFreeAlly(m, dt); return; }
        if ((m.Flags & EntityFlags.Yielded) != 0) return;
        if ((m.Flags & EntityFlags.Boss) != 0 && BossOverridesMovement(m, dt)) return;
        var def = m.Def!;
        if (m.SlowTimer > 0) { m.SlowTimer -= dt; if (m.SlowTimer <= 0) m.Flags &= ~EntityFlags.Slowed; }
        float speed = m.Speed * ((m.Flags & EntityFlags.Slowed) != 0 ? 0.5f : 1f);
        m.AttackTimer = System.Math.Max(0, m.AttackTimer - dt);

        Entity? target = m.TargetId != 0 ? Get(m.TargetId) : null;
        if (target is not null && (target.Flags & EntityFlags.Disguised) != 0 && def.Guard && Zone.Infiltration) target = null;
        if (target is null || target.Dead)
        {
            target = def.Guard && Zone.Infiltration ? GuardSees(m, def.DetectRange, dt) : NearestPlayer(m.Pos, def.DetectRange);
            m.TargetId = target?.Id ?? 0;
        }

        switch (m.State)
        {
            case MonsterState.Idle:
                if (target is not null) m.State = MonsterState.Chase;
                break;
            case MonsterState.Chase:
                if (target is null || Vec2.Distance(m.Pos, m.Home) > ((m.Flags & EntityFlags.Boss) != 0 ? 1800 : 1100)) { m.State = MonsterState.Return; m.TargetId = 0; break; }
                float reach = def.AttackRange + m.Radius + target.Radius;
                float dist = Vec2.Distance(m.Pos, target.Pos);
                if (dist > reach) MoveClamped(m, (target.Pos - m.Pos).Normalized() * speed * dt);
                else if (m.AttackTimer <= 0)
                {
                    m.AttackTimer = def.AttackCooldown;
                    HitPlayer(m, target);
                }
                break;
            case MonsterState.Return:
                // Ordinary monsters heal when they give up the chase; bosses keep their wounds, so a fight can't be reset by pulling them away.
                if (Vec2.Distance(m.Pos, m.Home) < 20) { m.State = MonsterState.Idle; if ((m.Flags & EntityFlags.Boss) == 0) m.Hp = m.MaxHp; break; }
                MoveClamped(m, (m.Home - m.Pos).Normalized() * speed * dt);
                break;
        }
    }

    private void HitPlayer(Entity m, Entity p, double? rawOverride = null)
    {
        if ((p.Flags & EntityFlags.Dueling) != 0) return; // duelists are inside the arena, untouchable by anything else
        if (SharedBlow(m, p, rawOverride ?? m.Damage)) return;
        HitPlayerCore(m, p, rawOverride ?? m.Damage);
    }

    private void HitPlayerCore(Entity m, Entity p, double raw)
    {
        if (p.Dead || (p.Flags & EntityFlags.Dueling) != 0) return;
        var cls = Classes.Get(p.Class);
        double reduction = cls.DamageReduction + p.BuffDefense + (1 - 100.0 / (100 + Defense(p) * 5));
        double dmgD = raw * (1 - System.Math.Min(0.75, reduction)) * (0.85 + _rng.NextDouble() * 0.3);
        RuneXp(p, 1, s => s != ItemSlot.Weapon); // armour runes learn from every blow they take
        if (_rng.NextDouble() < BlockChance(p))
        {
            dmgD *= 1 - _data.SkillRules.BlockReduction;
            Events.Add(new SimEvent(SimEventType.Blocked, m.Id, p.Id, 0));
            // Shield mastery grows with every blow it turns aside.
            if (p.MasteryFor("shield").AddHit())
                Events.Add(new SimEvent(SimEventType.MasteryUp, p.Id, p.Id, p.MasteryFor("shield").Level, "shield"));
        }
        int dmg = System.Math.Max(1, (int)System.Math.Round(dmgD));
        p.Hp -= dmg;
        Events.Add(new SimEvent(SimEventType.Damage, m.Id, p.Id, dmg));
        if (p.Hp <= 0) KillPlayer(p, m);
    }

    private Entity? NearestPlayer(Vec2 at, float range)
    {
        Entity? best = null;
        float bestD = range;
        foreach (var p in _entities.Values)
        {
            if (p.Kind != EntityKind.Player || p.Dead) continue;
            float d = Vec2.Distance(at, p.Pos);
            if (d <= bestD) { bestD = d; best = p; }
        }
        return best;
    }


    /// <summary>Pushes overlapping live monsters apart so packs spread out instead of stacking on one point.</summary>
    private void SeparateMonsters()
    {
        _scratch.Clear();
        foreach (var e in _entities.Values) if (e.Kind == EntityKind.Monster && !e.Dead && e.State != MonsterState.Idle) _scratch.Add(e);
        for (int i = 0; i < _scratch.Count; i++)
        for (int j = i + 1; j < _scratch.Count; j++)
        {
            var a = _scratch[i];
            var b = _scratch[j];
            var d = b.Pos - a.Pos;
            float min = a.Radius + b.Radius;
            float len = d.Length;
            if (len >= min || len < 0.001f) continue;
            var push = d / len * ((min - len) * 0.5f);
            a.Pos = ClampAndPush(a.Pos - push, a.Radius);
            b.Pos = ClampAndPush(b.Pos + push, b.Radius);
        }
    }
}
