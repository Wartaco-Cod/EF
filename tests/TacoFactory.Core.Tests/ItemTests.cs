using TacoFactory.Core.Content;
using TacoFactory.Core.Data;
using TacoFactory.Core.Sim;

namespace TacoFactory.Core.Tests;

public class ItemTests
{
    private static readonly GameData Data = GameData.Load(ContentRoot.Locate(AppContext.BaseDirectory)!);

    private static Item Gear(string name, ItemSlot slot, Rarity r, int level, Dictionary<string, int> props, WeaponType w = WeaponType.None) =>
        new(Loot.NewId(), name, slot, r, level, slot == ItemSlot.Weapon ? 10 : 0, slot == ItemSlot.Weapon ? 0 : 5, w) { Properties = props };

    [Fact]
    public void Rarity_sets_the_number_of_properties()
    {
        var rng = new Random(1);
        foreach (var r in Enum.GetValues<Rarity>())
            Assert.Equal((int)r, Loot.MakeItem(rng, 1, 10, r).Properties.Count);
    }

    [Fact]
    public void Forge_follows_the_design_examples()
    {
        // FORGE_SYSTEM.md example 2: Strength 4 + Strength 2 averages to 3, other properties carry over.
        var sword = Gear("Arming Sword", ItemSlot.Weapon, Rarity.Rare, 20, new() { ["strength"] = 4, ["fire"] = 2 }, WeaponType.OneHandedSword);
        var dagger = Gear("Rondel Dagger", ItemSlot.Weapon, Rarity.Rare, 20, new() { ["strength"] = 2, ["lightning"] = 1 }, WeaponType.Dagger);
        var r = Forge.Combine(sword, dagger, keepFirstType: true);
        Assert.Equal(3, r.Prop("strength"));
        Assert.Equal(2, r.Prop("fire"));
        Assert.Equal(1, r.Prop("lightning"));
        Assert.Equal(WeaponType.OneHandedSword, r.Weapon);
        Assert.Equal(Rarity.Rare, r.Rarity);
        Assert.True(r.Forged);
        Assert.NotEqual(sword.Id, r.Id);

        // Example 3: White + Yellow rounds up to Blue (Common + Elite -> Rare); level averages.
        var white = Gear("Arming Sword", ItemSlot.Weapon, Rarity.Common, 1, new() { ["strength"] = 1 }, WeaponType.OneHandedSword);
        var yellow = Gear("Great Helm", ItemSlot.Helm, Rarity.Elite, 29, new() { ["health"] = 3, ["fire"] = 2 });
        var helm = Forge.Combine(white, yellow, keepFirstType: false);
        Assert.Equal(ItemSlot.Helm, helm.Slot);
        Assert.Equal(Rarity.Rare, helm.Rarity);
        Assert.Equal(15, helm.ItemLevel);
        Assert.Equal(3, helm.Properties.Count);
    }

    [Fact]
    public void Forge_needs_at_least_one_property()
    {
        var a = Gear("Coif", ItemSlot.Helm, Rarity.Common, 1, new());
        var b = Gear("Coif", ItemSlot.Helm, Rarity.Common, 1, new());
        Assert.NotNull(Forge.CanCombine(a, b));
        Assert.NotNull(Forge.CanCombine(a, a));
    }

    [Fact]
    public void Value_rises_with_rarity_and_merchants_pay_a_quarter()
    {
        var rng = new Random(3);
        var common = Loot.MakeItem(rng, 1, 10, Rarity.Common, ItemSlot.Armor);
        var epic = common with { Rarity = Rarity.Epic };
        Assert.True(Loot.Value(epic) > Loot.Value(common) * 5);
        Assert.Equal(Loot.Value(common) / 4, Loot.SellPrice(common));
    }

    [Fact]
    public void Equipping_applies_properties_and_two_handers_drop_the_shield()
    {
        var w = new World(Data, "bloodied_fields", 1);
        var p = w.AddPlayer("Kay", ClassId.Knight, level: 10);
        int baseHp = p.MaxHp;
        var helm = Gear("Great Helm", ItemSlot.Helm, Rarity.Rare, 5, new() { ["health"] = 4 });
        p.Inventory.Add(helm);
        Assert.Null(w.Equip(p, helm.Id));
        Assert.Equal(baseHp + 40, p.MaxHp);

        var maul = Gear("Great Maul", ItemSlot.Weapon, Rarity.Common, 1, new(), WeaponType.HeavyTwoHanded);
        p.Inventory.Add(maul);
        Assert.Null(w.Equip(p, maul.Id));
        Assert.False(p.Equipped.ContainsKey(ItemSlot.Shield));
        Assert.Contains(p.Inventory, i => i.Slot == ItemSlot.Shield);
        var shield = p.Inventory.First(i => i.Slot == ItemSlot.Shield);
        Assert.NotNull(w.Equip(p, shield.Id)); // needs a free hand
    }

    [Fact]
    public void Level_requirement_is_enforced()
    {
        var w = new World(Data, "bloodied_fields", 2);
        var p = w.AddPlayer("Kay", ClassId.Knight, level: 3);
        var big = Gear("Great Helm", ItemSlot.Helm, Rarity.Rare, 12, new() { ["health"] = 4 });
        p.Inventory.Add(big);
        Assert.Equal("You need level 12 to use that.", w.Equip(p, big.Id));
    }

    [Fact]
    public void Potions_heal_and_have_a_short_cooldown()
    {
        var w = new World(Data, "bloodied_fields", 3);
        var p = w.AddPlayer("Kay", ClassId.Knight);
        p.HealthPotions = 2;
        p.Hp = 10;
        Assert.Null(w.Drink(p, health: true));
        Assert.True(p.Hp > 10);
        Assert.Equal(1, p.HealthPotions);
        w.Drink(p, health: true); // still cooling down
        Assert.Equal(1, p.HealthPotions);
    }

    [Fact]
    public void Gambling_never_returns_common()
    {
        var rng = new Random(9);
        for (int i = 0; i < 200; i++) Assert.True(Merchant.Gamble(rng, 10, ItemSlot.Armor).Rarity >= Rarity.Uncommon);
    }
}
