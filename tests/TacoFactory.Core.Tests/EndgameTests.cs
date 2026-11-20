using System.Text.Json;
using TacoFactory.Core.Content;
using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;

namespace TacoFactory.Core.Tests;

public class EndgameTests
{
    private static readonly GameData Data = GameData.Load(ContentRoot.Locate(AppContext.BaseDirectory)!);

    [Fact]
    public void Sockets_follow_the_design()
    {
        var rng = new Random(3);
        Assert.Equal(3, Runes.SocketCount(Loot.MakeNamed(rng, "Mail Hauberk", 10, Rarity.Rare)));     // chest
        Assert.Equal(3, Runes.SocketCount(Loot.MakeNamed(rng, "Plate Cuisses", 10, Rarity.Rare)));    // legs
        Assert.Equal(3, Runes.SocketCount(Loot.MakeNamed(rng, "Greatsword", 10, Rarity.Rare)));       // two-handed
        Assert.Equal(1, Runes.SocketCount(Loot.MakeNamed(rng, "Arming Sword", 10, Rarity.Rare)));     // one-handed
        Assert.Equal(1, Runes.SocketCount(Loot.MakeNamed(rng, "Kite Shield", 10, Rarity.Rare)));
        Assert.Equal(1, Runes.SocketCount(Loot.MakeNamed(rng, "Sword Belt", 10, Rarity.Rare)));
        Assert.Equal(1, Runes.SocketCount(Loot.MakeNamed(rng, "Great Helm", 10, Rarity.Rare)));
        Assert.Equal(1, Runes.SocketCount(Loot.MakeNamed(rng, "Leather Bracers", 10, Rarity.Rare)));
        Assert.Equal(0, Runes.SocketCount(Loot.MakeNamed(rng, "Haversack", 10, Rarity.Rare)));
        Assert.Equal(4, Runes.SocketCount(Smithing.MakeHigh(rng, 50, Rarity.Exotic, ItemSlot.Armor))); // one more on Exotic
    }

    [Fact]
    public void Every_slot_drops_and_rings_go_on_either_hand()
    {
        var rng = new Random(5);
        var seen = new HashSet<ItemSlot>();
        for (int i = 0; i < 3000; i++) seen.Add(Loot.MakeItem(rng, Loot.NewId(), 20, Rarity.Rare).Slot);
        Assert.Equal(Enum.GetValues<ItemSlot>().Length - 1, seen.Count); // every slot but Ring2
        Assert.DoesNotContain(ItemSlot.Ring2, seen);

        var w = new World(Data, Data.Town.Id, 1);
        var p = w.AddPlayer("Hugh", ClassId.Knight, 30);
        var a = Loot.MakeNamed(rng, "Signet Ring", 10, Rarity.Rare);
        var b = Loot.MakeNamed(rng, "Gold Ring", 10, Rarity.Rare);
        p.Inventory.AddRange([a, b]);
        Assert.Null(w.Equip(p, a.Id));
        Assert.Null(w.Equip(p, b.Id));
        Assert.Equal(a.Id, p.Equipped[ItemSlot.Ring].Id);
        Assert.Equal(b.Id, p.Equipped[ItemSlot.Ring2].Id);
        Assert.Null(w.Unequip(p, ItemSlot.Ring2));
        Assert.Equal(ItemSlot.Ring, p.Inventory.Single(i => i.Id == b.Id).Slot); // a ring again in the bag
    }

    [Fact]
    public void Runes_grow_to_fifty_and_fuse_into_the_next_tier()
    {
        var rng = new Random(1);
        var r = Runes.Make(rng, Rarity.Common, "might");
        Assert.Equal(1, r.Level);
        int before = Runes.Value(r);
        while (r.Level < Runes.MaxLevel) Runes.AddXp(r, 1000);
        Assert.Equal(Runes.MaxLevel, r.Level);
        Assert.True(Runes.Value(r) > before);
        var other = new Rune { Id = 99, Kind = "might", Tier = Rarity.Common, Level = 10 };
        Assert.NotNull(Runes.CanFuse(r, other)); // the second isn't at 50 yet
        other.Level = 50;
        Assert.Null(Runes.CanFuse(r, other));
        var fused = Runes.Fuse(r, other);
        Assert.Equal(Rarity.Uncommon, fused.Tier);
        Assert.Equal(1, fused.Level);
        Assert.NotNull(Runes.CanFuse(r, new Rune { Id = 7, Kind = "flame", Tier = Rarity.Common, Level = 50 }));
    }

    [Fact]
    public void A_socketed_rune_adds_its_property_to_the_item()
    {
        var rng = new Random(2);
        var sword = Loot.MakeNamed(rng, "Arming Sword", 10, Rarity.Common);
        Assert.Equal(0, sword.Prop("fire"));
        var r = new Rune { Id = 1, Kind = "flame", Tier = Rarity.Rare, Level = 10 };
        Runes.SocketsOf(sword)[0] = r;
        Assert.Equal(Runes.Value(r), sword.Prop("fire"));
    }

