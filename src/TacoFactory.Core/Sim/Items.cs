namespace TacoFactory.Core.Sim;

/// <summary>Rarity tiers, lowest to highest. Colors follow the Templar Knights loot table.</summary>
public enum Rarity : byte { Common = 0, Uncommon = 1, Rare = 2, Elite = 3, Epic = 4, Legendary = 5, Exotic = 6, Mythic = 7 }

/// <summary>
/// Equipment slots. Armor is the chest piece. Ring2 is only ever an equipped position: a ring in the bag is a Ring,
/// and becomes Ring2 while worn on the second hand.
/// </summary>
public enum ItemSlot : byte
{
    Weapon = 0, Armor = 1, Helm = 2, Shield = 3, Shoulders = 4, Legs = 5, Boots = 6, Gloves = 7, Belt = 8, Pack = 9,
    Mantle = 10, Ring = 11, Ring2 = 12, Amulet = 13,
}

/// <summary>Weapon families. Each one is trained by its own skill.</summary>
public enum WeaponType : byte { None = 0, OneHandedSword = 1, Dagger = 2, LightTwoHanded = 3, HeavyTwoHanded = 4, Throwing = 5 }

/// <summary>
/// A piece of gear. Ids are random 63-bit numbers so items stay unique as they move between zones,
/// stashes, trades and the auction house. Properties are the magical affixes (strength, fire, ...).
/// </summary>
public sealed record Item(long Id, string Name, ItemSlot Slot, Rarity Rarity, int ItemLevel, int Damage, int Defense, WeaponType Weapon = WeaponType.None)
{
    public Dictionary<string, int> Properties { get; init; } = new();
    public bool Forged { get; init; }
    /// <summary>Tempering at the forge (+1 to +10). For Exotic and Mythic items, the rank gained by empowering them.</summary>
    public int Upgrade { get; init; }
    /// <summary>A trait (keen, reinforced, ...), or null. See <see cref="Traits"/>.</summary>
    public string? Trait { get; init; }
    /// <summary>The item set this piece belongs to, or null. See <see cref="ItemSets"/>.</summary>
    public string? Set { get; init; }
    /// <summary>An Exalted set piece (Proving Pit floor 6 and deeper): gold name, one extra set bonus line.</summary>
    public bool Exalted { get; init; }
    /// <summary>Rune sockets: null entries are empty. Sized by <see cref="Runes.SocketCount"/>; null on items saved before sockets.</summary>
    public List<Rune?>? Sockets { get; set; }

    /// <summary>A property's total: the rolled value plus socketed runes and the trait.</summary>
    public int Prop(string key) => Properties.GetValueOrDefault(key) + Runes.Bonus(this, key) + Traits.Bonus(this, key);

    /// <summary>"+3 Arming Sword of Strength" style display name, from the strongest property.</summary>
    public string DisplayName =>
        (Upgrade > 0 ? $"+{Upgrade} " : "") +
        (Properties.Count == 0 || Set is not null || Rarity == Rarity.Mythic ? Name : $"{Name} of {Loot.PropertyTitle(Properties.MaxBy(kv => kv.Value).Key)}");
}

/// <summary>How each weapon family attacks. Damage multiplies the character's base damage plus the weapon's own.</summary>
public sealed record WeaponStats(float DamageMul, float Cooldown, float Range, float AreaRadius, bool Ranged, bool TwoHanded);

public static class Weapons
{
    public static WeaponStats Of(WeaponType t) => t switch
    {
        WeaponType.OneHandedSword => new(1.00f, 0.60f, 70, 0, false, false),
        WeaponType.Dagger => new(0.70f, 0.40f, 60, 0, false, false),
        WeaponType.LightTwoHanded => new(1.35f, 0.75f, 90, 0, false, true),
        WeaponType.HeavyTwoHanded => new(1.60f, 1.10f, 80, 95, false, true), // strikes everything around the target
        WeaponType.Throwing => new(0.85f, 0.70f, 360, 0, true, false),
        _ => new(0.50f, 0.60f, 60, 0, false, false), // bare hands
    };

    public static string Describe(WeaponType t) => t switch
    {
        WeaponType.OneHandedSword => "One-handed sword",
        WeaponType.Dagger => "Dagger",
        WeaponType.LightTwoHanded => "Two-handed sword",
        WeaponType.HeavyTwoHanded => "Maul or club",
        WeaponType.Throwing => "Throwing weapon",
        _ => "Fists",
    };
}

public static class Loot
{
    public const double DropChance = 0.30;
    public const int InventoryLimit = 40, StashLimit = 60, GuildStashLimit = 100;

