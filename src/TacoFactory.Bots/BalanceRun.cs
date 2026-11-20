using System.Diagnostics;
using TacoFactory.Core.Content;
using TacoFactory.Core.Data;
using TacoFactory.Core.Sim;

/// <summary>
/// Balance runs: a 6-bot group plays each zone inside the real simulation at full speed (no network)
/// and reports whether it cleared, how long it took and how often bots died.
/// </summary>
public static class BalanceRun
{
    /// <summary>Levels above the zone minimum the bots arrive with (TF_LEVEL_BONUS), e.g. to model a solo player who outlevels zones.</summary>
    private static int LevelBonus => int.TryParse(Environment.GetEnvironmentVariable("TF_LEVEL_BONUS"), out var b) ? b : 0;

    public static int Run(string which, int groupSize = 6)
    {
        string assets = ContentRoot.Locate(AppContext.BaseDirectory) ?? ContentRoot.Locate(Environment.CurrentDirectory)
                        ?? throw new DirectoryNotFoundException("No assets folder found.");
        var data = GameData.Load(assets);
        // Sweep knob for tuning: TF_DIFFICULTY="damageBase,damagePerLevel,healthBase,healthPerLevel,groupHealth,groupDamage"
        if (Environment.GetEnvironmentVariable("TF_DIFFICULTY") is { Length: > 0 } knobs)
        {
            var v = knobs.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            (Difficulty.DamageBase, Difficulty.DamagePerLevel, Difficulty.HealthBase, Difficulty.HealthPerLevel, Difficulty.GroupHealthPerPlayer, Difficulty.GroupDamagePerPlayer) = (v[0], v[1], v[2], v[3], v[4], v[5]);
        }
        var zones = which == "all"
            ? data.Zones.Values.Where(z => !z.IsTown).OrderBy(z => z.LevelMin).ThenBy(z => z.Id).ToList()
            : [data.Zones[which]];

        Console.WriteLine($"{"Zone",-30} {"Lv",5} {"Foes",5} {"Cleared",8} {"Time",7} {"Deaths",7} {"End lv",7}");
        int failures = 0;
        foreach (var zone in zones)
        {
            var world = new World(data, zone.Id, seed: 1307);
            var rng = new Random(13);
            var bots = new List<(Entity P, BotBrain B)>();
            for (int i = 0; i < groupSize; i++)
            {
                var cls = Classes.All[i % 4];
                var p = world.AddPlayer($"bot{i}", cls, zone.LevelMin + LevelBonus);
                // Gear a player arriving at this zone would plausibly wear, keeping the class's weapon type.
                var weapon = world.WeaponOf(p);
                foreach (var slot in Enum.GetValues<ItemSlot>().Where(sl => sl != ItemSlot.Pack))
                {
                    if (slot == ItemSlot.Shield && !p.Equipped.ContainsKey(ItemSlot.Shield)) continue;
                    p.Equipped[slot] = Loot.MakeItem(rng, 0, zone.LevelMin, Rarity.Uncommon, slot, slot == ItemSlot.Weapon ? weapon : null) with { Slot = slot };
                }
                // Build points a player would have by now: one per level plus about one per quest done.
                p.BuildPoints += zone.LevelMin / 3;
                var brain = new BotBrain(cls);
                brain.SpendAndRefresh(world, p);
                brain.SetExplore(zone.Width, zone.Height);
                bots.Add((p, brain));
            }
            world.Populate();
            int foes = world.MonstersAlive;
            var sw = Stopwatch.StartNew();
            const float dt = 0.05f;
            for (int tick = 0; tick < 20 * 60 * 30 && !world.Cleared; tick++) // 30 minutes of game time
            {
                var view = BotBrain.ViewOf(world);
                foreach (var (p, b) in bots)
                {
                    if (tick % 20 == 0) b.SpendAndRefresh(world, p);
                    world.SetInput(p.Id, b.Decide(p.Id, view, dt));
                }
                world.Tick(dt);
            }
            int deaths = bots.Sum(b => b.P.Deaths);
            if (!world.Cleared) failures++;
            string time = world.Cleared ? $"{world.ClearedAt / 60:F1}m" : "-";
            Console.WriteLine($"{zone.Name,-30} {zone.LevelMin,2}-{zone.LevelMax,-2} {foes,5} {(world.Cleared ? "yes" : "NO"),8} {time,7} {deaths,7} {bots.Average(b => b.P.Level),7:F1}   ({sw.Elapsed.TotalSeconds:F1}s real)");
        }
        Console.WriteLine(failures == 0 ? "All zones cleared." : $"{failures} zone(s) not cleared in 30 minutes.");
        return 0;
    }
}
