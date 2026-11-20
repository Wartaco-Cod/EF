using TacoFactory.Core.Data;
using TacoFactory.Core.Math;

namespace TacoFactory.Core.Sim;

/// <summary>
/// Endgame rules that live inside a zone copy: level sync, runes growing with use, the Mist takeover (embers, threat,
/// the Grey Huntsman, coffers), the Proving Pit's floors, weekly instabilities, and the world boss's shared blows.
/// </summary>
public sealed partial class World
{
    // ------------------------------------------------------------------ level sync

    /// <summary>
    /// In a fighting zone, a knight more than two levels above its top level fights as if two levels above it,
    /// with gear power scaled to match. Towns, the Pit and world bosses never sync.
    /// </summary>
    public void ApplySync(Entity p)
    {
        int cap = Zone.LevelMax + 2;
        bool sync = Zone.LevelSync && !Zone.IsTown && !Zone.ProvingPit && !Zone.WorldBoss && Zone.LevelMax > 0 && p.Level > cap;
        p.SyncLevel = sync ? cap : 0;
        if (!sync) { p.GearScale = 1; return; }
        double avg = p.Equipped.Count == 0 ? p.Level : p.Equipped.Values.Average(i => i.ItemLevel);
        p.GearScale = (float)System.Math.Clamp((cap + 8) / (avg + 8), 0.2, 1.0);
    }

    // ------------------------------------------------------------------ runes

    private void RuneXp(Entity p, long xp, Func<ItemSlot, bool> where)
    {
        foreach (var (slot, item) in p.Equipped)
        {
            if (!where(slot)) continue;
            foreach (var r in Runes.In(item))
                if (Runes.AddXp(r, xp)) Events.Add(new SimEvent(SimEventType.RuneUp, p.Id, p.Id, r.Level, Runes.Name(r)));
        }
    }

    /// <summary>Every rune worn learns from each kill made nearby, more from tougher foes.</summary>
    private void RuneKillXp(Entity p, Entity m)
    {
        double toughness = System.Math.Clamp((m.Def?.Health ?? 40) / 40.0, 0.25, 4.0);
        long xp = (long)System.Math.Max(1, 10 * toughness * ((m.Flags & EntityFlags.Superunique) != 0 ? 5 : 1));
        RuneXp(p, xp, _ => true);
    }

    // ------------------------------------------------------------------ weekly instabilities

    /// <summary>This week's instabilities (Mist zones and the Proving Pit only), set by the server.</summary>
    public List<string> Instabilities { get; } = [];
    private bool Unstable(string id) => (MistActive || Zone.ProvingPit) && Instabilities.Contains(id);

    private readonly List<(Vec2 At, float Left, float Radius, int Damage)> _blasts = [];
    private float _plagueTimer = 8;

    private void TickInstabilities(float dt)
    {
        for (int i = _blasts.Count - 1; i >= 0; i--)
        {
            var (at, left, radius, dmg) = _blasts[i];
            left -= dt;
            if (left > 0) { _blasts[i] = (at, left, radius, dmg); continue; }
            _blasts.RemoveAt(i);
            foreach (var p in Players.Where(p => !p.Dead && Vec2.Distance(p.Pos, at) <= radius + p.Radius).ToList())
                DamagePlayer(null, p, dmg, "blast");
        }
        if (Unstable("plague") && Players.Any(p => !p.Dead) && (_plagueTimer -= dt) <= 0)
        {
            _plagueTimer = 9;
            var target = Players.Where(p => !p.Dead).OrderBy(_ => _rng.Next()).First();
            Blast(target.Pos, 95, (int)(target.MaxHp * 0.14), 1.4f);
        }
        if (Unstable("vigour"))
            foreach (var m in Monsters)
                if (!m.Dead && m.Hp < m.MaxHp && (TickCount % 20) == 0) m.Hp = System.Math.Min(m.MaxHp, m.Hp + System.Math.Max(1, m.MaxHp / 100));
    }