    // Weights per rarity (Common .. Legendary).
    // Exotic and Mythic never roll from ordinary drops (weight 0): they come from world bosses, the king, the Athanor.
    private static readonly int[] Weights = [30, 25, 15, 10, 7, 3, 0, 0];
    private static readonly int[] RarityBonus = [1, 2, 4, 6, 8, 10, 13, 16];
    private static readonly double[] RarityValue = [1, 1.6, 2.6, 4, 6, 9, 14, 20];

    /// <summary>Magical properties (from the Templar Knights balance constants). Stats add flat bonuses; elements add % damage.</summary>
    public static readonly string[] StatProperties = ["strength", "health", "dexterity", "intelligence"];
    public static readonly string[] ElementProperties = ["fire", "freeze", "lightning", "poison", "acid"];
    /// <summary>Properties that only runes, traits and set bonuses give: crit %, armour %, block %, gold find %, life leech %, bonus vs undead %.</summary>
    public static readonly string[] SpecialProperties = ["crit", "armor", "block", "gold", "leech", "undead"];

    public static string PropertyTitle(string key) => key switch
    {
        "strength" => "Strength", "health" => "Vigour", "dexterity" => "Swiftness", "intelligence" => "Wisdom",
        "fire" => "Flame", "freeze" => "Frost", "lightning" => "Storms", "poison" => "Venom", "acid" => "Corrosion",
        "crit" => "Keenness", "armor" => "Warding", "block" => "the Bulwark", "gold" => "Fortune", "leech" => "the Leech", "undead" => "the Exorcist",
        _ => key,
    };

    public static string DescribeProperty(string key, int value) => key switch
    {
        "strength" => $"+{value} strength (+{value * 3} damage)",
        "health" => $"+{value} health (+{value * 10} life)",
        "dexterity" => $"+{value} dexterity ({value * 2}% faster attacks)",
        "intelligence" => $"+{value} intelligence (+{value * 5} mana)",
        "crit" => $"+{value}% critical chance",
        "armor" => $"+{value}% defense",
        "block" => $"+{value}% block chance",
        "gold" => $"+{value}% gold found",
        "leech" => $"{value}% of damage dealt returns as life",
        "undead" => $"+{value}% damage against the undead",
        _ => $"+{value}% {key} damage",
    };

    public static long NewId() => Random.Shared.NextInt64(1, long.MaxValue);

    private static readonly Dictionary<WeaponType, string[]> WeaponNames = new()
    {
        [WeaponType.OneHandedSword] = ["Arming Sword", "Falchion", "Messer"],
        [WeaponType.Dagger] = ["Rondel Dagger", "Misericorde", "Baselard"],
        [WeaponType.LightTwoHanded] = ["Longsword", "Bastard Sword", "Greatsword"],
        [WeaponType.HeavyTwoHanded] = ["Great Maul", "Lead Maul", "Knotted War Club", "Great Club"],
        [WeaponType.Throwing] = ["Throwing Axes", "Throwing Knives", "Javelins"],
    };

    /// <summary>Base names per slot (index = ItemSlot). Ring2 has none: it is where a second ring is worn.</summary>
    private static readonly string[][] ArmorNames =
    [
        [],
        ["Gambeson", "Mail Hauberk", "Templar Surcoat", "Brigandine"],
        ["Kettle Helm", "Nasal Helm", "Great Helm", "Coif"],
        ["Heater Shield", "Buckler", "Kite Shield"],
        ["Mail Spaulders", "Leather Pauldrons", "Plate Pauldrons"],
        ["Mail Chausses", "Padded Breeches", "Plate Cuisses"],
        ["Riding Boots", "Mail Sabatons", "Turnshoes"],
        ["Mail Mittens", "Leather Bracers", "Gauntlets"],
        ["Sword Belt", "Leather Girdle", "Studded Belt"],
        ["Pilgrim's Satchel", "Haversack", "Pannier Pack"],
        ["Templar Mantle", "Pilgrim's Cloak", "Penitent's Robe"],
        ["Signet Ring", "Iron Band", "Gold Ring"],
        [],
        ["Pilgrim's Badge", "Reliquary Pendant", "Cross Pendant"],
    ];

    /// <summary>Share of a chest piece's defense each slot gives. Jewellery and packs give none.</summary>
    private static readonly double[] DefenseShare = [0, 1.0, 1.0, 1.0, 0.25, 0.3, 0.15, 0.15, 0.1, 0, 0.15, 0, 0, 0];

    /// <summary>
    /// Share of a full property roll each slot gets: the four original slots roll in full; the smaller pieces roll less,
    /// so a full harness of fourteen is stronger than four without leaving the monsters' tuning behind.
    /// </summary>
    private static readonly double[] PropertyShare = [1, 1, 1, 1, 0.45, 0.6, 0.45, 0.45, 0.45, 0.3, 0.45, 0.6, 0.6, 0.6];

