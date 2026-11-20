using TacoFactory.Core.Data;
using TacoFactory.Core.Math;

namespace TacoFactory.Core.Sim;

/// <summary>
/// Data-driven boss fights (bosses.json) for Acts II-IV, plus the story mechanics they need:
/// a boss can yield and wait for the party's verdict, disguised players slip past guards in
/// infiltration zones, and allied Templars fight beside the party.
/// </summary>
public sealed partial class World
{
    private sealed class ScriptState
    {
        public required BossDef Def { get; init; }
        public required Entity Boss { get; init; }
        public int Phase = -1;
        public bool Engaged;
        public float GroupHp = 1;
        public float BaseDamage, BaseSpeed;
        public readonly Dictionary<BossAbilityDef, float> Timers = new();
        public readonly Dictionary<BossAbilityDef, List<Entity>> Adds = new();
        public readonly List<(Vec2 At, float Left, float Radius, float DamageMul)> Strikes = [];
        public bool Yielded, Resolved;
        public Vec2? ChargeTo;
        public float ChargeLeft;
    }

    private ScriptState? _script;
    private readonly Dictionary<int, float> _suspicion = new();

    public const float DisguiseSightRange = 75, SuspicionSeconds = 1.5f, AlarmRadius = 700;

    /// <summary>The scripted boss that yielded and waits for a verdict, if any.</summary>
    public Entity? YieldedBoss => _script is { Yielded: true, Resolved: false } s ? s.Boss : null;
    public BossDef? ScriptedBoss => _script?.Def;

    /// <summary>World bosses rise at the level of the knights who muster against them (set by the server).</summary>
    public int? BossLevel { get; set; }

    private void SpawnScriptedBoss(BossDef def, Vec2 at, float groupHp)
    {
        var su = _data.Superuniques[def.Id];
        int level = BossLevel ?? su.Level;
        var boss = SpawnMonster(_data.Monsters[su.Base], level, at, groupHp * su.HealthMul, su.DamageMul, su.SpeedMul);
        boss.DisplayName = su.Name;
        boss.UniqueId = su.Id;
        boss.Radius *= 1.35f;
        boss.Flags |= EntityFlags.Superunique | EntityFlags.Boss;
        var cap = RarityCap();
        boss.GuaranteedDrop = Loot.MakeItem(_rng, Loot.NewId(), level, cap >= Rarity.Epic ? Rarity.Epic : cap);
        boss.ExtraDrops.Add(Loot.MakeItem(_rng, Loot.NewId(), level, cap >= Rarity.Elite ? Rarity.Elite : cap));
        boss.ExtraDrops.Add(Loot.MakeItem(_rng, Loot.NewId(), level, Rarity.Rare));
        if (level >= 70) boss.ExtraDrops.Add(Loot.MakeItem(_rng, Loot.NewId(), level, cap));
        _script = new ScriptState { Def = def, Boss = boss, GroupHp = groupHp, BaseDamage = boss.Damage, BaseSpeed = boss.Speed };
        for (int i = 0; i < su.EscortCount; i++)
            SpawnMonster(_data.Monsters[su.Escort], level, at + new Vec2(_rng.Next(-160, 161), _rng.Next(-160, 161)), groupHp);
    }

    private void TickScript(float dt)
    {
        if (_script is not { } s || s.Boss.Dead || s.Resolved) return;
        var boss = s.Boss;
        if (!s.Engaged)
        {
            if (NearestPlayer(boss.Pos, 900) is null && boss.State != MonsterState.Chase) return;
            s.Engaged = true;
            Events.Add(new SimEvent(SimEventType.BossPhase, boss.Id, 0, 1, s.Def.Title));
        }
        if (s.Yielded) return;

        // Phase changes as health falls.
        float frac = boss.Hp / (float)boss.MaxHp;
        int phase = 0;
        for (int i = 0; i < s.Def.Phases.Count; i++) if (frac <= s.Def.Phases[i].Below) phase = i;
        if (phase != s.Phase && phase > s.Phase) EnterScriptPhase(s, phase);
        var ph = s.Def.Phases[s.Phase];

        if (ph.ShieldedByAdds)
        {
            bool adds = ph.Abilities.Where(a => a.Kind == "summon").Any(a => s.Adds.TryGetValue(a, out var l) && l.Any(x => !x.Dead));
            if (adds) boss.Flags |= EntityFlags.Invulnerable; else boss.Flags &= ~EntityFlags.Invulnerable;
        }

        foreach (var ab in ph.Abilities)
        {
            float t = s.Timers.GetValueOrDefault(ab, ab.First) - dt;
            if (t > 0) { s.Timers[ab] = t; continue; }
            s.Timers[ab] = ab.Every;
            UseAbility(s, ab);
        }

        // Charges carry the boss across the floor.
        if (s.ChargeTo is { } dest)
        {
            s.ChargeLeft -= dt;
            var to = dest - boss.Pos;
            if (to.Length < 30 || s.ChargeLeft <= 0) s.ChargeTo = null;
            else MoveClamped(boss, to.Normalized() * boss.Speed * 3.2f * dt);
        }

        // Marked ground strikes when its time is up.
        for (int i = s.Strikes.Count - 1; i >= 0; i--)
        {
            var (at, left, radius, mul) = s.Strikes[i];
            left -= dt;
            if (left > 0) { s.Strikes[i] = (at, left, radius, mul); continue; }
            s.Strikes.RemoveAt(i);
            foreach (var p in Players.Where(p => !p.Dead && Vec2.Distance(p.Pos, at) <= radius + p.Radius).ToList())
                HitPlayer(boss, p, s.BaseDamage * mul);
        }
    }

