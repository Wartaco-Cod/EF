using TacoFactory.Core.Data;
using TacoFactory.Core.Math;

namespace TacoFactory.Core.Sim;

/// <summary>
/// Arena duels. Two players in a town with an arena agree to a duel; they are placed at opposite ends,
/// a 3-second countdown runs, then only they can hurt each other. Nobody dies: the duel ends when one
/// would drop to zero, leaves the ring, disconnects, or after 3 minutes (a draw).
/// </summary>
public sealed partial class World
{
    public const float DuelCountdown = 3, DuelMaxSeconds = 180;

    private sealed class Duel
    {
        public required Entity A { get; init; }
        public required Entity B { get; init; }
        public float Elapsed;
        public bool Live => Elapsed >= DuelCountdown;
    }

    private readonly List<Duel> _duels = [];

    public bool InDuel(Entity p) => _duels.Any(d => d.A == p || d.B == p);

    /// <summary>Starts a duel. Returns null on success or the reason it can't start.</summary>
    public string? StartDuel(Entity a, Entity b)
    {
        if (Zone.Arena is not { } arena) return "Duels happen in a town arena.";
        if (a == b) return "You can't duel yourself.";
        if (a.Dead || b.Dead) return "Both duelists must be standing.";
        if (InDuel(a) || InDuel(b)) return "One of you is already in a duel.";
        float cy = arena.Y + arena.Height / 2;
        a.Pos = new Vec2(arena.X + 80, cy);
        b.Pos = new Vec2(arena.X + arena.Width - 80, cy);
        foreach (var p in new[] { a, b })
        {
            p.Flags |= EntityFlags.Dueling;
            p.Hp = p.MaxHp;
            p.Mana = p.MaxMana;
            p.Input = default;
        }
        _duels.Add(new Duel { A = a, B = b });
        Events.Add(new SimEvent(SimEventType.DuelStarted, a.Id, b.Id, (int)DuelCountdown, arena.Name));
        return null;
    }

    private Entity? ActiveOpponent(Entity p)
    {
        var d = _duels.FirstOrDefault(d => d.Live && (d.A == p || d.B == p));
        return d is null ? null : d.A == p ? d.B : d.A;
    }

    private Entity? DuelOpponent(Entity attacker, int id) => ActiveOpponent(attacker) is { } o && o.Id == id ? o : null;

    /// <summary>A hit between duelists: same armour, block and mastery rules as fighting monsters.</summary>
    private int DuelHit(Entity p, Entity target, double raw, double crit, string? skill, SpellDef? spell)
    {
        var duel = _duels.FirstOrDefault(d => d.Live && (d.A == p && d.B == target || d.B == p && d.A == target));
        if (duel is null) return 0;
        var cls = Classes.Get(target.Class);
        double reduction = cls.DamageReduction + target.BuffDefense + (1 - 100.0 / (100 + Defense(target) * 5));
        double mult = System.Math.Max(0.1, 1 + p.BuffDamage) * (1 - System.Math.Min(0.75, reduction));
        if (skill is not null) mult *= p.MasteryFor(skill).DamageMultiplier;
        bool isCrit = _rng.NextDouble() < crit;
        if (isCrit) mult *= 1.5;
        double dmgD = raw * mult * (0.9 + _rng.NextDouble() * 0.2);
        if (_rng.NextDouble() < BlockChance(target))
        {
            dmgD *= 1 - _data.SkillRules.BlockReduction;
            Events.Add(new SimEvent(SimEventType.Blocked, p.Id, target.Id, 0));
        }
        int dmg = System.Math.Max(1, (int)System.Math.Round(dmgD));
        target.Hp -= dmg;
        Events.Add(new SimEvent(SimEventType.Damage, p.Id, target.Id, dmg, isCrit ? "crit" : ""));
        if (skill is not null && p.MasteryFor(skill).AddHit())
            Events.Add(new SimEvent(SimEventType.MasteryUp, p.Id, p.Id, p.MasteryFor(skill).Level, skill));
        if (spell is not null && spell.Slow > 0) { target.Flags |= EntityFlags.Slowed; }
        if (target.Hp <= 1)
        {
            target.Hp = 1;
            EndDuel(duel, p, target);
        }
        return dmg;
    }

    private void TickDuels(float dt)
    {
        foreach (var d in _duels.ToList())
        {
            d.Elapsed += dt;
            var arena = Zone.Arena!;
            if (!arena.Contains(d.A.Pos.X, d.A.Pos.Y)) EndDuel(d, d.B, d.A);
            else if (!arena.Contains(d.B.Pos.X, d.B.Pos.Y)) EndDuel(d, d.A, d.B);
            else if (d.Elapsed >= DuelMaxSeconds + DuelCountdown) EndDuel(d, null, null);
        }
    }

    /// <summary>Ends a duel. A null winner is a draw. Winners are paid by the server (scroll rules live there).</summary>
    private void EndDuel(Duel d, Entity? winner, Entity? loser)
    {
        if (!_duels.Remove(d)) return;
        d.A.Flags &= ~(EntityFlags.Dueling | EntityFlags.Slowed);
        d.B.Flags &= ~(EntityFlags.Dueling | EntityFlags.Slowed);
        d.A.Hp = System.Math.Max(d.A.Hp, d.A.MaxHp / 2);
        d.B.Hp = System.Math.Max(d.B.Hp, d.B.MaxHp / 2);
        Events.Add(winner is null
            ? new SimEvent(SimEventType.DuelEnded, d.A.Id, d.B.Id, 0, "draw")
            : new SimEvent(SimEventType.DuelEnded, winner.Id, loser!.Id, 1, "win"));
    }

    private void ForfeitDuel(Entity p)
    {
        var d = _duels.FirstOrDefault(d => d.A == p || d.B == p);
        if (d is not null) EndDuel(d, d.A == p ? d.B : d.A, p);
    }
}