    /// <summary>How often each slot drops (index = ItemSlot): weapons most, then chest, helm and shield.</summary>
    private static readonly int[] SlotWeights = [24, 10, 9, 8, 6, 7, 6, 6, 5, 3, 5, 6, 0, 5];

    public static ItemSlot RandomSlot(Random rng)
    {
        int roll = rng.Next(SlotWeights.Sum());
        for (int i = 0; i < SlotWeights.Length; i++) if ((roll -= SlotWeights[i]) < 0) return (ItemSlot)i;
        return ItemSlot.Weapon;
    }

    /// <summary>Base item names for one slot (Ring2 shares the Ring names).</summary>
    public static IReadOnlyList<string> NamesFor(ItemSlot slot) => ArmorNames[(int)(slot == ItemSlot.Ring2 ? ItemSlot.Ring : slot)];

    /// <summary>The slot an item goes back to when taken off (a second ring is a ring again).</summary>
    public static ItemSlot Home(ItemSlot s) => s == ItemSlot.Ring2 ? ItemSlot.Ring : s;

    /// <summary>The name shown for a slot in windows (Armor is the chest, Gloves are gauntlets and bracers).</summary>
    public static string SlotName(ItemSlot s) => s switch
    {
        ItemSlot.Armor => "Chest", ItemSlot.Gloves => "Gauntlets", ItemSlot.Ring2 => "Ring", ItemSlot.Pack => "Pack",
        _ => s.ToString(),
    };

    /// <summary>Every base item name that drops (weapons, armour, helms, shields).</summary>
    public static IEnumerable<string> BaseNames => WeaponNames.Values.SelectMany(n => n).Concat(ArmorNames.SelectMany(n => n));

    private static readonly string[] BaseNamesLongestFirst = BaseNames.OrderByDescending(n => n.Length).ToArray();

    /// <summary>The base item an item name is built on ("Forged Falchion of Flame" -> "Falchion"), or null.</summary>
    public static string? BaseNameOf(string name)
    {
        foreach (var b in BaseNamesLongestFirst)
            if (name.Contains(b, StringComparison.OrdinalIgnoreCase)) return b;
        return null;
    }

    /// <summary>File-name slug of a base item, as used by its icon (assets/ui/icons/items/&lt;slug&gt;.png).</summary>
    public static string Slug(string baseName) => baseName.ToLowerInvariant().Replace("'", "").Replace(' ', '_');

    /// <summary>A specific base item ("Great Helm") with properties rolled for its rarity.</summary>
    public static Item MakeNamed(Random rng, string baseName, int itemLevel, Rarity rarity)
    {
        foreach (var (wt, names) in WeaponNames)
            if (names.Contains(baseName))
                return MakeItem(rng, NewId(), itemLevel, rarity, weapon: wt) with { Name = baseName };
        for (int slot = 1; slot < ArmorNames.Length; slot++)
            if (ArmorNames[slot].Contains(baseName))
            {
                var it = MakeItem(rng, NewId(), itemLevel, rarity, (ItemSlot)slot) with { Name = baseName };
                it.Sockets = Runes.NewSockets(it);
                return it;
            }
        throw new ArgumentException($"No base item called {baseName}");
    }

    public static Rarity RollRarity(Random rng, Rarity cap, Rarity min = Rarity.Common)
    {
        int lo = (int)min, hi = (int)cap;
        if (hi < lo) hi = lo;
        int total = 0;
        for (int i = lo; i <= hi; i++) total += Weights[i];
        int roll = rng.Next(total);
        for (int i = lo; i <= hi; i++)
        {
            roll -= Weights[i];
            if (roll < 0) return (Rarity)i;
        }
        return (Rarity)hi;
    }

    public static Item MakeItem(Random rng, long id, int itemLevel, Rarity rarity, ItemSlot? slot = null, WeaponType? weapon = null)
    {
        var s = weapon is not null ? ItemSlot.Weapon : Home(slot ?? RandomSlot(rng));
        int bonus = RarityBonus[(int)rarity];
        var props = RollProperties(rng, itemLevel, rarity);
        double share = PropertyShare[(int)s];
        if (share < 1) foreach (var k in props.Keys.ToList()) props[k] = System.Math.Max(1, (int)System.Math.Round(props[k] * share));
        Item item;
        if (s == ItemSlot.Weapon)
        {
            var wt = weapon ?? (WeaponType)rng.Next(1, 6);
            var names = WeaponNames[wt];
            item = new Item(id, names[rng.Next(names.Length)], s, rarity, itemLevel, 2 + itemLevel + bonus * 2, 0, wt) { Properties = props };
        }
        else
        {
            var an = ArmorNames[(int)s];
            int def = DefenseShare[(int)s] <= 0 ? 0 : System.Math.Max(1, (int)System.Math.Round((1 + itemLevel / 2 + bonus) * DefenseShare[(int)s]));
            item = new Item(id, an[rng.Next(an.Length)], s, rarity, itemLevel, 0, def) { Properties = props };
        }
        // Uncommon and better may carry a trait; Exotic and Mythic always do.
        if (rarity >= Rarity.Exotic || (rarity >= Rarity.Uncommon && rng.NextDouble() < 0.3))
            item = item with { Trait = Traits.Random(rng, s) };
        item.Sockets = Runes.NewSockets(item);
        return item;
    }