    private void Blast(Vec2 at, float radius, int damage, float delay)
    {
        _blasts.Add((at, delay, radius, damage));
        Events.Add(new SimEvent(SimEventType.Telegraph, 0, 0, (int)radius, $"{at.X:F0};{at.Y:F0};{delay}"));
    }

    /// <summary>Monster stats when spawned under the Mist or the Pit's instabilities.</summary>
    private (float Hp, float Dmg, float Speed) InstabilityMuls(bool elite)
    {
        float hp = 1, dmg = 1, speed = 1;
        if (Unstable("bloodthirst")) dmg *= 1.25f;
        if (Unstable("resolute")) hp *= elite ? 1.5f : 1.15f;
        if (Unstable("frenzy")) speed *= 1.2f;
        return (hp, dmg, speed);
    }

    // ------------------------------------------------------------------ the Mist

    public const float MistHealthMul = 1.6f, MistDamageMul = 1.35f, ThreatPerKill = 3, ThreatPerElite = 10, CofferCost = 60;
    public const int MistLevelBonus = 3;

    /// <summary>True while the Grey Mist holds this zone.</summary>
    public bool MistActive { get; private set; }
    private Entity? _huntsman;
    private readonly List<Entity> _coffers = [];

    /// <summary>The Mist arrives (or lifts). Arriving strengthens every monster already here and sets out the coffers.</summary>
    public void SetMist(bool on)
    {
        if (on == MistActive) return;
        MistActive = on;
        if (on)
        {
            foreach (var m in Monsters.Where(m => !m.Dead && (m.Flags & EntityFlags.Ally) == 0)) Mistify(m);
            if (Populated) PlaceCoffers();
        }
        else
        {
            foreach (var c in _coffers) _entities.Remove(c.Id);
            _coffers.Clear();
            foreach (var p in Players) { p.Extras.Threat = 0; p.Extras.Hunted = false; }
        }
    }

    private void Mistify(Entity m)
    {
        if (m.Mist) return;
        m.Mist = true;
        m.Level += MistLevelBonus;
        var (hp, dmg, speed) = InstabilityMuls((m.Flags & EntityFlags.Superunique) != 0);
        m.MaxHp = (int)(m.MaxHp * MistHealthMul * hp);
        m.Hp = (int)(m.Hp * MistHealthMul * hp);
        m.Damage = System.Math.Max(1, (int)(m.Damage * MistDamageMul * dmg));
        m.Speed *= speed;
    }

    private void PlaceCoffers()
    {
        if (_coffers.Count > 0) return;
        var spawn = new Vec2(Zone.PlayerSpawn[0], Zone.PlayerSpawn[1]);
        for (int i = 0; i < 3; i++)
        {
            Vec2 at;
            int guard = 0;
            do at = new Vec2(_rng.NextSingle() * (Zone.Width - 600) + 300, _rng.NextSingle() * (Zone.Height - 600) + 300);
            while ((Vec2.Distance(at, spawn) < 500 || _coffers.Any(c => Vec2.Distance(c.Pos, at) < 700)) && ++guard < 60);
            var c = new Entity { Id = _nextId++, Kind = EntityKind.Chest, Pos = ClampAndPush(at, 40), Radius = 30, Hp = 1, MaxHp = 1, DisplayName = "Mist Coffer", Flags = EntityFlags.Invulnerable };
            _entities[c.Id] = c;
            _coffers.Add(c);
        }
    }

    public IReadOnlyList<Entity> Coffers => _coffers;

    /// <summary>Opens a Mist coffer for its price in embers. The server rolls and hands out the contents.</summary>
    public string? OpenCoffer(Entity p, int cofferId)
    {
        if (!MistActive || _coffers.FirstOrDefault(c => c.Id == cofferId) is not { } c) return "The coffer has sunk back into the Mist.";
        if (Vec2.Distance(p.Pos, c.Pos) > 160) return "Walk closer to the coffer.";
        if (!p.Extras.Spend(Materials.MistEmber, (int)CofferCost)) return $"The coffer wants {CofferCost} Mist embers.";
        p.Extras.Count("coffer");
        Events.Add(new SimEvent(SimEventType.CofferOpened, p.Id, c.Id, 0, ""));
        return null;
    }