    private void EnterScriptPhase(ScriptState s, int phase)
    {
        s.Phase = phase;
        var ph = s.Def.Phases[phase];
        var boss = s.Boss;
        boss.Damage = System.Math.Max(1, (int)(s.BaseDamage * ph.DamageMul));
        boss.Speed = s.BaseSpeed * ph.SpeedMul;
        boss.Flags &= ~EntityFlags.Invulnerable;
        foreach (var ab in ph.Abilities) s.Timers[ab] = ab.First;
        if (ph.Announce.Length > 0) Events.Add(new SimEvent(SimEventType.BossPhase, boss.Id, 0, phase + 1, ph.Announce));
    }

    private void UseAbility(ScriptState s, BossAbilityDef ab)
    {
        var boss = s.Boss;
        var near = Players.Where(p => !p.Dead && Vec2.Distance(p.Pos, boss.Pos) < 1100).ToList();
        switch (ab.Kind)
        {
            case "summon":
            {
                if (!s.Adds.TryGetValue(ab, out var list)) s.Adds[ab] = list = [];
                list.RemoveAll(x => x.Dead);
                for (int i = 0; i < ab.Count && list.Count < ab.Max; i++)
                {
                    double angle = _rng.NextDouble() * System.Math.PI * 2;
                    var at = boss.Pos + new Vec2((float)System.Math.Cos(angle) * 240, (float)System.Math.Sin(angle) * 240);
                    var m = SpawnMonster(_data.Monsters[ab.Monster], boss.Level, at, s.GroupHp * 0.6f, _groupDamage == 0 ? 1 : 1);
                    if (ab.Name.Length > 0) m.DisplayName = ab.Name;
                    m.State = MonsterState.Chase;
                    list.Add(m);
                }
                if (ab.Text.Length > 0) Events.Add(new SimEvent(SimEventType.BossPhase, boss.Id, 0, s.Phase + 1, ab.Text));
                break;
            }
            case "telegraph":
            {
                // Marks the ground under some of the party; the blow lands after a delay.
                int marks = System.Math.Max(1, System.Math.Min(near.Count, ab.Count));
                foreach (var p in near.OrderBy(_ => _rng.Next()).Take(marks))
                {
                    s.Strikes.Add((p.Pos, ab.Delay, ab.Radius, ab.DamageMul));
                    Events.Add(new SimEvent(SimEventType.Telegraph, boss.Id, 0, (int)ab.Radius, $"{p.Pos.X:F0};{p.Pos.Y:F0};{ab.Delay}"));
                }
                break;
            }
            case "nova":
                // A ring around the boss: telegraphed, then everyone close is struck.
                s.Strikes.Add((boss.Pos, ab.Delay, ab.Radius, ab.DamageMul));
                Events.Add(new SimEvent(SimEventType.Telegraph, boss.Id, 0, (int)ab.Radius, $"{boss.Pos.X:F0};{boss.Pos.Y:F0};{ab.Delay}"));
                if (ab.Text.Length > 0) Events.Add(new SimEvent(SimEventType.BossPhase, boss.Id, 0, s.Phase + 1, ab.Text));
                break;
            case "charge":
                if (near.Count == 0) break;
                var farthest = near.OrderByDescending(p => Vec2.Distance(p.Pos, boss.Pos)).First();
                s.ChargeTo = farthest.Pos;
                s.ChargeLeft = 1.2f;
                s.Strikes.Add((farthest.Pos, 0.6f, ab.Radius, ab.DamageMul));
                Events.Add(new SimEvent(SimEventType.Telegraph, boss.Id, 0, (int)ab.Radius, $"{farthest.Pos.X:F0};{farthest.Pos.Y:F0};0.6"));
                break;
            case "heal":
                int amount = (int)(boss.MaxHp * ab.Amount);
                boss.Hp = System.Math.Min(boss.MaxHp, boss.Hp + amount);
                Events.Add(new SimEvent(SimEventType.Heal, boss.Id, boss.Id, amount));
                if (ab.Text.Length > 0) Events.Add(new SimEvent(SimEventType.BossPhase, boss.Id, 0, s.Phase + 1, ab.Text));
                break;
            case "curse":
                // Weakens everyone near for a while (damage down).
                foreach (var p in near)
                {
                    p.Buffs.RemoveAll(x => x.SpellId == "curse");
                    p.Buffs.Add(new Buff { SpellId = "curse", Damage = -ab.Amount, Remaining = 8 });
                    Events.Add(new SimEvent(SimEventType.Buffed, boss.Id, p.Id, 8, "curse"));
                }
                if (ab.Text.Length > 0) Events.Add(new SimEvent(SimEventType.BossPhase, boss.Id, 0, s.Phase + 1, ab.Text));
                break;
        }
    }