    /// <summary>One property per rarity step above Common (Uncommon 1 ... Legendary 5). Values grow with item level.</summary>
    public static Dictionary<string, int> RollProperties(Random rng, int itemLevel, Rarity rarity)
    {
        var props = new Dictionary<string, int>();
        int count = (int)rarity;
        var pool = StatProperties.Concat(ElementProperties).OrderBy(_ => rng.Next()).Take(count);
        foreach (var key in pool)
            props[key] = ElementProperties.Contains(key) ? 5 + itemLevel / 2 + rng.Next(0, 4) : 1 + itemLevel / 8 + rng.Next(0, 3);
        return props;
    }

    /// <summary>Gear score, used to rank items.</summary>
    public static int Score(Item item) => item.Damage * 2 + item.Defense + item.Properties.Values.Sum() + (int)item.Rarity * 2;

    /// <summary>What a merchant charges for an item. Merchants buy back at a quarter of this.</summary>
    public static int Value(Item i) =>
        (int)((10 + i.ItemLevel * 8 + i.Damage * 6 + i.Defense * 6 + i.Properties.Values.Sum() * 20) * RarityValue[(int)i.Rarity]);

    public static int SellPrice(Item i) => System.Math.Max(1, Value(i) / 4);
}

/// <summary>
/// The Forge (FORGE_SYSTEM.md): two items become one. The result takes the chosen item's type, the average
/// rarity (rounded up) and item level, and every magical property of both; shared properties are averaged.
/// </summary>
public static class Forge
{
    public static string? CanCombine(Item a, Item b)
    {
        if (a.Id == b.Id) return "Choose two different items.";
        if (a.Properties.Count + b.Properties.Count == 0) return "At least one of the two items must have a magical property.";
        if (a.Rarity >= Rarity.Exotic || b.Rarity >= Rarity.Exotic) return "Exotic and Mythic pieces can't be combined; empower them instead.";
        return null;
    }

    public static Item Combine(Item a, Item b, bool keepFirstType)
    {
        // The result is a new piece: tempering, set and sockets start over, the chosen item's trait stays.
        var baseItem = keepFirstType ? a : b;
        var props = new Dictionary<string, int>();
        foreach (var key in a.Properties.Keys.Union(b.Properties.Keys))
        {
            bool inA = a.Properties.TryGetValue(key, out int va), inB = b.Properties.TryGetValue(key, out int vb);
            props[key] = inA && inB ? (int)System.Math.Ceiling((va + vb) / 2.0) : inA ? va : vb;
        }
        var rarity = (Rarity)System.Math.Min((int)Rarity.Legendary, (int)System.Math.Ceiling(((int)a.Rarity + (int)b.Rarity) / 2.0));
        int level = (int)System.Math.Round((a.ItemLevel + b.ItemLevel) / 2.0, MidpointRounding.AwayFromZero);
        string name = baseItem.Name.StartsWith("Forged ") ? baseItem.Name : "Forged " + baseItem.Name;
        var r = baseItem with { Id = Loot.NewId(), Name = name, Rarity = rarity, ItemLevel = level, Properties = props, Forged = true, Upgrade = 0, Set = null, Exalted = false };
        r.Sockets = Runes.NewSockets(r);
        return r;
    }
}

/// <summary>Merchant stock and gambling.</summary>
public static class Merchant
{
    public const int HealthPotionPrice = 30, ManaPotionPrice = 30, MaxPotions = 20;

    public static List<Item> Stock(Random rng, int level, bool rareStock)
    {
        var list = new List<Item>();
        for (int i = 0; i < 12; i++)
        {
            var rarity = Loot.RollRarity(rng, rareStock ? Rarity.Rare : Rarity.Uncommon);
            list.Add(Loot.MakeItem(rng, Loot.NewId(), System.Math.Max(1, level), rarity, i < 4 ? ItemSlot.Weapon : Loot.RandomSlot(rng)));
        }
        return list;
    }

    public static int GamblePrice(int level) => 150 + 30 * level;

    /// <summary>A gamble: pay a fixed price for an unseen item of the chosen kind, rolled at Uncommon or better.</summary>
    public static Item Gamble(Random rng, int level, ItemSlot slot) =>
        Loot.MakeItem(rng, Loot.NewId(), System.Math.Max(1, level), Loot.RollRarity(rng, Rarity.Legendary, Rarity.Uncommon), slot);
}