    private void MistKill(Entity m, Entity killer, List<Entity> sharers)
    {
        if (!MistActive) return;
        bool elite = (m.Flags & EntityFlags.Superunique) != 0;
        foreach (var p in sharers)
        {
            int embers = elite ? 12 : _rng.Next(1, 4);
            p.Extras.Give(Materials.MistEmber, embers);
            p.Extras.Count("embers", embers);
        }
        if (m == _huntsman)
        {
            _huntsman = null;
            foreach (var p in sharers) { p.Extras.Threat = 0; p.Extras.Hunted = false; p.Extras.Give(Materials.MistEmber, 40); p.Extras.Count("huntsman"); }
            Events.Add(new SimEvent(SimEventType.HuntsmanSlain, killer.Id, m.Id, 0, "The Grey Huntsman"));
            return;
        }
        if (_huntsman is not null) return;
        killer.Extras.Threat = System.Math.Min(100, killer.Extras.Threat + (elite ? ThreatPerElite : ThreatPerKill));
        if (killer.Extras.Threat >= 100 && !killer.Extras.Hunted) SummonHuntsman(killer);
    }

    /// <summary>Threat full: the Grey Huntsman and his hounds come out of the Mist around this knight.</summary>
    private void SummonHuntsman(Entity p)
    {
        p.Extras.Hunted = true;
        var baseDef = _data.Monsters.GetValueOrDefault("royal_huntsman") ?? _data.Monsters.Values.First();
        int level = System.Math.Max(Zone.LevelMax, p.Level) + MistLevelBonus;
        var angle = _rng.NextDouble() * System.Math.PI * 2;
        var at = p.Pos + new Vec2((float)System.Math.Cos(angle), (float)System.Math.Sin(angle)) * 320;
        int group = System.Math.Max(1, Players.Count(x => !x.Dead));
        var h = SpawnMonster(baseDef, level, at, 9f * (1 + 0.6f * (group - 1)), 2.2f, 1.15f);
        h.DisplayName = "The Grey Huntsman";
        h.UniqueId = "grey_huntsman";
        h.Flags |= EntityFlags.Superunique;
        h.Radius *= 1.3f;
        h.Mist = true;
        h.State = MonsterState.Chase;
        h.TargetId = p.Id;
        h.GuaranteedDrop = Loot.MakeItem(_rng, Loot.NewId(), level, Loot.RollRarity(_rng, Rarity.Legendary, Rarity.Epic));
        _huntsman = h;
        var hound = _data.Monsters.GetValueOrDefault("court_hound") ?? baseDef;
        for (int i = 0; i < 3; i++)
        {
            var e = SpawnMonster(hound, level, at + new Vec2(_rng.Next(-90, 91), _rng.Next(-90, 91)), 2.5f, 1.4f, 1.1f);
            e.DisplayName = "Mist Hound";
            e.Mist = true;
            e.State = MonsterState.Chase;
            e.TargetId = p.Id;
        }
        Events.Add(new SimEvent(SimEventType.Hunted, p.Id, h.Id, 0, p.Name));
    }

    // ------------------------------------------------------------------ the Proving Pit

    public const int PitFloors = 10;
    public int PitFloor { get; private set; }
    /// <summary>Level the Pit's monsters start from (the party's highest), set by the server before the first floor.</summary>
    public int PitLevel { get; set; } = 1;
    private float _pitBreak = -1;
    public bool PitComplete { get; private set; }

