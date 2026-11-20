using System.Diagnostics;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;
using TacoFactory.Net;

/// <summary>
/// End-to-end check of the town economy over the network, with two characters seeded into the
/// server's save folder: merchant buy/sell/potions/gamble, the Forge, the personal stash, the auction
/// house (post, search, buy, cancel), a trade window and a guild with its hall and shared strongbox.
/// </summary>
public static class EconomyTest
{
    private sealed class Agent(ClientConnection c)
    {
        public readonly ClientConnection C = c;
        public Vec2? Goal;
        public readonly Dictionary<string, string> Ui = new();
        public readonly List<string> Said = [];
        public Vec2 Pos => C.Entities.TryGetValue(C.YourId, out var e) ? e.Current.Pos : default;
        public T? Last<T>(string kind) where T : class => Ui.TryGetValue(kind, out var j) ? UiKind.Deserialize<T>(j) : null;
        public int Gold => C.Stats?.Gold ?? 0;
        public List<ItemInfo> Bag => C.Stats?.Inventory ?? [];
    }

    private static readonly List<string> Log = [];
    private static readonly List<(string Check, bool Ok)> Checks = [];
    private static Agent[] _all = [];

    /// <summary>Writes the two test characters into the save folder the server will use.</summary>
    public static void Seed(string saves)
    {
        var store = new CharacterStore(saves);
        Item Sword(long id, string prop, int v, Rarity r) =>
            new(id, "Arming Sword", ItemSlot.Weapon, r, 8, 14, 0, WeaponType.OneHandedSword) { Properties = new() { [prop] = v } };
        store.Save(new CharacterRecord
        {
            Name = "Merchant Odo", Class = ClassId.Knight, Level = 8, Gold = 20000,
            QuestsDone = ["q1_bloodied_fields", "q2_burned_village", "q3_smiths_hammer"], Unlocks = ["forge"],
            Extras = new PlayerExtras
            {
                Runes = [new Rune { Id = 800001, Kind = "might", Level = Runes.MaxLevel }, new Rune { Id = 800002, Kind = "might", Level = Runes.MaxLevel },
                         new Rune { Id = 800003, Kind = "flame", Level = 3 }],
                Materials = new() { [Materials.Whetstone] = 12, [Materials.HolyOil] = 2 },
            },
            Inventory = [Sword(900001, "strength", 4, Rarity.Rare), Sword(900002, "fire", 6, Rarity.Uncommon),
                         new(900003, "Kettle Helm", ItemSlot.Helm, Rarity.Rare, 7, 0, 9, WeaponType.None) { Properties = new() { ["health"] = 5 } },
                         new(900004, "Heater Shield", ItemSlot.Shield, Rarity.Uncommon, 6, 0, 12, WeaponType.None)],
        });
        store.Save(new CharacterRecord { Name = "Merchant Bree", Class = ClassId.Healer, Level = 6, Gold = 6000 });
    }

