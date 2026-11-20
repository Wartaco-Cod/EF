using System.Text.Json.Serialization;

namespace TacoFactory.Core.Sim;

// ======================================================================================================== runes

/// <summary>
/// A rune: socketed into gear, it adds one property. Every rune drops at Common tier, level 1, and grows by use:
/// experience from kills made while it is worn, and from the blows it strikes or turns aside. At level 50, two runes of
/// the same kind and tier are fused at the forge into one of the next tier, back at level 1.
/// </summary>
public sealed class Rune
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public Rarity Tier { get; set; }
    public int Level { get; set; } = 1;
    public long Xp { get; set; }
}

public sealed record RuneKind(string Id, string Name, string Property, double Base, string Flavour);

public static class Runes
{
    public const int MaxLevel = 50, PouchLimit = 60;

    public static readonly RuneKind[] Kinds =
    [
        new("might", "Rune of Might", "strength", 1.0, "A smith's mark cut deep into the steel."),
        new("vigour", "Rune of Vigour", "health", 1.5, "Carved by a field surgeon who lost no one at Acre."),
        new("haste", "Rune of Haste", "dexterity", 0.6, "The courier's sign, scratched on stirrups."),
        new("wisdom", "Rune of Wisdom", "intelligence", 1.5, "Copied from a margin in the Order's library."),
        new("flame", "Rune of Flame", "fire", 3.0, "Still warm from the pyre."),
        new("frost", "Rune of Frost", "freeze", 3.0, "Taken from the frozen vineyards."),
        new("storms", "Rune of Storms", "lightning", 3.0, "Struck by lightning on the bell tower of Provins."),
        new("venom", "Rune of Venom", "poison", 3.0, "The poisoner's own seal."),
        new("corrosion", "Rune of Corrosion", "acid", 3.0, "Etched with the tanner's acids."),
        new("keenness", "Rune of Keenness", "crit", 0.4, "A whetstone's prayer."),
        new("warding", "Rune of Warding", "armor", 1.2, "The sign painted on the gates of the Temple."),
        new("fortune", "Rune of Fortune", "gold", 2.5, "Pressed into a Templar note of credit."),
        new("leech", "Rune of the Leech", "leech", 0.25, "Cut by the barber-surgeons of Paris."),
    ];

    public static RuneKind? Kind(string id) => Kinds.FirstOrDefault(k => k.Id == id);

    private static readonly double[] TierMul = [1, 1.5, 2.2, 3, 4, 5.5, 7, 9];

    /// <summary>The property a rune adds at its level and tier.</summary>
    public static int Value(Rune r)
    {
        var k = Kind(r.Kind);
        if (k is null) return 0;
        return System.Math.Max(1, (int)System.Math.Round(k.Base * (1 + 0.1 * (r.Level - 1)) * TierMul[(int)r.Tier]));
    }

    /// <summary>Experience from one level to the next. Higher tiers grow more slowly.</summary>
    public static long XpToNext(Rune r) =>
        r.Level >= MaxLevel ? long.MaxValue : (long)(12 * System.Math.Pow(r.Level, 1.3) * (1 + 0.75 * (int)r.Tier));

    /// <summary>Adds experience; true when the rune gained a level.</summary>
    public static bool AddXp(Rune r, long xp)
    {
        if (r.Level >= MaxLevel || xp <= 0) return false;
        r.Xp += xp;
        bool up = false;
        while (r.Level < MaxLevel && r.Xp >= XpToNext(r))
        {
            r.Xp -= XpToNext(r);
            r.Level++;
            up = true;
        }
        if (r.Level >= MaxLevel) r.Xp = 0;
        return up;
    }

    public static string Name(Rune r) => $"{r.Tier} {Kind(r.Kind)?.Name ?? r.Kind}";

    public static Rune Make(Random rng, Rarity tier = Rarity.Common, string? kind = null) =>
        new() { Id = Loot.NewId(), Kind = kind ?? Kinds[rng.Next(Kinds.Length)].Id, Tier = tier, Level = 1 };

    /// <summary>
    /// Sockets per item: chest and legs 3, two-handed weapons 3, shoulders and mantles 2, everything else 1,
    /// packs none. Exotic and Mythic pieces have one more.
    /// </summary>
    public static int SocketCount(Item i)
    {
        int n = i.Slot switch
        {
            ItemSlot.Weapon => Weapons.Of(i.Weapon).TwoHanded ? 3 : 1,
            ItemSlot.Armor or ItemSlot.Legs => 3,
            ItemSlot.Shoulders or ItemSlot.Mantle => 2,
            ItemSlot.Pack => 0,
            _ => 1,
        };
        return n == 0 ? 0 : n + (i.Rarity >= Rarity.Exotic ? 1 : 0);
    }

