using TacoFactory.Core.Data;
using TacoFactory.Core.Math;

namespace TacoFactory.Core.Sim;

/// <summary>What a bot can see of one entity. Built from the world in tests and from snapshots over the network.</summary>
public readonly record struct BotView(int Id, EntityKind Kind, Vec2 Pos, int Hp, int MaxHp, bool Dead, ClassId Class, EntityFlags Flags = EntityFlags.None)
{
    public bool Hostile => Kind == EntityKind.Monster && (Flags & EntityFlags.Ally) == 0;
    public bool Immune => (Flags & EntityFlags.Invulnerable) != 0;
}

/// <summary>
/// Simple playtest bot: fights the nearest monster with its weapon and spells, heals and buffs allies,
/// revives the fallen, picks up loot and spends build points on a class build.
/// It only sends inputs, exactly like a player, so it exercises the real server rules.
/// </summary>
public sealed class BotBrain
{
    private readonly ClassId _class;
    private readonly float[] _cooldown = new float[Entity.BarSlots];
    private readonly List<Vec2> _waypoints = [];
    private int _waypoint;
    // Loot it could not pick up (full bag): ignored after a few seconds of trying.
    private readonly HashSet<int> _ignoredLoot = [];
    private int _lootId;
    private float _lootTime;

    /// <summary>The spells on this bot's bar, slot 1 first. Update when it learns new ones.</summary>
    public IReadOnlyList<SpellDef> Bar { get; set; } = [];

    public BotBrain(ClassId cls) => _class = cls;

    /// <summary>Build order per class: the bot raises whichever of these is lowest and affordable.</summary>
    public static readonly Dictionary<ClassId, string[]> Builds = new()
    {
        [ClassId.Knight] = ["one_handed_sword", "shield", "warding"],
        [ClassId.Paladin] = ["liturgy", "one_handed_sword", "shield"],
        [ClassId.Wizard] = ["alchemy", "hermetica", "warding"],
        [ClassId.Healer] = ["liturgy", "warding", "dagger"],
    };

    public static string? ChooseLearn(ClassId cls, Func<string, int> level, int buildPoints, Func<string, int> cost, int maxLevel = 10) =>
        Builds[cls].Where(s => level(s) < maxLevel && cost(s) <= buildPoints).OrderBy(level).FirstOrDefault();

    /// <summary>Gives the bot a sweep route across the zone for when no monster is in view (network bots only see nearby).</summary>
    public void SetExplore(float width, float height, float step = 1400)
    {
        _waypoints.Clear();
        bool flip = false;
        for (float x = step / 2; x < width; x += step, flip = !flip)
        {
            var col = new List<Vec2>();
            for (float y = step / 2; y < height; y += step) col.Add(new Vec2(x, y));
            if (flip) col.Reverse();
            _waypoints.AddRange(col);
        }
        _waypoint = 0;
    }

    private PlayerInput Cast(int slot, int target, Vec2 at)
    {
        _cooldown[slot] = slot == 0 ? 0 : Bar[slot - 1].Cooldown + 0.2f;
        return new PlayerInput(Vec2.Zero, PlayerAction.UseSkill, (byte)slot, target, at);
    }