    /// <summary>A boss that can yield stops fighting at its threshold instead of dying. Returns true if it just yielded.</summary>
    private bool TryYield(Entity m)
    {
        if (_script is not { } s || s.Boss != m || s.Yielded || s.Def.Choice is null || s.Def.YieldAt <= 0) return false;
        if (m.Hp > m.MaxHp * s.Def.YieldAt) return false;
        s.Yielded = true;
        m.Hp = System.Math.Max(1, (int)(m.MaxHp * s.Def.YieldAt));
        m.Flags |= EntityFlags.Invulnerable | EntityFlags.Yielded;
        m.State = MonsterState.Idle;
        m.TargetId = 0;
        s.Strikes.Clear();
        foreach (var list in s.Adds.Values) foreach (var add in list) if (!add.Dead) { add.Hp = 0; add.Flags |= EntityFlags.Dead; }
        Events.Add(new SimEvent(SimEventType.Yielded, m.Id, 0, 0, m.UniqueId ?? ""));
        return true;
    }

    /// <summary>The party has decided. "kill": the boss dies with its loot. "spare": it leaves, and drops only its guaranteed item.</summary>
    public void ResolveYield(string outcome, int creditPlayerId)
    {
        if (_script is not { Yielded: true, Resolved: false } s) return;
        // Called between ticks (by the server, when the vote ends): the events it makes go out with the next tick.
        int before = Events.Count;
        try { Resolve(s, outcome, creditPlayerId); }
        finally
        {
            _deferred.AddRange(Events.Skip(before));
            Events.RemoveRange(before, Events.Count - before);
        }
    }

    private readonly List<SimEvent> _deferred = [];

    private void Resolve(ScriptState s, string outcome, int creditPlayerId)
    {
        s.Resolved = true;
        var boss = s.Boss;
        boss.Flags &= ~(EntityFlags.Invulnerable | EntityFlags.Yielded);
        var credit = Get(creditPlayerId) ?? Players.FirstOrDefault();
        if (outcome == "kill" && credit is not null) { KillMonster(boss, credit); return; }
        // Spared: walks away. Quest credit still comes from the UniqueKilled event.
        boss.Hp = 0;
        boss.Flags |= EntityFlags.Dead;
        boss.ExtraDrops.Clear();
        Events.Add(new SimEvent(SimEventType.Death, credit?.Id ?? 0, boss.Id, 0, boss.DisplayName));
        if (boss.UniqueId is string uid) Events.Add(new SimEvent(SimEventType.UniqueKilled, credit?.Id ?? 0, boss.Id, 1, uid));
        if (boss.GuaranteedDrop is Item g) DropItem(boss.Pos, g);
    }

    // ------------------------------------------------------------------ disguise

    /// <summary>Players entering an infiltration zone in costume are disguised until they fight or linger near a guard.</summary>
    public void Disguise(Entity p) => p.Flags |= EntityFlags.Disguised;