    public static List<Rune?> NewSockets(Item i) => Enumerable.Repeat<Rune?>(null, SocketCount(i)).ToList();

    /// <summary>Gives an item saved before sockets its empty sockets.</summary>
    public static List<Rune?> SocketsOf(Item i)
    {
        int n = SocketCount(i);
        i.Sockets ??= [];
        while (i.Sockets.Count < n) i.Sockets.Add(null);
        return i.Sockets;
    }

    public static int Bonus(Item i, string key)
    {
        if (i.Sockets is null) return 0;
        int sum = 0;
        foreach (var r in i.Sockets)
            if (r is not null && Kind(r.Kind)?.Property == key) sum += Value(r);
        return sum;
    }

    public static IEnumerable<Rune> In(Item i) => i.Sockets?.OfType<Rune>() ?? [];

    /// <summary>Two level-50 runes of one kind and tier become one of the next tier at level 1.</summary>
    public static string? CanFuse(Rune a, Rune b)
    {
        if (a.Id == b.Id) return "Choose two different runes.";
        if (a.Kind != b.Kind) return "Both runes must be of the same kind.";
        if (a.Tier != b.Tier) return "Both runes must be of the same tier.";
        if (a.Level < MaxLevel || b.Level < MaxLevel) return $"Both runes must reach level {MaxLevel} first.";
        if (a.Tier >= Rarity.Mythic) return "A Mythic rune can grow no further.";
        return null;
    }

    public static Rune Fuse(Rune a, Rune b) => new() { Id = Loot.NewId(), Kind = a.Kind, Tier = a.Tier + 1, Level = 1 };
}

// ======================================================================================================= traits

/// <summary>Trait families: which items can carry which traits.</summary>
public enum TraitFamily : byte { Weapon, Armour, Jewellery }

public sealed record TraitDef(string Id, string Name, TraitFamily Family, string Property, int Base, string Text);

/// <summary>
/// Item traits. Learned by research at the forge (the item is taken apart), then applied with Holy Oil to any item
/// of the same family. Values grow with item level, up to three times their base.
/// </summary>
public static class Traits
{
    public static readonly TraitDef[] All =
    [
        new("keen", "Keen", TraitFamily.Weapon, "crit", 3, "an edge that finds the gaps in mail"),
        new("balanced", "Balanced", TraitFamily.Weapon, "dexterity", 3, "weighted to swing quickly"),
        new("blessed", "Blessed", TraitFamily.Weapon, "undead", 12, "blessed at the altar of the Temple"),
        new("weighted", "Weighted", TraitFamily.Weapon, "strength", 3, "heavier than it looks"),
        new("thirsting", "Thirsting", TraitFamily.Weapon, "leech", 1, "it drinks what it spills"),
        new("reinforced", "Reinforced", TraitFamily.Armour, "armor", 5, "riveted twice over"),
        new("padded", "Padded", TraitFamily.Armour, "health", 4, "quilted thick beneath"),
        new("sturdy", "Sturdy", TraitFamily.Armour, "block", 2, "made to take a blow"),
        new("warded", "Warded", TraitFamily.Armour, "intelligence", 4, "sewn with a prayer"),
        new("gilded", "Gilded", TraitFamily.Jewellery, "gold", 8, "fine work that opens purses"),
        new("sage", "Sage", TraitFamily.Jewellery, "intelligence", 4, "a scholar's piece"),
        new("vital", "Vital", TraitFamily.Jewellery, "health", 4, "warm to the touch"),
        new("swift", "Swift", TraitFamily.Jewellery, "dexterity", 2, "light as a courier's purse"),
    ];

    public static TraitDef? Get(string? id) => id is null ? null : All.FirstOrDefault(t => t.Id == id);

    public static TraitFamily FamilyOf(ItemSlot s) => s switch
    {
        ItemSlot.Weapon => TraitFamily.Weapon,
        ItemSlot.Ring or ItemSlot.Ring2 or ItemSlot.Amulet or ItemSlot.Belt or ItemSlot.Pack => TraitFamily.Jewellery,
        _ => TraitFamily.Armour,
    };

    public static string Random(Random rng, ItemSlot slot)
    {
        var fam = FamilyOf(slot);
        var list = All.Where(t => t.Family == fam).ToArray();
        return list[rng.Next(list.Length)].Id;
    }

