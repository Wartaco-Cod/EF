using System.Diagnostics;
using TacoFactory.Core.Content;
using TacoFactory.Core.Data;
using TacoFactory.Core.Sim;

/// <summary>
/// The whole game, start to finish, in the real simulation: a bot party plays every zone of every act in order,
/// keeping its levels, skills and mastery, and re-gearing at each zone as a player would from drops and shops.
/// Reports levels, deaths and time per zone, and the experience each zone gives (for tuning the level curve).
/// </summary>
public static class CampaignRun
{
    public static int Run(int groupSize, string? csv)
    {
        string assets = ContentRoot.Locate(AppContext.BaseDirectory) ?? ContentRoot.Locate(Environment.CurrentDirectory)
                        ?? throw new DirectoryNotFoundException("No assets folder found.");
        var data = GameData.Load(assets);
        var zones = data.Zones.Values.Where(z => !z.IsTown && !z.Prologue && z.PackCount > 0)
            .OrderBy(z => z.Act).ThenBy(z => z.LevelMin).ThenBy(z => z.Id).ToList();
        // TF_ONLY=zone_id plays just that zone, with the party at the zone's level (for boss tuning).
        if (Environment.GetEnvironmentVariable("TF_ONLY") is { Length: > 0 } only) zones = zones.Where(z => z.Id == only).ToList();
        var rng = new Random(7);
        var party = new List<(CharacterRecord Rec, BotBrain Brain)>();
        for (int i = 0; i < groupSize; i++)
        {
            var cls = Classes.All[i % 4];
            int startLevel = Environment.GetEnvironmentVariable("TF_ONLY") is { Length: > 0 } ? zones[0].LevelMin : 1;
            party.Add((new CharacterRecord { Name = $"bot{i}", Class = cls, Level = startLevel, BuildPoints = startLevel + startLevel / 4 }, new BotBrain(cls)));
        }
        var sw = Stopwatch.StartNew();
        var lines = new List<string> { "zone,act,levelMin,levelMax,cleared,minutes,deaths,startLevel,endLevel,xpPerPlayer" };
        Console.WriteLine($"{"Zone",-38} {"Lv",7} {"Clr",4} {"Time",6} {"Dth",4} {"Start",6} {"End",6} {"XP/player",12}");
        double totalMinutes = 0;
        foreach (var zone in zones)
        {
            var world = new World(data, zone.Id, seed: 1307 + zone.Id.GetHashCode());
            var bots = new List<(Entity P, BotBrain B)>();
            foreach (var (rec, _) in party)
            {
                var brain = new BotBrain(rec.Class); // fresh per zone: entity ids start again in every world
                var p = world.AddPlayer(rec.Name, rec.Class, rec.Level);
                rec.ApplyTo(p);
                // Gear as a player arriving here would wear it: a Rare set a little below the zone.
                var weapon = world.WeaponOf(p);
                int ilvl = System.Math.Max(1, System.Math.Min(p.Level, zone.LevelMin));
                foreach (var slot in Enum.GetValues<ItemSlot>().Where(sl => sl != ItemSlot.Pack))
                {
                    if (slot == ItemSlot.Shield && !p.Equipped.ContainsKey(ItemSlot.Shield)) continue;
                    p.Equipped[slot] = Loot.MakeItem(rng, 0, ilvl, zone.Act >= 3 ? Rarity.Elite : Rarity.Rare, slot, slot == ItemSlot.Weapon ? weapon : null) with { Slot = slot };
                }
                world.RefreshPlayer(p);
                p.Hp = p.MaxHp; p.Mana = p.MaxMana;
                brain.SpendAndRefresh(world, p);
                brain.SetExplore(zone.Width, zone.Height);
                bots.Add((p, brain));
            }
            world.Populate(groupSize);
            int startLevel = (int)bots.Average(b => b.P.Level);
            long xp0 = bots.Sum(b => b.P.LifetimeXp);
            const float dt = 0.05f;
            for (int tick = 0; tick < 20 * 60 * 40 && !world.Cleared; tick++)
            {
                var view = BotBrain.ViewOf(world);
                foreach (var (p, b) in bots)
                {
                    if (tick % 20 == 0) b.SpendAndRefresh(world, p);
                    world.SetInput(p.Id, b.Decide(p.Id, view, dt));
                }
                world.Tick(dt);
                if (world.YieldedBoss is not null) world.ResolveYield(tick % 2 == 0 ? "spare" : "kill", bots[0].P.Id);
            }
            if (!world.Cleared && Environment.GetEnvironmentVariable("TF_DEBUG") is not null)
                foreach (var m in world.Entities.Where(e => e.Kind == EntityKind.Monster && !e.Dead && (e.Flags & EntityFlags.Ally) == 0).Take(8))
                    Console.WriteLine($"    left: {m.DisplayName} L{m.Level} at {m.Pos.X:F0},{m.Pos.Y:F0} hp {m.Hp}/{m.MaxHp} {m.State} {m.Flags}; players at "
                                      + string.Join(" ", bots.Select(b => $"{b.P.Pos.X:F0},{b.P.Pos.Y:F0}{(b.P.Dead ? "(dead)" : "")}")));
            long xpEach = (bots.Sum(b => b.P.LifetimeXp) - xp0) / System.Math.Max(1, bots.Count);
            int deaths = bots.Sum(b => b.P.Deaths);
            double minutes = (world.ClearedAt ?? world.Time) / 60;
            totalMinutes += minutes;
            string name = zone.Name + (zone.Floor is string f ? $" ({f})" : "");
            Console.WriteLine($"{name,-38} {zone.LevelMin,3}-{zone.LevelMax,-3} {(world.Cleared ? "yes" : "NO"),4} {minutes,5:F1}m {deaths,4} {startLevel,6} {bots.Average(b => b.P.Level),6:F1} {xpEach,12:N0}");
            lines.Add($"{zone.Id},{zone.Act},{zone.LevelMin},{zone.LevelMax},{world.Cleared},{minutes:F2},{deaths},{startLevel},{bots.Average(b => b.P.Level):F2},{xpEach}");
            for (int i = 0; i < party.Count; i++)
            {
                var p = bots[i].P;
                var rec = CharacterRecord.From(p);
                rec.Deaths = 0;
                party[i] = (rec, party[i].Brain);
                party[i].Rec.Equipped.Clear();
                party[i].Rec.Inventory.Clear(); // sold in town between zones
            }
        }
        Console.WriteLine($"Campaign of {groupSize}: {totalMinutes / 60:F1} hours of play, final level {party.Average(p => p.Rec.Level):F1} ({sw.Elapsed.TotalSeconds:F0}s real)");
        if (csv is not null) File.WriteAllLines(csv, lines);
        return 0;
    }
}
