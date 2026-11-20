using TacoFactory.Core.Content;
using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;

namespace TacoFactory.Core.Tests;

public class WorldTests
{
    private static readonly GameData Data = GameData.Load(ContentRoot.Locate(AppContext.BaseDirectory)!);

    [Fact]
    public void Game_data_loads_and_validates()
    {
        Assert.True(Data.Monsters.Count > 19); // Act I roster + the Grand Inquisitor + Acts II-IV
        Assert.True(Data.Zones.ContainsKey("bloodied_fields"));
    }

    [Fact]
    public void Group_size_scales_monster_health()
    {
        var solo = new World(Data, "bloodied_fields", 1);
        solo.AddPlayer("a", ClassId.Knight);
        solo.Populate();
        var six = new World(Data, "bloodied_fields", 1);
        for (int i = 0; i < 6; i++) six.AddPlayer("p" + i, Classes.All[i % 4]);
        six.Populate();
        // Compare like with like: same monster type at the same level (pack rolls differ between worlds).
        var soloHp = solo.Monsters.GroupBy(m => (m.TypeIndex, m.Level, m.Flags)).ToDictionary(g => g.Key, g => g.First().MaxHp);
        var ratios = six.Monsters.Where(m => soloHp.ContainsKey((m.TypeIndex, m.Level, m.Flags)))
                                 .Select(m => m.MaxHp / (double)soloHp[(m.TypeIndex, m.Level, m.Flags)]).ToList();
        Assert.NotEmpty(ratios);
        Assert.All(ratios, r => Assert.InRange(r, 3.3, 3.7)); // 1 + 0.5 × 5 = 3.5
    }

    [Fact]
    public void Seventh_player_is_refused()
    {
        var w = new World(Data, "bloodied_fields", 2);
        for (int i = 0; i < 6; i++) w.AddPlayer("p" + i, ClassId.Knight);
        Assert.Throws<InvalidOperationException>(() => w.AddPlayer("p7", ClassId.Knight));
    }

    [Fact]
    public void Superunique_spawns_with_escort()
    {
        var w = new World(Data, "bloodied_fields", 3);
        w.AddPlayer("a", ClassId.Knight);
        w.Populate();
        var boss = Assert.Single(w.Monsters, m => (m.Flags & EntityFlags.Superunique) != 0);
        Assert.Equal("Corbin the Bailiff", boss.DisplayName);
        Assert.NotNull(boss.GuaranteedDrop);
        Assert.Equal(ItemSlot.Weapon, boss.GuaranteedDrop!.Slot);
        Assert.True(boss.GuaranteedDrop.Rarity >= Rarity.Uncommon);
    }

    [Fact]
    public void Hits_build_mastery_on_the_skill_used()
    {
        var w = new World(Data, "bloodied_fields", 4);
        var k = w.AddPlayer("knight", ClassId.Knight);
        w.Populate();
        var target = w.Monsters.OrderBy(m => Vec2.Distance(m.Pos, k.Pos)).First();
        k.Pos = target.Pos + new Vec2(40, 0);
        for (int i = 0; i < 40 && !target.Dead; i++)
        {
            w.SetInput(k.Id, new PlayerInput(Vec2.Zero, PlayerAction.UseSkill, 0, target.Id, target.Pos));
            w.Tick(0.05f);
        }
        Assert.True(k.MasteryFor("one_handed_sword").Hits > 0 || k.MasteryFor("one_handed_sword").Level > 0);
    }

    [Fact]
    public void Six_bot_group_clears_the_bloodied_fields()
    {
        var w = new World(Data, "bloodied_fields", 2026);
        var bots = new List<(Entity P, BotBrain B)>();
        for (int i = 0; i < 6; i++)
        {
            var cls = Classes.All[i % 4];
            var brain = new BotBrain(cls);
            brain.SetExplore(w.Zone.Width, w.Zone.Height);
            bots.Add((w.AddPlayer($"bot{i}", cls), brain));
        }
        const float dt = 0.05f;
        for (int tick = 0; tick < 20 * 60 * 20 && !w.Cleared; tick++) // up to 20 minutes of game time
        {
            var view = BotBrain.ViewOf(w);
            foreach (var (p, b) in bots)
            {
                if (tick % 20 == 0) b.SpendAndRefresh(w, p);
                w.SetInput(p.Id, b.Decide(p.Id, view, dt));
            }
            w.Tick(dt);
        }
        Assert.True(w.Cleared, $"{w.MonstersAlive} monsters left after {w.Time:F0}s");
        Assert.All(bots, b => Assert.True(b.P.Level >= 2));
    }
}