    public static int Value(TraitDef t, int itemLevel) => (int)System.Math.Round(t.Base * System.Math.Min(3.0, 1 + itemLevel / 40.0));

    public static int Bonus(Item i, string key) =>
        Get(i.Trait) is { } t && t.Property == key ? Value(t, i.ItemLevel) : 0;

    /// <summary>Research key for a trait in a family ("weapon:keen").</summary>
    public static string Key(TraitDef t) => $"{t.Family.ToString().ToLowerInvariant()}:{t.Id}";
}

// ========================================================================================================= sets

public sealed record SetPiece(ItemSlot Slot, string Base);
public sealed record SetBonus(int Pieces, Dictionary<string, int> Props);
public sealed record ItemSet(string Id, string Name, string Owner, int MinLevel, List<SetPiece> Pieces, List<SetBonus> Bonuses, Dictionary<string, int> Exalted, string Lore, bool BossOnly = false);

/// <summary>
/// Item sets. Pieces drop as Elite or better from ordinary monsters (bosses for the Iron King's regalia); wearing
/// several pieces of one set unlocks its bonuses. Exalted pieces (Proving Pit floor 6 and deeper) add one more line
/// once two pieces of the set are worn. Every piece a character ever holds is noted in their collection book.
/// </summary>
public static class ItemSets
{
    private static Dictionary<string, int> P(params (string K, int V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);

    public static readonly ItemSet[] All =
    [
        new("temple_guard", "Guard of the Temple", "Temple Guard's", 10,
            [new(ItemSlot.Helm, "Great Helm"), new(ItemSlot.Armor, "Mail Hauberk"), new(ItemSlot.Shield, "Kite Shield"),
             new(ItemSlot.Legs, "Plate Cuisses"), new(ItemSlot.Boots, "Mail Sabatons"), new(ItemSlot.Gloves, "Gauntlets")],
            [new(2, P(("health", 8))), new(4, P(("armor", 12))), new(6, P(("block", 6), ("health", 12)))],
            P(("armor", 8)), "The mail of the knights who kept the Temple Mount."),
        new("acre", "Last Stand of Acre", "Acre's", 25,
            [new(ItemSlot.Weapon, "Longsword"), new(ItemSlot.Shoulders, "Plate Pauldrons"), new(ItemSlot.Armor, "Brigandine"), new(ItemSlot.Legs, "Mail Chausses")],
            [new(2, P(("strength", 6))), new(4, P(("fire", 20), ("crit", 4)))],
            P(("strength", 6)), "Worn on the walls of Acre in its last May."),
        new("pilgrim", "The Pilgrim's Road", "Pilgrim's Road", 5,
            [new(ItemSlot.Boots, "Riding Boots"), new(ItemSlot.Mantle, "Pilgrim's Cloak"), new(ItemSlot.Pack, "Pilgrim's Satchel"), new(ItemSlot.Belt, "Leather Girdle")],
            [new(2, P(("dexterity", 4))), new(4, P(("gold", 25), ("health", 6)))],
            P(("dexterity", 3)), "Gear of the brothers who guarded the road to Jerusalem."),
        new("hospital", "Mercy of the Hospital", "Hospitaller's", 18,
            [new(ItemSlot.Amulet, "Reliquary Pendant"), new(ItemSlot.Ring, "Signet Ring"), new(ItemSlot.Gloves, "Leather Bracers"), new(ItemSlot.Mantle, "Templar Mantle")],
            [new(2, P(("intelligence", 6))), new(4, P(("leech", 3), ("health", 8)))],
            P(("intelligence", 6)), "Given by the Hospitallers to a Templar who carried their wounded."),
        new("penitent", "The Penitent's Vigil", "Penitent's", 35,
            [new(ItemSlot.Mantle, "Penitent's Robe"), new(ItemSlot.Belt, "Studded Belt"), new(ItemSlot.Ring, "Iron Band"), new(ItemSlot.Amulet, "Cross Pendant")],
            [new(2, P(("lightning", 15))), new(4, P(("intelligence", 10), ("crit", 5)))],
            P(("lightning", 12)), "Worn through a night of prayer before the pyre."),
        new("iron_king", "Regalia of the Iron King", "Iron King's", 40,
            [new(ItemSlot.Helm, "Great Helm"), new(ItemSlot.Shoulders, "Plate Pauldrons"), new(ItemSlot.Armor, "Brigandine"),
             new(ItemSlot.Legs, "Plate Cuisses"), new(ItemSlot.Boots, "Mail Sabatons"), new(ItemSlot.Gloves, "Gauntlets")],
            [new(2, P(("strength", 8))), new(4, P(("armor", 10), ("crit", 5))), new(6, P(("fire", 15), ("freeze", 15), ("lightning", 15), ("poison", 15), ("acid", 15)))],
            P(("strength", 10), ("health", 10)), "Philip's own harness, taken from his armoury.", BossOnly: true),
    ];

    public static ItemSet? Get(string? id) => id is null ? null : All.FirstOrDefault(s => s.Id == id);

    /// <summary>Makes a set piece of one of a set's slots (random when not given).</summary>
    public static Item MakePiece(Random rng, ItemSet set, int itemLevel, Rarity rarity, ItemSlot? slot = null, bool exalted = false)
    {
        var piece = slot is { } s ? set.Pieces.First(p => p.Slot == Loot.Home(s)) : set.Pieces[rng.Next(set.Pieces.Count)];
        var baseItem = Loot.MakeNamed(rng, piece.Base, itemLevel, rarity);
        var it = baseItem with { Name = $"{set.Owner} {piece.Base}", Set = set.Id, Exalted = exalted };
        it.Sockets = Runes.NewSockets(it);
        return it;
    }

    /// <summary>Elite+ drops sometimes become set pieces (never the boss-only set).</summary>
    public static Item MaybeSet(Random rng, Item item, double chance)
    {
        if (item.Rarity < Rarity.Elite || item.Rarity >= Rarity.Exotic || rng.NextDouble() >= chance) return item;
        var sets = All.Where(s => !s.BossOnly && s.MinLevel <= item.ItemLevel && s.Pieces.Any(p => p.Slot == item.Slot)).ToList();
        if (sets.Count == 0) return item;
        return MakePiece(rng, sets[rng.Next(sets.Count)], item.ItemLevel, item.Rarity, item.Slot);
    }

    /// <summary>Set bonus properties for what is worn: every threshold reached, plus the exalted line.</summary>
    public static Dictionary<string, int> Bonuses(IEnumerable<Item> worn)
    {
        var total = new Dictionary<string, int>();
        foreach (var g in worn.Where(i => i.Set is not null).GroupBy(i => i.Set!))
        {
            if (Get(g.Key) is not { } set) continue;
            int n = g.Count();
            foreach (var b in set.Bonuses.Where(b => n >= b.Pieces))
                foreach (var (k, v) in b.Props) total[k] = total.GetValueOrDefault(k) + v;
            if (n >= 2 && g.Any(i => i.Exalted))
                foreach (var (k, v) in set.Exalted) total[k] = total.GetValueOrDefault(k) + v;
        }
        return total;
    }

    /// <summary>A bonus line in words ("+15% damage of every element" when all five elements match).</summary>
    public static string Describe(Dictionary<string, int> props)
    {
        var el = Loot.ElementProperties;
        if (el.All(props.ContainsKey) && el.Select(k => props[k]).Distinct().Count() == 1)
        {
            var rest = props.Where(kv => !el.Contains(kv.Key)).Select(kv => Loot.DescribeProperty(kv.Key, kv.Value));
            return string.Join(", ", rest.Append($"+{props[el[0]]}% damage of every element"));
        }
        return string.Join(", ", props.Select(kv => Loot.DescribeProperty(kv.Key, kv.Value)));
    }

    /// <summary>Collection book key for a piece ("acre:Weapon", "acre:Weapon:exalted").</summary>
    public static string CollectionKey(Item i, bool exalted) => $"{i.Set}:{Loot.Home(i.Slot)}{(exalted ? ":exalted" : "")}";
}

// =================================================================================================== materials

public sealed record MaterialDef(string Id, string Name, string Text);

/// <summary>Upgrade materials and tokens, kept in a character's wallet rather than the bag.</summary>
public static class Materials
{
    public const string Whetstone = "whetstone", HolyOil = "holy_oil", SoulCore = "soul_core", MistEmber = "mist_ember",
        TrophyRelic = "trophy_relic", PetToken = "pet_token";

