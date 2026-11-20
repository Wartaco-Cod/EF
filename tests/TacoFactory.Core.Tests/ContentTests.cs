using TacoFactory.Core.Content;
using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;
using Xunit;

namespace TacoFactory.Core.Tests;

/// <summary>The four acts hang together: towns, level bands, quest chains, bosses and choices.</summary>
public class ContentTests
{
    private static readonly GameData Data = GameData.Load(ContentRoot.Locate(AppContext.BaseDirectory)!);

    [Fact]
    public void Four_acts_each_with_a_town_arena_and_services()
    {
        Assert.Equal(4, Data.Acts);
        for (int act = 1; act <= 4; act++)
        {
            var town = Data.TownOf(act);
            Assert.Equal(act, town.Act);
            Assert.NotNull(town.Arena);
            var services = town.Npcs.SelectMany(n => n.Services).ToHashSet();
            foreach (var needed in new[] { "main_quest", "buy", "heal", "forge", "stash", "auction" })
                Assert.Contains(needed, services);
        }
    }

    [Fact]
    public void Level_bands_climb_to_one_hundred()
    {
        Assert.Equal(100, Data.Zones.Values.Max(z => z.LevelMax));
        for (int act = 2; act <= 4; act++)
            Assert.True(Data.Zones.Values.Where(z => z.Act == act && !z.IsTown).Min(z => z.LevelMin)
                        > Data.Zones.Values.Where(z => z.Act == act - 1 && !z.IsTown).Max(z => z.LevelMin));
    }

    [Fact]
    public void Each_act_ends_with_a_quest_that_opens_the_next()
    {
        foreach (var (act, unlock) in new[] { (1, "act2"), (2, "act3"), (3, "act4") })
            Assert.Contains(Data.Quests, q => q.Act == act && q.Reward.Unlock == unlock);
        Assert.Contains(Data.Quests, q => q.Objective.Target == "philip_iv");
        // Every main quest's prerequisite exists, and every kill target is placed in its zone.
        foreach (var q in Data.Quests)
        {
            if (q.After is not null) Assert.Contains(Data.Quests, x => x.Id == q.After);
            if (q.Objective.Type == ObjectiveType.KillUnique && q.Objective.Target != "imbert")
            {
                var z = Data.Zones[q.Zone];
                Assert.True(z.Superunique == q.Objective.Target || z.Boss == q.Objective.Target, $"{q.Id}: {q.Objective.Target} is not in {q.Zone}");
            }
        }
    }

    [Fact]
    public void Choices_give_story_unlocks_and_scripted_bosses_spawn()
    {
        foreach (var b in Data.Bosses.Values.Where(b => b.Choice is not null))
        {
            Assert.True(b.YieldAt > 0);
            Assert.All(b.Choice!.Options, o => Assert.False(string.IsNullOrEmpty(o.Unlock)));
        }
        var zone = Data.Zones.Values.First(z => z.Boss == "reginald");
        var w = new World(Data, zone.Id, 3);
        w.AddPlayer("Kay", ClassId.Knight, 30, new Vec2(zone.BossPosition![0] - 300, zone.BossPosition[1]));
        w.Populate(1);
        Assert.NotNull(w.ScriptedBoss);
        for (int i = 0; i < 40; i++) w.Tick(0.05f);
        Assert.Contains(w.Entities, e => e.UniqueId == "reginald");
    }

    [Fact]
    public void The_prologue_starts_with_three_soldiers()
    {
        var court = Data.Zones.Values.Single(z => z.Prologue);
        var w = new World(Data, court.Id, 1);
        w.AddPlayer("Kay", ClassId.Knight);
        w.Populate(1);
        Assert.Equal(3, w.MonstersAlive);
    }