    [Fact]
    public void Tempering_raises_stats_and_can_shatter_past_four()
    {
        var rng = new Random(4);
        var helm = Loot.MakeNamed(rng, "Great Helm", 20, Rarity.Rare);
        var t = Smithing.Temper(helm);
        Assert.Equal(1, t.Upgrade);
        Assert.True(t.Defense > helm.Defense);
        Assert.All(helm.Properties, kv => Assert.True(t.Properties[kv.Key] > kv.Value));
        Assert.Equal(0, Smithing.BreakChance(helm with { Upgrade = 3 }));
        Assert.True(Smithing.BreakChance(helm with { Upgrade = 6 }) > 0);
        Assert.True(Smithing.TemperOil(helm with { Upgrade = 5 }) > 0);
        Assert.NotNull(Smithing.CanTemper(Smithing.MakeHigh(rng, 30, Rarity.Exotic)));
    }

    [Fact]
    public void Only_one_exotic_and_one_mythic_can_be_worn()
    {
        var rng = new Random(6);
        var w = new World(Data, Data.Town.Id, 1);
        var p = w.AddPlayer("Odo", ClassId.Knight, 60);
        var helm = Smithing.MakeHigh(rng, 40, Rarity.Exotic, ItemSlot.Helm);
        var chest = Smithing.MakeHigh(rng, 40, Rarity.Exotic, ItemSlot.Armor);
        var mythicChest = Smithing.MakeHigh(rng, 40, Rarity.Mythic, ItemSlot.Armor);
        p.Inventory.AddRange([helm, chest, mythicChest]);
        Assert.Null(w.Equip(p, helm.Id));
        Assert.NotNull(w.Equip(p, chest.Id));        // a second Exotic
        Assert.Null(w.Equip(p, mythicChest.Id));     // a Mythic beside an Exotic is fine
        Assert.NotNull(Smithing.CanEmpower(helm, Loot.MakeNamed(rng, "Coif", 40, Rarity.Legendary)));
        Assert.Null(Smithing.CanEmpower(helm, chest));
        Assert.Equal(1, Smithing.Empower(helm).Upgrade);
    }

    [Fact]
    public void Set_bonuses_and_the_exalted_line()
    {
        var rng = new Random(7);
        var set = ItemSets.Get("temple_guard")!;
        var pieces = set.Pieces.Take(4).Select(pc => ItemSets.MakePiece(rng, set, 30, Rarity.Elite, pc.Slot)).ToList();
        var bonus = ItemSets.Bonuses(pieces);
        Assert.Equal(8, bonus["health"]);
        Assert.Equal(12, bonus["armor"]);
        Assert.False(bonus.ContainsKey("block"));
        pieces[0] = pieces[0] with { Exalted = true };
        Assert.Equal(12 + 8, ItemSets.Bonuses(pieces)["armor"]); // the exalted line
        var extras = new PlayerExtras();
        Assert.True(extras.Note(pieces[0]));
        Assert.Contains("temple_guard:Helm:exalted", extras.Collection);
    }

    [Fact]
    public void Traits_have_families_and_scale_with_level()
    {
        Assert.Equal(TraitFamily.Weapon, Traits.FamilyOf(ItemSlot.Weapon));
        Assert.Equal(TraitFamily.Jewellery, Traits.FamilyOf(ItemSlot.Ring2));
        Assert.Equal(TraitFamily.Armour, Traits.FamilyOf(ItemSlot.Boots));
        var keen = Traits.Get("keen")!;
        Assert.True(Traits.Value(keen, 80) > Traits.Value(keen, 1));
        var sword = Loot.MakeNamed(new Random(1), "Falchion", 40, Rarity.Rare) with { Trait = "keen" };
        Assert.Equal(Traits.Value(keen, 40), sword.Prop("crit"));
    }

    [Fact]
    public void Level_sync_brings_a_veteran_down_to_the_zone()
    {
        var zone = Data.Zones.Values.First(z => z.Id == "bloodied_fields");
        var w = new World(Data, zone.Id, 1);
        var p = w.AddPlayer("Old Guard", ClassId.Knight, 60);
        p.Equipped[ItemSlot.Armor] = Loot.MakeItem(new Random(1), 1, 60, Rarity.Epic, ItemSlot.Armor);
        w.RefreshPlayer(p);
        Assert.Equal(zone.LevelMax + 2, p.SyncLevel);
        Assert.True(p.GearScale < 1);
        Assert.Equal(Progression.MaxHealth(Classes.Get(ClassId.Knight), zone.LevelMax + 2) + 10 * w.PropertyTotal(p, "health"), p.MaxHp);
        var town = new World(Data, Data.Town.Id, 1);
        var q = town.AddPlayer("Old Guard", ClassId.Knight, 60);
        Assert.Equal(0, q.SyncLevel);
    }

    [Fact]
    public void The_mist_strengthens_monsters_and_sets_out_coffers()
    {
        var zone = Data.Zones.Values.First(z => z.MistEligible && z.PackCount > 0);
        var plain = new World(Data, zone.Id, 11);
        plain.Populate(1);
        var mist = new World(Data, zone.Id, 11);
        mist.SetMist(true);
        mist.Populate(1);
        Assert.Equal(3, mist.Coffers.Count);
        Assert.True(mist.Monsters.Sum(m => m.MaxHp) > plain.Monsters.Sum(m => m.MaxHp) * 1.4);
        var p = mist.AddPlayer("Brother", ClassId.Knight, 10);
        Assert.NotNull(mist.OpenCoffer(p, mist.Coffers[0].Id)); // no embers yet
    }