    public static readonly MaterialDef[] All =
    [
        new(Whetstone, "Whetstone", "Tempers gear at the forge. From salvage, daily endeavours and Mist coffers."),
        new(HolyOil, "Holy Oil", "Tempers gear past +4 and applies traits. From pledges, the Proving Pit and world bosses."),
        new(SoulCore, "Soul Core", "Feeds the Athanor. From Mist takeovers and the Proving Pit."),
        new(MistEmber, "Mist Ember", "Opens Mist coffers. Fades when the Mist moves on."),
        new(TrophyRelic, "Trophy Relic", "Teaches a relic skill such as Cleave or Riposte. From world bosses."),
        new(PetToken, "Menagerie Token", "With gold, buys a companion from the menagerie. From world bosses."),
    ];

    public static string Name(string id) => All.FirstOrDefault(m => m.Id == id)?.Name ?? id;
}

// ===================================================================================================== forge

/// <summary>Tempering, salvage, research and empowering: the forge's new work.</summary>
public static class Smithing
{
    public const int MaxTemper = 10, MaxEmpower = 10;

    public static int TemperGold(Item i) => 40 * (i.Upgrade + 1) * (1 + i.ItemLevel / 8);
    public static int TemperWhetstones(Item i) => i.Upgrade + 1;
    public static int TemperOil(Item i) => i.Upgrade >= 4 ? i.Upgrade - 3 : 0;