    public PlayerInput Decide(int selfId, IReadOnlyList<BotView> view, float dt)
    {
        for (int i = 0; i < _cooldown.Length; i++) _cooldown[i] = System.Math.Max(0, _cooldown[i] - dt);
        BotView? self = null;
        foreach (var v in view) if (v.Id == selfId) { self = v; break; }
        if (self is not { } me || me.Dead) return default;

        // Prefer foes that can be hurt right now (Imbert is immune while his confessions stand).
        BotView? target = Nearest(view, me.Pos, EntityKind.Monster, 4000, skipImmune: true) ?? Nearest(view, me.Pos, EntityKind.Monster, 4000);
        BotView? loot = Nearest(view, me.Pos, EntityKind.Loot, 320, ignore: _ignoredLoot);
        if (loot is { } seen)
        {
            if (seen.Id != _lootId) { _lootId = seen.Id; _lootTime = 0; }
            else if ((_lootTime += dt) > 3) { _ignoredLoot.Add(seen.Id); loot = null; }
        }
        BotView? threat = Nearest(view, me.Pos, EntityKind.Monster, 260);
        int near = 0;
        foreach (var v in view) if (v.Hostile && !v.Dead && Vec2.Distance(v.Pos, me.Pos) < 260) near++;

        for (int slot = 1; slot <= Bar.Count && slot < Entity.BarSlots; slot++)
        {
            if (_cooldown[slot] > 0) continue;
            var sp = Bar[slot - 1];
            switch (sp.Kind)
            {
                case SpellKind.Revive:
                    foreach (var v in view)
                        if (v.Kind == EntityKind.Player && v.Dead && v.Id != me.Id && Vec2.Distance(v.Pos, me.Pos) < 900) return Cast(slot, v.Id, v.Pos);
                    break;
                case SpellKind.Heal:
                    if (MostHurt(view, me, 0.65f, 900) is { } hurt) return Cast(slot, hurt.Id, hurt.Pos);
                    break;
                case SpellKind.HealArea:
                    if (MostHurt(view, me, 0.55f, sp.Radius) is not null) return Cast(slot, me.Id, me.Pos);
                    break;
                case SpellKind.BuffSelf:
                case SpellKind.BuffArea:
                    if (threat is not null) return Cast(slot, me.Id, me.Pos);
                    break;
                case SpellKind.Nova:
                    if (near >= 3) return Cast(slot, 0, me.Pos);
                    break;
                case SpellKind.Bolt:
                case SpellKind.Burst:
                case SpellKind.Drain:
                    if (target is { } t && Vec2.Distance(t.Pos, me.Pos) < 900) return Cast(slot, t.Id, t.Pos);
                    break;
            }
        }

        if (loot is { } l && threat is null)
            return new PlayerInput((l.Pos - me.Pos).Normalized(), PlayerAction.None, 0, 0, l.Pos);

        if (target is not { } tgt)
        {
            if (_waypoints.Count == 0) return default;
            var goal = _waypoints[_waypoint % _waypoints.Count];
            if (Vec2.Distance(goal, me.Pos) < 160) { _waypoint++; goal = _waypoints[_waypoint % _waypoints.Count]; }
            return new PlayerInput((goal - me.Pos).Normalized(), PlayerAction.None, 0, 0, goal);
        }
        return new PlayerInput(Vec2.Zero, PlayerAction.UseSkill, 0, tgt.Id, tgt.Pos);
    }

    private static BotView? MostHurt(IReadOnlyList<BotView> view, BotView me, float below, float range)
    {
        BotView? hurt = null;
        float worst = below;
        foreach (var v in view)
        {
            if (v.Kind != EntityKind.Player || v.Dead || v.MaxHp == 0) continue;
            float frac = v.Hp / (float)v.MaxHp;
            if (frac < worst && Vec2.Distance(v.Pos, me.Pos) < range) { worst = frac; hurt = v; }
        }
        return hurt;
    }

    private static BotView? Nearest(IReadOnlyList<BotView> view, Vec2 from, EntityKind kind, float range, bool skipImmune = false, HashSet<int>? ignore = null)
    {
        BotView? best = null;
        float bestD = range;
        foreach (var v in view)
        {
            if (v.Kind != kind || v.Dead || ignore?.Contains(v.Id) == true) continue;
            if (kind == EntityKind.Monster && (!v.Hostile || (skipImmune && v.Immune))) continue;
            float d = Vec2.Distance(v.Pos, from);
            if (d < bestD) { bestD = d; best = v; }
        }
        return best;
    }

    public static List<BotView> ViewOf(World world)
    {
        var list = new List<BotView>(world.Entities.Count);
        foreach (var e in world.Entities) list.Add(new BotView(e.Id, e.Kind, e.Pos, e.Hp, e.MaxHp, e.Dead, e.Class, e.Flags));
        return list;
    }

    /// <summary>In-process helper: spend build points on the class build and refresh the bar.</summary>
    public void SpendAndRefresh(World world, Entity p)
    {
        while (ChooseLearn(_class, p.SkillLevel, p.BuildPoints, s => world.Data.LearnCost(_class.ToString(), s), world.Data.SkillRules.MaxSkillLevel) is string s
               && world.LearnSkill(p, s) == World.LearnResult.Learned) { }
        Bar = p.SpellBar.Select(id => world.Data.Spells[id]).ToList();
    }
}
