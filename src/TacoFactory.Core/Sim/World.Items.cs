namespace TacoFactory.Core.Sim;

/// <summary>Equipment, inventory and potions. Every change goes through here so derived stats stay right.</summary>
public sealed partial class World
{
    /// <summary>A property over everything worn, set bonuses included, scaled by level sync.</summary>
    public int PropertyTotal(Entity p, string key) =>
        (int)((p.Equipped.Values.Sum(i => i.Prop(key)) + p.SetBonus.GetValueOrDefault(key)) * p.GearScale);

    /// <summary>Recomputes life and mana from level, class and equipped properties, and the level sync for this zone.</summary>
    public void RecalcStats(Entity p)
    {
        var cls = Classes.Get(p.Class);
        ApplySync(p);
        p.SetBonus = ItemSets.Bonuses(p.Equipped.Values);
        int maxHp = Progression.MaxHealth(cls, p.EffectiveLevel) + 10 * PropertyTotal(p, "health");
        int maxMana = Progression.MaxMana(cls, p.EffectiveLevel) + 5 * PropertyTotal(p, "intelligence");
        p.MaxHp = maxHp;
        p.MaxMana = maxMana;
        p.Hp = System.Math.Min(p.Hp <= 0 && !p.Dead ? maxHp : p.Hp, maxHp);
        p.Mana = System.Math.Min(p.Mana, maxMana);
    }

    public Item? FindInventory(Entity p, long id) => p.Inventory.FirstOrDefault(i => i.Id == id);

    /// <summary>
    /// Equips an item from the bag. Rings go on the first free hand (or <paramref name="into"/> = Ring2 for the second).
    /// Returns null on success or the reason it can't.
    /// </summary>
    public string? Equip(Entity p, long itemId, ItemSlot? into = null)
    {
        if (FindInventory(p, itemId) is not { } item) return "That item isn't in your bag.";
        if (item.ItemLevel > p.Level) return $"You need level {item.ItemLevel} to use that.";
        if (item.Slot == ItemSlot.Shield && Weapons.Of(WeaponOf(p)).TwoHanded) return "Your two-handed weapon needs both hands.";
        var slot = item.Slot;
        if (slot == ItemSlot.Ring)
            slot = into == ItemSlot.Ring2 || (into is null && p.Equipped.ContainsKey(ItemSlot.Ring) && !p.Equipped.ContainsKey(ItemSlot.Ring2))
                ? ItemSlot.Ring2 : ItemSlot.Ring;
        if (Gear.HighRarityRule(p, item, slot) is string why) return why;
        p.Equipped.TryGetValue(slot, out var old);
        if (slot == ItemSlot.Pack && p.Inventory.Count - 1 + (old is null ? 0 : 1) > Loot.InventoryLimit + Gear.PackSpace(item))
            return "Your bag holds too much for that pack.";
        p.Inventory.Remove(item);
        if (old is not null) { p.Equipped.Remove(slot); p.Inventory.Add(old with { Slot = Loot.Home(old.Slot) }); }
        p.Equipped[slot] = item with { Slot = slot };
        if (item.Slot == ItemSlot.Weapon && Weapons.Of(item.Weapon).TwoHanded && p.Equipped.Remove(ItemSlot.Shield, out var shield))
            p.Inventory.Add(shield);
        RecalcStats(p);
        return null;
    }

    public string? Unequip(Entity p, ItemSlot slot)
    {
        if (!p.Equipped.TryGetValue(slot, out var item)) return "Nothing equipped there.";
        int limitAfter = slot == ItemSlot.Pack ? Loot.InventoryLimit : Gear.BagLimit(p);
        if (p.Inventory.Count >= limitAfter) return slot == ItemSlot.Pack ? "Empty your bag a little before taking off the pack." : "Your bag is full.";
        p.Equipped.Remove(slot);
        p.Inventory.Add(item with { Slot = Loot.Home(item.Slot) });
        RecalcStats(p);
        return null;
    }

    /// <summary>Drinks a potion: health restores 40% of life, mana 50% of mana. One second between drinks.</summary>
    public string? Drink(Entity p, bool health)
    {
        if (p.Dead) return "You are dead.";
        if (p.PotionCooldown > 0) return null;
        if (health ? p.HealthPotions <= 0 : p.ManaPotions <= 0) return health ? "No health potions." : "No mana potions.";
        if (health) { p.HealthPotions--; Heal(p, p, (int)(p.MaxHp * 0.4)); }
        else { p.ManaPotions--; p.Mana = System.Math.Min(p.MaxMana, p.Mana + p.MaxMana * 0.5f); }
        p.PotionCooldown = 1;
        return null;
    }
}