    /// <summary>Chance the item shatters when tempered from its current level: none up to +4, then rising to half.</summary>
    public static double BreakChance(Item i) => i.Upgrade switch { < 4 => 0, 4 => 0.10, 5 => 0.15, 6 => 0.20, 7 => 0.30, 8 => 0.40, _ => 0.50 };

    public static string? CanTemper(Item i)
    {
        if (i.Rarity >= Rarity.Exotic) return "Exotic and Mythic pieces are empowered, not tempered.";
        if (i.Upgrade >= MaxTemper) return $"It is already +{MaxTemper}.";
        return null;
    }

    /// <summary>One step of tempering: +8% damage or defense (at least 1) and +6% on every property (at least 1).</summary>
    public static Item Temper(Item i) => Improve(i, 0.08, 0.06);

    public static string? CanEmpower(Item target, Item sacrifice)
    {
        if (target.Id == sacrifice.Id) return "Choose two different pieces.";
        if (target.Rarity < Rarity.Exotic) return "Only Exotic and Mythic pieces are empowered.";
        if (sacrifice.Rarity != target.Rarity) return $"The sacrifice must also be {target.Rarity}.";
        if (target.Upgrade >= MaxEmpower) return $"It is already at rank {MaxEmpower}.";
        return null;
    }

    public static Item Empower(Item i) => Improve(i, 0.06, 0.05);

    private static Item Improve(Item i, double stat, double props)
    {
        int Up(int v, double f) => v <= 0 ? v : System.Math.Max(v + 1, (int)System.Math.Round(v * (1 + f)));
        var p = i.Properties.ToDictionary(kv => kv.Key, kv => Up(kv.Value, props));
        var r = i with { Upgrade = i.Upgrade + 1, Damage = Up(i.Damage, stat), Defense = Up(i.Defense, stat), Properties = p };
        r.Sockets = i.Sockets?.ToList();
        return r;
    }

    /// <summary>What taking an item apart yields.</summary>
    public static Dictionary<string, int> Salvage(Item i, Random rng)
    {
        var d = new Dictionary<string, int> { [Materials.Whetstone] = i.Rarity switch { <= Rarity.Rare => 1, Rarity.Elite => 2, Rarity.Epic => 3, _ => 5 } + i.Upgrade / 2 };
        if (i.Rarity >= Rarity.Legendary || rng.NextDouble() < 0.06 * (int)i.Rarity) d[Materials.HolyOil] = 1;
        return d;
    }

    public static int ResearchGold(Item i) => 100 + 10 * i.ItemLevel;
    public static int ApplyTraitGold(Item i) => 200 + 15 * i.ItemLevel;

    /// <summary>Mythic pieces carry a name of their own.</summary>
    public static readonly Dictionary<ItemSlot, string[]> MythicNames = new()
    {
        [ItemSlot.Weapon] = ["Durendal", "Joyeuse", "Hauteclere", "Almace", "Courtain"],
        [ItemSlot.Armor] = ["Hauberk of Hugues de Payns", "The Grand Master's Harness"],
        [ItemSlot.Helm] = ["Helm of Godfrey", "The Crowned Helm of Outremer"],
        [ItemSlot.Shield] = ["Aegis of Saint Maurice", "The Shield of Montgisard"],
        [ItemSlot.Mantle] = ["Mantle of the First Nine"],
        [ItemSlot.Ring] = ["Ring of the Grand Preceptor"],
        [ItemSlot.Amulet] = ["The Seal of Two Riders"],
    };