    /// <summary>Who a guard can see: disguised players only up close, and only after a moment's suspicion.</summary>
    private Entity? GuardSees(Entity guard, float range, float dt)
    {
        Entity? best = null;
        float bestD = range;
        foreach (var p in _entities.Values)
        {
            if (p.Kind != EntityKind.Player || p.Dead) continue;
            float d = Vec2.Distance(guard.Pos, p.Pos);
            if (d > bestD) continue;
            if ((p.Flags & EntityFlags.Disguised) != 0)
            {
                if (d > DisguiseSightRange) continue;
                float sus = _suspicion.GetValueOrDefault(p.Id) + dt;
                _suspicion[p.Id] = sus;
                if (sus < SuspicionSeconds) continue;
                BreakDisguise(p, "A guard looks twice. Your disguise is seen through!");
            }
            bestD = d;
            best = p;
        }
        return best;
    }

    private void BreakDisguise(Entity p, string why)
    {
        if ((p.Flags & EntityFlags.Disguised) == 0) return;
        p.Flags &= ~EntityFlags.Disguised;
        _suspicion.Remove(p.Id);
        Events.Add(new SimEvent(SimEventType.Alarm, p.Id, p.Id, 0, why));
        foreach (var m in _entities.Values)
        {
            if (m.Kind != EntityKind.Monster || m.Dead || m.Def?.Guard != true || Vec2.Distance(m.Pos, p.Pos) > AlarmRadius) continue;
            m.Flags |= EntityFlags.Alerted;
            m.State = MonsterState.Chase;
            m.TargetId = p.Id;
        }
    }

    // ------------------------------------------------------------------ allies

    /// <summary>Allied Templars (Act IV): spawn by the entrance and fight whatever is nearest. Monsters ignore them.</summary>
    private void SpawnAllies(int count)
    {
        if (count <= 0 || !_data.Monsters.TryGetValue(Zone.AllyMonster, out var def)) return;
        var spawn = new Vec2(Zone.PlayerSpawn[0], Zone.PlayerSpawn[1]);
        for (int i = 0; i < count; i++)
        {
            double angle = i * System.Math.PI * 2 / count;
            var a = SpawnMonster(def, Zone.LevelMin, spawn + new Vec2((float)System.Math.Cos(angle) * 160, (float)System.Math.Sin(angle) * 160), 1);
            a.Flags |= EntityFlags.Ally | EntityFlags.Invulnerable;
            a.DisplayName = "Brother of the Remnant";
            AlliesSpawned++;
        }
    }

    public int AlliesSpawned { get; private set; }

    private void TickFreeAlly(Entity a, float dt)
    {
        a.AttackTimer = System.Math.Max(0, a.AttackTimer - dt);
        Entity? target = a.TargetId != 0 ? Get(a.TargetId) : null;
        if (target is null || target.Dead || (target.Flags & (EntityFlags.Invulnerable | EntityFlags.Ally)) != 0)
        {
            target = null;
            float best = 900;
            foreach (var m in _entities.Values)
            {
                if (m.Kind != EntityKind.Monster || m.Dead || (m.Flags & (EntityFlags.Ally | EntityFlags.Invulnerable)) != 0) continue;
                float d = Vec2.Distance(m.Pos, a.Pos);
                if (d < best) { best = d; target = m; }
            }
            a.TargetId = target?.Id ?? 0;
        }
        if (target is null)
        {
            // Nothing to fight: keep up with the party.
            if (NearestPlayer(a.Pos, 6000) is { } lead && Vec2.Distance(lead.Pos, a.Pos) > 220)
                MoveClamped(a, (lead.Pos - a.Pos).Normalized() * a.Speed * dt);
            return;
        }
        float reach = a.Def!.AttackRange + a.Radius + target.Radius;
        if (Vec2.Distance(a.Pos, target.Pos) > reach) { MoveClamped(a, (target.Pos - a.Pos).Normalized() * a.Speed * dt); return; }
        if (a.AttackTimer > 0) return;
        a.AttackTimer = a.Def.AttackCooldown;
        int dmg = System.Math.Max(1, (int)(a.Damage * 0.8));
        target.Hp -= dmg;
        Events.Add(new SimEvent(SimEventType.Damage, a.Id, target.Id, dmg));
        if (target.State == MonsterState.Idle) target.State = MonsterState.Chase;
        if (target.Hp <= 0)
        {
            if (TryYield(target)) return;
            if (NearestPlayer(target.Pos, 5000) is { } credit) KillMonster(target, credit);
            else target.Hp = 1;
        }
    }
}
