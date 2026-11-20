using TacoFactory.Core.Data;
using TacoFactory.Core.Math;

namespace TacoFactory.Core.Sim;

/// <summary>
/// Guillaume Imbert, the Act I boss (ACT1_WORLD_DESIGN, "The Question").
/// Phase 1: invulnerable while the four confessions he reads out are standing; casts Doubt.
/// Phase 2 (66%): burns pages that become Chapel Wraiths, throws telegraphed Anathema, re-reads a dead confession.
/// Phase 3 (33%): the dead rise against him, his defenses drop and he runs.
/// </summary>
public sealed partial class World
{
    private static readonly string[] ConfessionNames = ["Brother Raoul", "Brother Gérard", "Brother Ponsard", "Brother Hugues"];

    private sealed class BossState
    {
        public required Entity Boss { get; init; }
        public Vec2 Home;
        public int Phase = 1;
        public bool Engaged;
        public int ConfessionsSpawned;
        public float SpawnTimer, DoubtTimer = 6, WraithTimer = 2, AnathemaTimer = 1.5f, RaiseTimer = 20;
        public float GroupHp = 1;
        public readonly List<Entity> Confessions = [];
        public readonly List<Entity> Wraiths = [];
        public readonly List<(Vec2 At, float Left)> Anathema = [];
    }

    private BossState? _boss;

    public const float AnathemaRadius = 80, AnathemaDelay = 1.2f;

    public int BossPhase => _boss?.Phase ?? 0;
    public Entity? BossEntity => _boss?.Boss;

    private void SpawnBoss(string uniqueId, Vec2 at, float groupHp)
    {
        var su = _data.Superuniques[uniqueId];
        var boss = SpawnMonster(_data.Monsters[su.Base], su.Level, at, groupHp * su.HealthMul, su.DamageMul, su.SpeedMul);
        boss.DisplayName = su.Name;
        boss.UniqueId = su.Id;
        boss.Radius *= 1.3f;
        boss.Flags |= EntityFlags.Superunique | EntityFlags.Boss | EntityFlags.Invulnerable;
        boss.GuaranteedDrop = Loot.MakeItem(_rng, Loot.NewId(), su.Level, Rarity.Epic);
        boss.ExtraDrops.Add(Loot.MakeItem(_rng, Loot.NewId(), su.Level, Rarity.Elite));
        boss.ExtraDrops.Add(Loot.MakeItem(_rng, Loot.NewId(), su.Level, Rarity.Rare));
        boss.ExtraDrops.Add(Loot.MakeItem(_rng, Loot.NewId(), su.Level, Rarity.Rare));
        _boss = new BossState { Boss = boss, Home = at, GroupHp = groupHp };
    }

    private double BossDamageTakenMultiplier(Entity m) => _boss?.Boss == m && _boss.Phase == 3 ? 1.5 : 1.0;

    /// <summary>Imbert stands and reads in phases 1-2 (ranged attack only) and flees in phase 3.</summary>
    private bool BossOverridesMovement(Entity boss, float dt)
    {
        if (_boss?.Boss != boss) return false;
        var nearest = NearestPlayer(boss.Pos, 2000);
        if (_boss.Phase == 3)
        {
            if (nearest is not null)
            {
                var away = (boss.Pos - nearest.Pos).Normalized();
                MoveClamped(boss, (away.LengthSquared > 0 ? away : new Vec2(1, 0)) * boss.Speed * 1.1f * dt);
            }
            return true;
        }
        if (nearest is not null && Vec2.Distance(nearest.Pos, boss.Pos) <= boss.Def!.AttackRange + boss.Radius + nearest.Radius && boss.AttackTimer <= 0)
        {
            boss.AttackTimer = boss.Def.AttackCooldown;
            HitPlayer(boss, nearest);
        }
        return true;
    }