    /// <summary>A Mythic or Exotic piece (world bosses, the king, the Athanor).</summary>
    public static Item MakeHigh(Random rng, int itemLevel, Rarity rarity, ItemSlot? slot = null)
    {
        var it = Loot.MakeItem(rng, Loot.NewId(), itemLevel, rarity, slot);
        if (rarity == Rarity.Mythic && MythicNames.TryGetValue(it.Slot, out var names))
            it = it with { Name = $"{names[rng.Next(names.Length)]}, the {it.Name}" };
        it.Sockets = Runes.NewSockets(it);
        return it;
    }
}

// ============================================================================================ player progress

public sealed class Loadout
{
    public string Name { get; set; } = "";
    /// <summary>Item id per slot (slot names as in <see cref="ItemSlot"/>).</summary>
    public Dictionary<string, long> Items { get; set; } = new();
}

/// <summary>Per-day progress for pledges and endeavours. Counters reset when the date changes.</summary>
public sealed class DailyState
{
    public string Date { get; set; } = "";
    public Dictionary<string, int> Tally { get; set; } = new();
    public HashSet<string> Claimed { get; set; } = [];
}

/// <summary>
/// Everything the endgame systems remember about a character, saved with it: the rune pouch, the wallet, research,
/// the set collection, loadouts and the armoury rack, pets, and today's pledges.
/// </summary>
public sealed class PlayerExtras
{
    public List<Rune> Runes { get; set; } = [];
    public Dictionary<string, int> Materials { get; set; } = new();
    public HashSet<string> Researched { get; set; } = [];
    public HashSet<string> Collection { get; set; } = [];
    public HashSet<string> SetsClaimed { get; set; } = [];
    public List<Loadout> Loadouts { get; set; } = [];
    public List<Item> Armoury { get; set; } = [];
    public HashSet<string> Pets { get; set; } = [];
    /// <summary>Relic arts learned from trophy relics (spell ids).</summary>
    public HashSet<string> Relics { get; set; } = [];
    public string? ActivePet { get; set; }
    public DailyState Daily { get; set; } = new();
    /// <summary>The Mist takeover the embers belong to; when the Mist moves on, they fade.</summary>
    public string? MistId { get; set; }
    public int PitBest { get; set; }

    /// <summary>Mist threat, 0 to 100 (not saved).</summary>
    [JsonIgnore] public float Threat { get; set; }
    /// <summary>True while the Grey Huntsman is after this knight (not saved).</summary>
    [JsonIgnore] public bool Hunted { get; set; }

    public int Has(string material) => Materials.GetValueOrDefault(material);

    public void Give(string material, int n)
    {
        if (n == 0) return;
        Materials[material] = System.Math.Max(0, Materials.GetValueOrDefault(material) + n);
    }

    public bool Spend(string material, int n)
    {
        if (Has(material) < n) return false;
        Give(material, -n);
        return true;
    }

    public void Count(string key, int n = 1) => Daily.Tally[key] = Daily.Tally.GetValueOrDefault(key) + n;
    public void Best(string key, int value) => Daily.Tally[key] = System.Math.Max(Daily.Tally.GetValueOrDefault(key), value);

    /// <summary>Notes a set piece in the collection book; true if it was new.</summary>
    public bool Note(Item i)
    {
        if (i.Set is null) return false;
        bool added = Collection.Add(ItemSets.CollectionKey(i, false));
        if (i.Exalted) added |= Collection.Add(ItemSets.CollectionKey(i, true));
        return added;
    }
}

public static class Gear
{
    public const int ArmouryLimit = 60, MaxLoadouts = 4;

    /// <summary>Extra bag space from the pack: 2 at Common, 2 more per rarity step.</summary>
    public static int PackSpace(Item? pack) => pack is null ? 0 : 2 + 2 * (int)pack.Rarity;

    public static int BagLimit(Entity p) => Loot.InventoryLimit + PackSpace(p.Equipped.GetValueOrDefault(ItemSlot.Pack));

    /// <summary>Only one Exotic and one Mythic piece may be worn at once.</summary>
    public static string? HighRarityRule(Entity p, Item item, ItemSlot into)
    {
        if (item.Rarity < Rarity.Exotic) return null;
        var other = p.Equipped.FirstOrDefault(kv => kv.Key != into && kv.Value.Rarity == item.Rarity).Value;
        return other is null ? null : $"You can wear only one {item.Rarity} piece at a time ({other.DisplayName}).";
    }
}