    private static readonly string[][] PitRoster =
    [
        ["royal_sergeant", "hunting_mastiff", "crossbowman", "man_at_arms"],
        ["flagellant", "warhound", "confessors_acolyte", "iron_sergeant"],
        ["routier", "routier_archer", "smuggler_bruiser", "garrison_pikeman"],
        ["fallen_templar", "risen_marshal", "chapel_wraith", "quarry_ghoul"],
        ["inquisition_jailer", "dominican_inquisitor", "inquisition_executioner", "penitent_zealot"],
        ["court_guard", "court_sergeant", "court_hound", "apostate_templar"],
        ["ossuary_knight", "cathedral_shade", "papal_guard", "papal_crossbowman"],
        ["royal_guardsman", "royal_archer", "household_knight", "provosts_man"],
        ["vatican_champion", "cardinal_inquisitor", "living_effigy", "tower_warden"],
        ["chosen_knight", "confession_wraith", "vatican_champion", "royal_guardsman"],
    ];

    private void StartPitFloor(int floor)
    {
        PitFloor = floor;
        int group = System.Math.Clamp(Players.Count(), 1, MaxGroupSize);
        float groupHp = 1f + Difficulty.GroupHealthPerPlayer * (group - 1);
        _groupDamage = 1f + Difficulty.GroupDamagePerPlayer * (group - 1);
        int level = System.Math.Clamp(PitLevel + floor / 2, 1, Progression.MaxLevel);
        float hp = groupHp * (1 + 0.18f * (floor - 1)), dmg = 1 + 0.08f * (floor - 1);
        var roster = PitRoster[floor - 1].Where(_data.Monsters.ContainsKey).ToList();
        if (roster.Count == 0) roster = [_data.Monsters.Keys.First()];
        var center = new Vec2(Zone.Width / 2, Zone.Height / 2);
        int packs = 3 + floor / 2;
        for (int k = 0; k < packs; k++)
        {
            var ang = k / (float)packs * MathF.PI * 2 + _rng.NextSingle() * 0.4f;
            var at = center + new Vec2(MathF.Cos(ang), MathF.Sin(ang) * 0.7f) * (420 + _rng.Next(0, 160));
            int n = 3 + _rng.Next(0, 3);
            for (int i = 0; i < n; i++)
            {
                var (ih, id, isp) = InstabilityMuls(false);
                var m = SpawnMonster(_data.Monsters[roster[_rng.Next(roster.Count)]], level, at + new Vec2(_rng.Next(-80, 81), _rng.Next(-80, 81)), hp * ih, dmg * id, isp);
                m.State = MonsterState.Chase;
            }
        }
        if (floor % 5 == 0)
        {
            var champ = _data.Monsters.GetValueOrDefault(floor == 10 ? "chosen_knight" : "garrison_knight") ?? _data.Monsters[roster[0]];
            var (ih, id, isp) = InstabilityMuls(true);
            var c = SpawnMonster(champ, level + 2, center + new Vec2(0, -260), hp * 6 * ih, 1.8f * dmg * id, isp);
            c.DisplayName = floor == 10 ? "The Champion of the Pit" : "The Pit's Gatekeeper";
            c.UniqueId = floor == 10 ? "pit_champion" : "pit_gatekeeper";
            c.Flags |= EntityFlags.Superunique;
            c.Radius *= 1.3f;
        }
        Events.Add(new SimEvent(SimEventType.PitFloor, 0, 0, floor, $"Floor {floor} of {PitFloors}"));
    }

    private void TickPit(float dt)
    {
        if (!Zone.ProvingPit || PitComplete || PitFloor == 0) return;
        if (_pitBreak >= 0)
        {
            if ((_pitBreak -= dt) <= 0) { _pitBreak = -1; StartPitFloor(PitFloor + 1); }
            return;
        }
        if (MonstersAlive > 0) return;
        Events.Add(new SimEvent(SimEventType.PitFloorCleared, 0, 0, PitFloor, $"Floor {PitFloor} cleared"));
        foreach (var p in Players)
        {
            p.Extras.Best("pit_floor", PitFloor);
            p.Extras.PitBest = System.Math.Max(p.Extras.PitBest, PitFloor);
            if (!p.Dead) Heal(p, p, p.MaxHp * 3 / 10);
            else Revive(p, p.MaxHp / 2, p.Pos, null);
        }
        foreach (var loot in _entities.Values.Where(e => e.Kind == EntityKind.Loot).ToList())
        {
            // Pull floor loot to the middle so nothing is left in a corner.
            loot.Pos = new Vec2(Zone.Width / 2, Zone.Height / 2) + new Vec2(_rng.Next(-120, 121), _rng.Next(-80, 81));
        }
        if (PitFloor >= PitFloors) { PitComplete = true; Cleared = true; ClearedAt = Time; Events.Add(new SimEvent(SimEventType.ZoneCleared, 0, 0, (int)Time, Zone.Name)); return; }
        _pitBreak = 6;
    }