    [Fact]
    public void The_proving_pit_climbs_floor_by_floor()
    {
        var w = new World(Data, "proving_pit", 3) { PitLevel = 20 };
        var p = w.AddPlayer("Novice", ClassId.Knight, 20);
        w.Populate(1);
        Assert.Equal(1, w.PitFloor);
        Assert.True(w.MonstersAlive > 0);
        foreach (var m in w.Monsters) { m.Hp = 0; m.Flags |= EntityFlags.Dead; }
        w.Tick(0.05f);
        Assert.Contains(w.Events, e => e.Type == SimEventType.PitFloorCleared && e.Value == 1);
        for (int i = 0; i < 140; i++) w.Tick(0.05f);
        Assert.Equal(2, w.PitFloor);
        Assert.False(w.Cleared);
    }

    [Fact]
    public void World_boss_blows_are_shared_and_allies_can_be_raised()
    {
        var w = new World(Data, Endgame.WorldBossZone, 9) { BossLevel = 30 };
        var knights = Enumerable.Range(0, 10).Select(i => w.AddPlayer($"K{i}", ClassId.Knight, 30)).ToList();
        w.Populate(10);
        var boss = w.Monsters.First(m => m.UniqueId == Endgame.WorldBossId);
        Assert.Equal(30, boss.Level);
        foreach (var k in knights) k.Pos = boss.Pos + new Vec2(80, 0);
        var hp = knights.Select(k => k.Hp).ToList();
        for (int i = 0; i < 60; i++) w.Tick(0.05f);
        Assert.True(knights.Count(k => k.Hp < k.MaxHp) >= 5); // everyone shares the blow, not just the target
        var down = knights[3];
        down.Hp = 0; down.Flags |= EntityFlags.Dead;
        var helper = knights.First(k => k != down && !k.Dead);
        helper.Pos = down.Pos;
        Assert.Null(w.ReviveAlly(helper, down.Id));
        Assert.False(down.Dead);
        var field = new World(Data, "bloodied_fields", 1);
        var a = field.AddPlayer("A", ClassId.Knight, 3);
        Assert.NotNull(field.ReviveAlly(a, 999)); // nowhere else
    }

    [Fact]
    public void Dailies_and_instabilities_are_stable_for_a_day_and_a_week()
    {
        var day = Endgame.Today(new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(Endgame.Pledges(Data, day).Select(t => t.Id), Endgame.Pledges(Data, day).Select(t => t.Id));
        Assert.Equal(3, Endgame.Pledges(Data, day).Count);
        Assert.Equal(4, Endgame.Endeavours("Hugh", day).Count);
        var monday = new DateTime(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc);
        var week = Endgame.ThisWeek(monday);
        Assert.Equal(2, week.Select(i => i.Id).Distinct().Count());
        Assert.Equal(week, Endgame.ThisWeek(monday.AddDays(5)));
        var (zone, ends, id) = Endgame.MistNow(Data, monday, 60);
        Assert.True(zone.MistEligible);
        Assert.True(ends > monday);
        Assert.NotEqual(id, Endgame.MistNow(Data, monday.AddHours(1), 60).Id);
    }

    [Fact]
    public void Old_saves_load_and_new_progress_round_trips()
    {
        var oldJson = """{"Name":"Old","Class":0,"Level":5,"Equipped":[{"Id":1,"Name":"Coif","Slot":2,"Rarity":1,"ItemLevel":3,"Damage":0,"Defense":4,"Weapon":0,"Properties":{"health":2}}],"Inventory":[]}""";
        var rec = JsonSerializer.Deserialize<CharacterRecord>(oldJson)!;
        var w = new World(Data, Data.Town.Id, 1);
        var p = w.AddPlayer(rec.Name, rec.Class, rec.Level);
        rec.ApplyTo(p);
        Assert.Single(p.Equipped[ItemSlot.Helm].Sockets!); // sockets added to old gear
        p.Extras.Runes.Add(new Rune { Id = 5, Kind = "vigour", Level = 7 });
        p.Extras.Give(Materials.SoulCore, 3);
        p.Extras.Relics.Add("cleave");
        var back = JsonSerializer.Deserialize<CharacterRecord>(JsonSerializer.Serialize(CharacterRecord.From(p)))!;
        Assert.Equal(7, back.Extras!.Runes.Single().Level);
        Assert.Equal(3, back.Extras.Has(Materials.SoulCore));
        Assert.Contains("cleave", back.Extras.Relics);
        var p2 = new World(Data, Data.Town.Id, 2).AddPlayer("Old", ClassId.Knight, 5);
        back.ApplyTo(p2);
        Assert.Contains("cleave", Data.KnownSpells(p2.Skills, p2.Extras.Relics).Select(s => s.Id));
    }
}