    [Theory]
    [InlineData("reginald_killed,arnaud_killed,clement_condemned,proof_secured,thomas_trust", "darkness")]
    [InlineData("reginald_spared,arnaud_spared,clement_spared,proof_secured,thomas_trust", "rescue")]
    [InlineData("reginald_killed,arnaud_spared,clement_spared,proof_secured,thomas_trust", "rescue")]
    [InlineData("reginald_spared,arnaud_spared,clement_condemned", "vengeance")]
    [InlineData("reginald_spared,arnaud_killed,clement_spared", "redemption")]
    public void Endings_follow_the_players_choices(string unlocks, string ending) =>
        Assert.Equal(ending, Story.Ending(unlocks.Split(',').ToHashSet()));

    [Fact]
    public void Every_ending_and_cutscene_exists()
    {
        foreach (var e in Story.Endings) Assert.True(Data.Cutscenes.ContainsKey("ending_" + e));
        foreach (var c in Data.Cutscenes.Values) Assert.NotEmpty(c.Lines);
    }

    [Fact]
    public void Zone_atmosphere_is_valid_and_keeps_ways_clear()
    {
        var data = GameData.Load(ContentRoot.Locate(AppContext.BaseDirectory)!);
        string[] air = ["", "embers", "dust", "snow", "ash", "fireflies", "spores", "rain"];
        foreach (var z in data.Zones.Values)
        {
            if (z.Ambience is { } a)
            {
                foreach (var hex in new[] { a.Ambient, a.Tint, a.Dust }) Assert.Matches("^#[0-9A-Fa-f]{6}$", hex);
                Assert.InRange(a.Level, 0.15f, 1.3f);
                Assert.Contains(a.Particles, air);
            }
            // Candelabra and fires never stand on an exit, the waypoint or where heroes arrive.
            var keep = z.Exits.Select(e => (e.X, e.Y)).Append((z.PlayerSpawn[0], z.PlayerSpawn[1])).ToList();
            if (z.Waypoint is { } wp) keep.Add((wp[0], wp[1]));
            foreach (var d in z.Decor.Where(d => d.Prop.StartsWith("candelabrum")))
                foreach (var (x, y) in keep)
                    Assert.True(MathF.Sqrt((d.X - x) * (d.X - x) + (d.Y - y) * (d.Y - y)) > 150, $"{z.Id}: candelabrum at {d.X},{d.Y} blocks {x},{y}");
            foreach (var l in z.Lights) Assert.Matches("^#[0-9A-Fa-f]{6}$", l.Light.Colour);
        }
        Assert.NotNull(data.Props["candelabrum_1"].Light);
    }

    [Fact]
    public void Every_base_item_can_be_made_by_name()
    {
        var rng = new Random(1);
        foreach (var n in Loot.BaseNames)
        {
            var it = Loot.MakeNamed(rng, n, 5, Rarity.Rare);
            Assert.Equal(n, it.Name);
            Assert.Equal(2, it.Properties.Count);
            Assert.Equal(it.Slot == ItemSlot.Weapon, it.Weapon != WeaponType.None);
        }
        Assert.Equal(ItemSlot.Helm, Loot.MakeNamed(rng, "Coif", 1, Rarity.Common).Slot);
        Assert.Equal(WeaponType.Throwing, Loot.MakeNamed(rng, "Javelins", 1, Rarity.Common).Weapon);
    }

    [Fact]
    public void Every_base_item_has_its_own_icon()
    {
        string root = ContentRoot.Locate(AppContext.BaseDirectory)!;
        var names = Loot.BaseNames.ToList();
        Assert.Equal(54, names.Count);
        foreach (var n in names)
        {
            Assert.True(File.Exists(Path.Combine(root, "ui", "icons", "items", Loot.Slug(n) + ".png")), $"no icon for {n}");
            Assert.Equal(n, Loot.BaseNameOf($"Forged {n} of Flame"));
        }
        Assert.Equal("Greatsword", Loot.BaseNameOf("Greatsword of Storms"));
        Assert.Equal("Great Helm", Loot.BaseNameOf("Great Helm"));
        Assert.Null(Loot.BaseNameOf("Ledger of Payns"));
    }
}
