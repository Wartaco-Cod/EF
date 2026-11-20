using TacoFactory.Core.Content;
using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;

namespace TacoFactory.Core.Tests;

public class Phase4Tests
{
    private static readonly GameData Data = GameData.Load(ContentRoot.Locate(AppContext.BaseDirectory)!);

    [Fact]
    public void Every_zone_is_reachable_from_the_first_town()
    {
        var seen = new HashSet<string>();
        var queue = new Queue<string>([Data.Town.Id]);
        while (queue.Count > 0)
        {
            var z = queue.Dequeue();
            if (!seen.Add(z)) continue;
            foreach (var e in Data.Zones[z].Exits) queue.Enqueue(e.To);
        }
        // The world boss muster and its shore are reached through the Herald, not by walking.
        Assert.Equal(Data.Zones.Values.Count(z => !z.Prologue && !z.Muster && !z.WorldBoss), seen.Count);
        // Every exit has a way back (the prologue is one way: out of the fire; the muster is left on foot).
        foreach (var z in Data.Zones.Values.Where(z => !z.Prologue && !z.Muster))
            foreach (var e in z.Exits)
                Assert.Contains(Data.Zones[e.To].Exits, back => back.To == z.Id);
    }

    [Fact]
    public void Town_has_npcs_no_monsters_and_never_clears()
    {
        var w = new World(Data, Data.Town.Id, 1);
        w.AddPlayer("Kay", ClassId.Knight);
        for (int i = 0; i < 40; i++) w.Tick(0.05f);
        Assert.Equal(Data.Town.Npcs.Count, w.Entities.Count(e => e.Kind == EntityKind.Npc));
        Assert.Equal(0, w.MonstersAlive);
        Assert.False(w.Cleared);
    }

    [Fact]
    public void Duel_runs_to_a_winner_without_anyone_dying()
    {
        var w = new World(Data, Data.Town.Id, 2);
        var a = w.AddPlayer("Kay", ClassId.Knight, level: 10);
        var b = w.AddPlayer("Merle", ClassId.Wizard, level: 2);
        w.Tick(0.05f);
        Assert.Null(w.StartDuel(a, b));
        Assert.NotNull(w.StartDuel(a, b)); // already dueling
        SimEvent? end = null;
        for (int i = 0; i < 20 * 120 && end is null; i++)
        {
            // Both walk towards each other and attack.
            w.SetInput(a.Id, new PlayerInput(Vec2.Zero, PlayerAction.UseSkill, 0, b.Id, b.Pos));
            w.SetInput(b.Id, new PlayerInput(Vec2.Zero, PlayerAction.UseSkill, 0, a.Id, a.Pos));
            w.Tick(0.05f);
            end = w.Events.Cast<SimEvent?>().FirstOrDefault(e => e!.Value.Type == SimEventType.DuelEnded);
        }
        Assert.NotNull(end);
        Assert.Equal(a.Id, end!.Value.Source);
        Assert.False(a.Dead);
        Assert.False(b.Dead);
        Assert.False(w.InDuel(a));
    }

    [Fact]
    public void Leaving_the_ring_forfeits()
    {
        var w = new World(Data, Data.Town.Id, 3);
        var a = w.AddPlayer("Kay", ClassId.Knight);
        var b = w.AddPlayer("Merle", ClassId.Wizard);
        w.Tick(0.05f);
        w.StartDuel(a, b);
        a.Pos = new Vec2(Data.Town.Width / 2, 200);
        w.Tick(0.05f);
        var end = w.Events.Single(e => e.Type == SimEventType.DuelEnded);
        Assert.Equal(b.Id, end.Source);
    }

    [Fact]
    public void Imbert_goes_through_three_phases_and_falls_to_a_full_group()
    {
        var w = new World(Data, "catacombs_of_the_rule", 1307);
        var rng = new Random(5);
        var bots = new List<(Entity P, BotBrain B)>();
        for (int i = 0; i < 6; i++)
        {
            var cls = Classes.All[i % 4];
            var p = w.AddPlayer($"bot{i}", cls, 20);
            foreach (var slot in Enum.GetValues<ItemSlot>())
            {
                if (slot == ItemSlot.Shield && !p.Equipped.ContainsKey(ItemSlot.Shield)) continue;
                p.Equipped[slot] = Loot.MakeItem(rng, 0, 20, Rarity.Rare, slot, slot == ItemSlot.Weapon ? w.WeaponOf(p) : null);
            }
            p.BuildPoints += 6;
            var brain = new BotBrain(cls);
            brain.SetExplore(w.Zone.Width, w.Zone.Height);
            brain.SpendAndRefresh(w, p);
            bots.Add((p, brain));
        }
        var phases = new HashSet<int>();
        bool imbertKilled = false, telegraphed = false;
        for (int tick = 0; tick < 20 * 60 * 30 && !imbertKilled; tick++)
        {
            var view = BotBrain.ViewOf(w);
            foreach (var (p, b) in bots) w.SetInput(p.Id, b.Decide(p.Id, view, 0.05f));
            w.Tick(0.05f);
            phases.Add(w.BossPhase);
            foreach (var e in w.Events)
            {
                if (e.Type == SimEventType.UniqueKilled && e.Text == "imbert") imbertKilled = true;
                if (e.Type == SimEventType.Telegraph) telegraphed = true;
            }
        }
        Assert.True(imbertKilled, $"Imbert still standing, phase {w.BossPhase}, {w.BossEntity?.Hp}/{w.BossEntity?.MaxHp} hp");
        Assert.Contains(2, phases);
        Assert.Contains(3, phases);
        Assert.True(telegraphed, "no Anathema was thrown in phase 2");
        Assert.InRange(w.Time, 60, 1800); // a real fight: at least a minute
        Assert.Contains(w.Entities, e => e.Kind == EntityKind.Loot && e.Item?.Rarity == Rarity.Epic);
    }
}