    /// <summary>Demo only: fills the auction house with a market of rolled items from invented sellers.</summary>
    public static void SeedMarket(string saves, int count = 40)
    {
        var rng = new Random(1307);
        string[] sellers = ["Aimery", "Bertrand", "Clemence", "Durand", "Esclarmonde", "Foulques", "Guiraud", "Heloise"];
        var now = DateTime.UtcNow;
        var listings = Enumerable.Range(0, count).Select(i =>
        {
            int level = rng.Next(1, 16);
            var item = Loot.MakeItem(rng, Loot.NewId(), level, Loot.RollRarity(rng, Rarity.Legendary, Rarity.Uncommon));
            var posted = now - TimeSpan.FromMinutes(rng.Next(5, 2800));
            return new
            {
                Id = (long)(i + 1), Seller = sellers[rng.Next(sellers.Length)], Item = item,
                Price = (int)(Loot.Value(item) * (0.8 + rng.NextDouble())), PostedUtc = posted, ExpiresUtc = posted + TimeSpan.FromHours(48),
            };
        }).ToList();
        File.WriteAllText(Path.Combine(saves, "auctions.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Listings = listings, Mailbox = new Dictionary<string, object>() }));
    }

    public static int Run(string host, int port)
    {
        var a = new Agent(new ClientConnection("Merchant Odo", ClassId.Knight));
        var b = new Agent(new ClientConnection("Merchant Bree", ClassId.Healer));
        _all = [a, b];
        a.C.Connect(host, port);
        b.C.Connect(host, port);
        try
        {
            if (!Pump(() => a.C.Stats is not null && b.C.Stats is not null && a.C.Entities.ContainsKey(a.C.YourId), 15)) return Fail("join");
            Check("seeded gold loaded", a.Gold == 20000);

            // ---- merchant
            Talk(a, "Eleanor of Troyes", "shop");
            var shop = a.Last<ShopState>(UiKind.Shop);
            Check("merchant shows stock", shop is { Stock.Count: > 0 });
            if (shop is { Stock.Count: > 0 })
            {
                var offer = shop.Stock[0];
                int g0 = a.Gold;
                a.C.Command("buy", offer.Item.Id.ToString());
                Pump(() => a.Bag.Any(i => i.Id == offer.Item.Id), 5);
                Check($"bought {offer.Item.Name} for {offer.Price}", a.Gold == g0 - offer.Price && a.Bag.Any(i => i.Id == offer.Item.Id));
                int g1 = a.Gold;
                a.C.Command("sell", offer.Item.Id.ToString());
                Pump(() => a.Bag.All(i => i.Id != offer.Item.Id), 5);
                Check("sold it back for a quarter", a.Gold == g1 + Math.Max(1, offer.Price / 4));
            }
            var before = a.Bag.Select(i => i.Id).ToHashSet();
            int gG = a.Gold;
            a.C.Command("gamble", "Helm");
            Pump(() => a.Bag.Count > before.Count, 5);
            var won = a.Bag.Where(i => !before.Contains(i.Id)).ToList();
            Note($"gambled: {string.Join(", ", won.Select(i => $"{i.Rarity} {i.Slot} {i.Name}"))}");
            Check("gamble gave an Uncommon+ helm", won is [{ Slot: ItemSlot.Helm, Rarity: >= Rarity.Uncommon }] && a.Gold < gG);

            Talk(a, "Sister Ysabeau", "shop");
            int hp0 = a.C.Stats!.HealthPotions;
            a.C.Command("potion", "health", "5");
            Pump(() => a.C.Stats!.HealthPotions > hp0, 5);
            Check("bought 5 health potions", a.C.Stats!.HealthPotions == hp0 + 5);
            a.C.Command("drink", "health");
            Pump(() => a.C.Stats!.HealthPotions < hp0 + 5, 5);
            Check("drank one", a.C.Stats!.HealthPotions == hp0 + 4);

            // ---- the forge room (west door of the Sanctuary)
            int fg0 = a.C.ZoneGeneration;
            Walk(a, new Vec2(120, 820), until: () => a.C.ZoneGeneration != fg0, timeout: 30);
            Pump(() => a.C.Welcome?.ZoneId == "the_forge" && a.C.Entities.ContainsKey(a.C.YourId), 5);
            Check("walked into the forge room", a.C.Welcome?.ZoneId == "the_forge");
            Talk(a, "Master Aubry", "forge");
            a.C.Command("forge", "900001", "900002", "1");
            Pump(() => a.Bag.Any(i => i.Forged), 5);
            var forged = a.Bag.FirstOrDefault(i => i.Forged);
            Check("forge combined two swords", forged is not null && forged.Props.ContainsKey("strength") && forged.Props.ContainsKey("fire")
                                               && forged.Rarity == Rarity.Rare && a.Bag.All(i => i.Id is not 900001 and not 900002));
            Check("forge played its cutscene", a.Ui.ContainsKey(UiKind.ForgeFx));
            Note($"forged: {forged?.Rarity} {forged?.Name} i{forged?.ItemLevel} {string.Join(",", forged?.Props ?? [])}");
            a.C.Command("temper", forged!.Id.ToString());
            Pump(() => a.Bag.Any(i => i.Upgrade == 1), 5);
            Check("tempered the forged sword to +1", a.Bag.Any(i => i.Forged && i.Upgrade == 1));
            var tempered = a.Bag.First(i => i.Forged);
            forged = tempered;
            a.C.Command("rune.socket", "800003", tempered.Id.ToString());
            Pump(() => a.Bag.Any(i => i.SocketList.Any(r => r?.Id == 800003)), 5);
            Check("set a rune in the sword", a.Bag.Any(i => i.SocketList.Any(r => r?.Id == 800003) && i.Total("fire") > i.Props.GetValueOrDefault("fire")));
            a.C.Command("rune.unsocket", tempered.Id.ToString(), "0");
            Pump(() => a.Last<ExtrasState>(UiKind.Extras)?.Runes.Any(r => r.Id == 800003) == true, 5);
            Check("took the rune out again", a.Last<ExtrasState>(UiKind.Extras)?.Runes.Any(r => r.Id == 800003) == true);
            Talk(a, "Gilles the Runecarver", "forge");
            a.C.Command("rune.fuse", "800001", "800002");
            Pump(() => a.Last<ExtrasState>(UiKind.Extras)?.Runes.Any(r => r.Kind == "might" && r.Tier == Rarity.Uncommon) == true, 5);
            Check("fused two level 50 runes into an Uncommon one", a.Last<ExtrasState>(UiKind.Extras)?.Runes.Count(r => r.Kind == "might") == 1);
            int fg1 = a.C.ZoneGeneration;
            Walk(a, new Vec2(1000, 1210), until: () => a.C.ZoneGeneration != fg1, timeout: 30);
            Pump(() => a.C.Welcome?.ZoneId == "sanctuary_of_the_broken_seal" && a.C.Entities.ContainsKey(a.C.YourId), 5);
            Check("back in the Sanctuary", a.C.Welcome?.ZoneId == "sanctuary_of_the_broken_seal");

            // ---- stash
            Talk(a, "The Order's Strongbox", "stash");
            a.C.Command("stash.put", "900004");
            Pump(() => a.Last<StashState>(UiKind.Stash)?.Items.Any(i => i.Id == 900004) == true, 5);
            Check("stored the shield", a.Last<StashState>(UiKind.Stash)?.Items.Count == 1 && a.Bag.All(i => i.Id != 900004));
            a.C.Command("stash.take", "900004");
            Pump(() => a.Bag.Any(i => i.Id == 900004), 5);
            Check("took it back", a.Bag.Any(i => i.Id == 900004) && a.Last<StashState>(UiKind.Stash)?.Items.Count == 0);

            // ---- auction house: Odo posts, Bree searches and buys
            Talk(a, "Jehan the Broker", "auction");
            Talk(b, "Jehan the Broker", "auction");
            int gPost = a.Gold;
            a.C.Command("ah.post", "900003", "800");
            a.C.Command("ah.post", "900004", "300");
            Pump(() => a.Bag.All(i => i.Id is not 900003 and not 900004), 5);
            Check("posted two listings, fee 5%", a.Gold == gPost - 40 - 15);
            a.C.Command("ah.mine");
            Pump(() => a.Last<AuctionPage>(UiKind.AuctionMine)?.Rows.Count == 2, 5);
            Check("my listings shows both", a.Last<AuctionPage>(UiKind.AuctionMine)?.Rows.Count == 2);
            b.Ui.Remove(UiKind.Auction);
            b.C.Command("ah.search", "kettle", "", "", "1", "100", "price", "0", "1");
            Pump(() => b.Last<AuctionPage>(UiKind.Auction) is not null, 5);
            var page = b.Last<AuctionPage>(UiKind.Auction);
            Check("search 'kettle' finds exactly the helm", page is { Total: 1 } && page.Rows[0].Item.Name.Contains("Kettle"));
            b.Ui.Remove(UiKind.Auction);
            b.C.Command("ah.search", "", "", "", "1", "100", "price", "1", "1");
            Pump(() => b.Last<AuctionPage>(UiKind.Auction) is not null, 5);
            var sorted = b.Last<AuctionPage>(UiKind.Auction);
            Check("sort by price, highest first", sorted is { Rows.Count: 2 } && sorted.Rows[0].Price == 800 && sorted.Rows[1].Price == 300);
            int gA = a.Gold, gB = b.Gold;
            if (page is { Rows.Count: > 0 }) b.C.Command("ah.buy", page.Rows[0].ListingId.ToString());
            Pump(() => b.Bag.Any(i => i.Id == 900003) && a.Gold > gA, 5);
            Check("Bree bought the helm, Odo got 90%", b.Bag.Any(i => i.Id == 900003) && b.Gold == gB - 800 && a.Gold == gA + 720);
            var mine = a.Last<AuctionPage>(UiKind.AuctionMine);
            a.C.Command("ah.mine");
            Pump(() => a.Last<AuctionPage>(UiKind.AuctionMine) is { Rows.Count: 1 }, 5);
            var shieldRow = a.Last<AuctionPage>(UiKind.AuctionMine)?.Rows.FirstOrDefault();
            if (shieldRow is not null) a.C.Command("ah.cancel", shieldRow.ListingId.ToString());
            Pump(() => a.Bag.Any(i => i.Id == 900004), 5);
            Check("cancelled listing returns the shield", a.Bag.Any(i => i.Id == 900004));

            // ---- trade window
            a.C.SendChat("/trade Merchant Bree");
            Pump(() => b.Said.Any(t => t.Contains("wants to trade")), 5);
            b.C.SendChat("/accept");
            Pump(() => a.Last<TradeState>(UiKind.Trade) is not null && b.Last<TradeState>(UiKind.Trade) is not null, 5);
            Check("trade window opened for both", a.Last<TradeState>(UiKind.Trade) is not null && b.Last<TradeState>(UiKind.Trade) is not null);
            a.C.Command("trade.add", "900004");
            b.C.Command("trade.gold", "250");
            Pump(() => b.Last<TradeState>(UiKind.Trade) is { Theirs.Count: 1 } && a.Last<TradeState>(UiKind.Trade) is { TheirGold: 250 }, 5);
            Check("both sides see the offers", b.Last<TradeState>(UiKind.Trade)?.Theirs.Count == 1 && a.Last<TradeState>(UiKind.Trade)?.TheirGold == 250);
            gA = a.Gold; gB = b.Gold;
            a.C.Command("trade.accept");
            b.C.Command("trade.accept");
            Pump(() => b.Bag.Any(i => i.Id == 900004), 5);
            Check("trade completed: shield for 250 gold", b.Bag.Any(i => i.Id == 900004) && a.Bag.All(i => i.Id != 900004) && a.Gold == gA + 250 && b.Gold == gB - 250);

            // ---- guild, hall and guild strongbox
            gA = a.Gold;
            a.C.SendChat("/guild create Payns");
            Pump(() => a.C.Stats?.Guild == "Payns", 5);
            Check("guild founded for 2,000 gold", a.C.Stats?.Guild == "Payns" && a.Gold == gA - 2000);
            a.C.SendChat("/guild invite Merchant Bree");
            Pump(() => b.Said.Any(t => t.Contains("Payns")), 5);
            b.C.SendChat("/accept");
            Pump(() => b.C.Stats?.Guild == "Payns", 5);
            Check("Bree joined the guild", b.C.Stats?.Guild == "Payns");
            a.Ui.Remove(UiKind.Guild);
            a.C.Command("guild.info");
            Pump(() => a.Last<GuildState>(UiKind.Guild) is not null, 5);
            Check("roster lists two members", a.Last<GuildState>(UiKind.Guild)?.Members.Count == 2);
            b.C.SendChat("/g for the Order", "guild");
            Pump(() => a.C.Chat.Count > 0 || a.Said.Any(t => t.Contains("for the Order")), 3);

            int gen = a.C.ZoneGeneration;
            Walk(a, new Vec2(1200, 120), until: () => a.C.ZoneGeneration != gen, timeout: 30);
            Check("entered the guild hall", a.C.Welcome?.ZoneId == "commandery_halls");
            Pump(() => a.C.Entities.ContainsKey(a.C.YourId), 5);
            Talk(a, "The Guild Strongbox", "stash");
            var forgedId = forged?.Id ?? 0;
            a.C.Command("gstash.put", forgedId.ToString());
            Pump(() => a.Last<StashState>(UiKind.Stash) is { Guild: true, Items.Count: 1 }, 5);
            Check("guild strongbox holds the forged sword", a.Last<StashState>(UiKind.Stash) is { Guild: true, Items.Count: 1 });
        }
        finally
        {
            foreach (var l in Log) Console.WriteLine("[eco] " + l);
            foreach (var (c, ok) in Checks) Console.WriteLine($"[eco] {(ok ? "ok  " : "FAIL")} {c}");
            a.C.Dispose();
            b.C.Dispose();
        }
        bool pass = Checks.Count > 0 && Checks.All(c => c.Ok);
        Console.WriteLine(pass ? $"[eco] PASS ({Checks.Count} checks)" : "[eco] FAIL");
        return pass ? 0 : 1;
    }

    private static int Fail(string what) { Console.WriteLine($"[eco] FAIL at {what}"); return 1; }
    private static void Check(string what, bool ok) => Checks.Add((what, ok));
    private static void Note(string what) => Log.Add(what);

    /// <summary>Polls both connections, steers each toward its goal, and collects UI states and system lines.</summary>
    private static bool Pump(Func<bool> until, double timeoutSeconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            foreach (var g in _all)
            {
                g.C.Poll();
                while (g.C.Ui.TryDequeue(out var ui)) g.Ui[ui.Kind] = ui.Json;
                while (g.C.Chat.TryDequeue(out var line)) { g.Said.Add(line.Text); Log.Add($"{g.C.Name} <{line.Channel}> {line.Text}"); }
                while (g.C.Events.TryDequeue(out _)) { }
                var move = g.Goal is { } goal && Vec2.Distance(goal, g.Pos) > 40 ? (goal - g.Pos).Normalized() : default;
                if (g.Goal is { } done && move.LengthSquared == 0) g.Goal = null;
                if (g.C.Welcome is not null) g.C.SendInput(new PlayerInput(move, PlayerAction.None, 0, 0, default));
            }
            if (until()) return true;
            Thread.Sleep(30);
        }
        return until();
    }

    private static void Walk(Agent g, Vec2 to, Func<bool>? until = null, double timeout = 20)
    {
        g.Goal = to;
        Pump(until ?? (() => g.Goal is null), timeout);
        g.Goal = null;
    }

    /// <summary>Walks up to a named NPC and talks, waiting for the window it opens.</summary>
    private static void Talk(Agent g, string npcName, string uiKind)
    {
        int index = g.C.Welcome!.NpcList.IndexOf(npcName);
        var npc = g.C.Entities.Values.FirstOrDefault(e => e.Current.Kind == EntityKind.Npc && e.Current.TypeIndex == index);
        if (index < 0 || npc is null) { Check($"found {npcName}", false); return; }
        var at = npc.Current.Pos;
        Walk(g, at + (g.Pos - at).Normalized() * 90);
        g.Ui.Remove(uiKind);
        g.C.Interact(npc.Current.Id);
        bool ok = Pump(() => g.Ui.ContainsKey(uiKind), 5);
        if (!ok) Note($"{g.C.Name} at {g.Pos} could not reach {npcName} at {at} (entity {npc.Current.Id}, zone {g.C.Welcome?.ZoneId})");
        Check($"{npcName} opened the {uiKind} window", ok);
    }
}