    // ------------------------------------------------------------------ world bosses

    /// <summary>
    /// At a world boss, its blows are shared: the damage is split between every living knight in the arena,
    /// so no single knight is singled out and cut down.
    /// </summary>
    private bool SharedBlow(Entity m, Entity target, double raw)
    {
        if (!Zone.WorldBoss || (m.Flags & EntityFlags.Boss) == 0) return false;
        var alive = Players.Where(p => !p.Dead && (p.Flags & EntityFlags.Dueling) == 0).ToList();
        if (alive.Count <= 1) return false;
        double each = raw * System.Math.Pow(alive.Count, 0.6) / alive.Count;
        foreach (var p in alive) HitPlayerCore(m, p, each);
        return true;
    }

    public const float ReviveRange = 140, WorldBossRespawn = 30;

    /// <summary>At a world boss only: a living knight raises a fallen one where they lie.</summary>
    public string? ReviveAlly(Entity by, int targetId)
    {
        if (!Zone.WorldBoss) return "Allies can only be revived at a world boss.";
        if (by.Dead) return "You are down yourself.";
        if (Get(targetId) is not { Kind: EntityKind.Player, Dead: true } t) return "There is no one to raise there.";
        if (Vec2.Distance(by.Pos, t.Pos) > ReviveRange) return "Get closer to them.";
        Revive(t, t.MaxHp * 2 / 5, t.Pos, by);
        by.Extras.Count("revive");
        return null;
    }

    // ------------------------------------------------------------------ shared hooks

    /// <summary>Damage to a player from no monster in particular (instability blasts).</summary>
    private void DamagePlayer(Entity? source, Entity p, int dmg, string text)
    {
        if (p.Dead || (p.Flags & EntityFlags.Dueling) != 0) return;
        p.Hp -= System.Math.Max(1, dmg);
        Events.Add(new SimEvent(SimEventType.Damage, source?.Id ?? 0, p.Id, dmg, text));
        if (p.Hp <= 0) KillPlayer(p, source);
    }

    private void KillPlayer(Entity p, Entity? by)
    {
        p.Hp = 0;
        p.Flags |= EntityFlags.Dead;
        p.Deaths++;
        p.RespawnTimer = Zone.WorldBoss ? WorldBossRespawn : RespawnSeconds;
        p.Input = default;
        Events.Add(new SimEvent(SimEventType.Death, by?.Id ?? 0, p.Id, 0, p.Name));
        foreach (var other in Monsters.Where(o => o.TargetId == p.Id)) other.TargetId = 0;
    }

    /// <summary>On every monster death: instability effects, Mist rewards, rune experience and daily tallies.</summary>
    private void OnMonsterKilled(Entity m, Entity killer, List<Entity> sharers)
    {
        bool elite = (m.Flags & EntityFlags.Superunique) != 0;
        foreach (var p in sharers)
        {
            RuneKillXp(p, m);
            p.Extras.Count("kill");
            if ((m.Flags & EntityFlags.Undead) != 0) p.Extras.Count("undead");
            if (elite) p.Extras.Count("elite");
        }
        if (Unstable("martyrdom") && (m.Flags & EntityFlags.Ally) == 0) Blast(m.Pos, 85, (int)(m.Damage * 2.2), 1.2f);
        MistKill(m, killer, sharers);
    }
}