    private void TickBoss(float dt)
    {
        if (_boss is not { } b || b.Boss.Dead) return;
        var boss = b.Boss;
        boss.AttackTimer = System.Math.Max(0, boss.AttackTimer - dt);
        if (!b.Engaged)
        {
            if (NearestPlayer(boss.Pos, 900) is null) return;
            b.Engaged = true;
            Events.Add(new SimEvent(SimEventType.BossPhase, boss.Id, 0, 1, "The Question"));
        }

        float frac = boss.Hp / (float)boss.MaxHp;
        if (b.Phase == 1 && frac <= 0.66f) EnterPhase(b, 2);
        if (b.Phase == 2 && frac <= 0.33f) EnterPhase(b, 3);

        switch (b.Phase)
        {
            case 1:
                b.SpawnTimer -= dt;
                if (b.ConfessionsSpawned < ConfessionNames.Length && b.SpawnTimer <= 0)
                {
                    b.SpawnTimer = 3;
                    var c = SpawnAdd("fallen_templar", $"Confession of {ConfessionNames[b.ConfessionsSpawned]}", b, b.ConfessionsSpawned);
                    b.Confessions.Add(c);
                    b.ConfessionsSpawned++;
                }
                bool shielded = b.ConfessionsSpawned < ConfessionNames.Length || b.Confessions.Any(c => !c.Dead);
                if (shielded) boss.Flags |= EntityFlags.Invulnerable; else boss.Flags &= ~EntityFlags.Invulnerable;
                Doubt(b, dt);
                break;
            case 2:
                Doubt(b, dt);
                if ((b.WraithTimer -= dt) <= 0)
                {
                    b.WraithTimer = 7;
                    if (b.Wraiths.Count(w => !w.Dead) < 3) b.Wraiths.Add(SpawnAdd("chapel_wraith", "Burned Page", b, b.Wraiths.Count));
                }
                if ((b.AnathemaTimer -= dt) <= 0)
                {
                    b.AnathemaTimer = 5;
                    var targets = Players.Where(p => !p.Dead && Vec2.Distance(p.Pos, boss.Pos) < 1000).ToList();
                    if (targets.Count > 0)
                    {
                        var at = targets[_rng.Next(targets.Count)].Pos;
                        b.Anathema.Add((at, AnathemaDelay));
                        Events.Add(new SimEvent(SimEventType.Telegraph, boss.Id, 0, (int)AnathemaRadius, $"{at.X:F0};{at.Y:F0};{AnathemaDelay}"));
                    }
                }
                if ((b.RaiseTimer -= dt) <= 0)
                {
                    b.RaiseTimer = 20;
                    if (b.Confessions.FirstOrDefault(c => c.Dead) is { } dead)
                    {
                        dead.Flags &= ~EntityFlags.Dead;
                        dead.Hp = dead.MaxHp / 2;
                        dead.State = MonsterState.Chase;
                        Events.Add(new SimEvent(SimEventType.BossPhase, boss.Id, dead.Id, 2, $"He reads the confession of {dead.DisplayName[14..]} again."));
                    }
                }
                break;
        }

        // Anathema lands where it was marked; anyone still standing there is struck.
        for (int i = b.Anathema.Count - 1; i >= 0; i--)
        {
            var (at, left) = b.Anathema[i];
            left -= dt;
            if (left > 0) { b.Anathema[i] = (at, left); continue; }
            b.Anathema.RemoveAt(i);
            foreach (var p in Players.Where(p => !p.Dead && Vec2.Distance(p.Pos, at) <= AnathemaRadius + p.Radius).ToList())
                HitPlayer(boss, p, boss.Damage * 4.0);
        }
    }

    private void EnterPhase(BossState b, int phase)
    {
        b.Phase = phase;
        var boss = b.Boss;
        boss.Flags &= ~EntityFlags.Invulnerable;
        if (phase == 2)
            Events.Add(new SimEvent(SimEventType.BossPhase, boss.Id, 0, 2, "The Ledger: he burns the pages."));
        if (phase == 3)
        {
            b.Anathema.Clear();
            Events.Add(new SimEvent(SimEventType.BossPhase, boss.Id, 0, 3, "Recantation: the dead rise against him."));
            for (int i = 0; i < 6; i++)
            {
                var ally = SpawnAdd("fallen_templar", "Recanted Brother", b, i, ring: 520);
                ally.Flags |= EntityFlags.Ally;
                ally.State = MonsterState.Chase;
                ally.TargetId = boss.Id;
            }
        }
    }

    private Entity SpawnAdd(string monsterId, string name, BossState b, int index, float ring = 260)
    {
        double angle = index * System.Math.PI * 2 / 6 + 0.4;
        var at = b.Boss.Pos + new Vec2((float)System.Math.Cos(angle) * ring, (float)System.Math.Sin(angle) * ring);
        var m = SpawnMonster(_data.Monsters[monsterId], b.Boss.Level, at, b.GroupHp);
        m.DisplayName = name;
        m.State = MonsterState.Chase;
        return m;
    }

    private void Doubt(BossState b, float dt)
    {
        if ((b.DoubtTimer -= dt) > 0) return;
        b.DoubtTimer = 12;
        foreach (var p in Players.Where(p => !p.Dead && Vec2.Distance(p.Pos, b.Boss.Pos) < 1000))
        {
            p.Buffs.RemoveAll(x => x.SpellId == "doubt");
            p.Buffs.Add(new Buff { SpellId = "doubt", Damage = -0.25f, Remaining = 8 });
            Events.Add(new SimEvent(SimEventType.Buffed, b.Boss.Id, p.Id, 8, "doubt"));
        }
    }

    /// <summary>Allied risen dead (phase 3) chase Imbert and strike him; the kill is credited to the nearest player.</summary>
    private void TickAlly(Entity a, float dt)
    {
        a.AttackTimer = System.Math.Max(0, a.AttackTimer - dt);
        if (_boss is not { } b || b.Boss.Dead)
        {
            a.Hp = 0;
            a.Flags |= EntityFlags.Dead;
            return;
        }
        var boss = b.Boss;
        float reach = a.Def!.AttackRange + a.Radius + boss.Radius;
        if (Vec2.Distance(a.Pos, boss.Pos) > reach) { MoveClamped(a, (boss.Pos - a.Pos).Normalized() * a.Speed * dt); return; }
        if (a.AttackTimer > 0) return;
        a.AttackTimer = a.Def.AttackCooldown;
        int dmg = System.Math.Max(1, (int)(a.Damage * 0.6 * BossDamageTakenMultiplier(boss)));
        boss.Hp -= dmg;
        Events.Add(new SimEvent(SimEventType.Damage, a.Id, boss.Id, dmg));
        if (boss.Hp <= 0 && NearestPlayer(boss.Pos, 5000) is { } credit) KillMonster(boss, credit);
        else if (boss.Hp <= 0) boss.Hp = 1;
    }
}
