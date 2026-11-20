using TacoFactory.Core.Content;
using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;

namespace TacoFactory.Core.Tests;

public class SkillTests
{
    private static readonly GameData Data = GameData.Load(ContentRoot.Locate(AppContext.BaseDirectory)!);

    [Fact]
    public void Eleven_skills_and_no_tracking_or_lock_picking()
    {
        Assert.Equal(11, Data.SkillList.Count);
        Assert.Equal(5, Data.SkillList.Count(s => s.Kind == SkillKind.Weapon));
        Assert.Equal(5, Data.SkillList.Count(s => s.Kind == SkillKind.SpellField));
        Assert.DoesNotContain(Data.SkillList, s => s.Id.Contains("track") || s.Id.Contains("lock"));
    }

    [Fact]
    public void Each_spell_field_has_five_spells_at_levels_1_3_5_7_9()
    {
        foreach (var field in Data.SkillList.Where(s => s.Kind == SkillKind.SpellField))
            Assert.Equal([1, 3, 5, 7, 9], Data.Spells.Values.Where(sp => sp.Field == field.Id).Select(sp => sp.RequiredLevel).OrderBy(x => x));
    }

    [Fact]
    public void Classes_start_with_three_skills_and_matching_gear()
    {
        var w = new World(Data, "bloodied_fields", 1);
        var knight = w.AddPlayer("Kay", ClassId.Knight);
        Assert.Equal(3, knight.Skills.Count);
        Assert.Equal(WeaponType.OneHandedSword, knight.Equipped[ItemSlot.Weapon].Weapon);
        Assert.True(knight.Equipped.ContainsKey(ItemSlot.Shield));
        var wizard = w.AddPlayer("Merle", ClassId.Wizard);
        Assert.Equal(WeaponType.Dagger, wizard.Equipped[ItemSlot.Weapon].Weapon);
        Assert.Equal(["firebolt", "arcane_dart"], wizard.SpellBar);
    }

    [Fact]
    public void Skills_max_out_at_level_ten()
    {
        var w = new World(Data, "bloodied_fields", 2);
        var p = w.AddPlayer("Kay", ClassId.Knight);
        p.BuildPoints = 1000;
        for (int i = 0; i < 20; i++) w.LearnSkill(p, "shield");
        Assert.Equal(10, p.SkillLevel("shield"));
        Assert.Equal(World.LearnResult.AtMaximum, w.LearnSkill(p, "shield"));
    }

    [Fact]
    public void Off_class_skills_cost_more()
    {
        Assert.Equal(4, Data.LearnCost("Knight", "shield"));
        Assert.Equal(8, Data.LearnCost("Knight", "alchemy"));
        Assert.Equal(4, Data.LearnCost("Wizard", "alchemy"));
        Assert.Equal(4, Data.LearnCost("Wizard", "dagger"));
        Assert.Equal(6, Data.LearnCost("Wizard", "heavy_two_handed"));
    }

    [Fact]
    public void Learning_needs_build_points_and_unlocks_spells()
    {
        var w = new World(Data, "bloodied_fields", 3);
        var p = w.AddPlayer("Merle", ClassId.Wizard);
        Assert.Equal(World.LearnResult.NotEnoughPoints, w.LearnSkill(p, "alchemy"));
        p.BuildPoints = 8;
        Assert.Equal(World.LearnResult.Learned, w.LearnSkill(p, "alchemy"));
        Assert.Equal(World.LearnResult.Learned, w.LearnSkill(p, "alchemy"));
        Assert.Equal(3, p.SkillLevel("alchemy"));
        Assert.Contains("frost_nova", p.SpellBar);
        Assert.Equal(0, p.BuildPoints);
    }

    [Fact]
    public void Level_ups_grant_build_points()
    {
        var w = new World(Data, "bloodied_fields", 4);
        var p = w.AddPlayer("Kay", ClassId.Knight, level: 10);
        Assert.Equal(9, p.BuildPoints);
    }

    [Fact]
    public void Untrained_weapons_hit_30_percent_softer()
    {
        var w = new World(Data, "bloodied_fields", 5);
        var p = w.AddPlayer("Kay", ClassId.Knight);
        p.Equipped[ItemSlot.Weapon] = Loot.MakeItem(new Random(1), 99, 1, Rarity.Common, weapon: WeaponType.HeavyTwoHanded);
        Assert.Equal(0.70, w.WeaponSkillMultiplier(p, out _), 6);
        p.Skills["heavy_two_handed"] = 10;
        Assert.Equal(1.60, w.WeaponSkillMultiplier(p, out _), 6);
    }

    [Fact]
    public void Two_handed_weapons_disable_the_shield()
    {
        var w = new World(Data, "bloodied_fields", 6);
        var p = w.AddPlayer("Kay", ClassId.Knight);
        Assert.True(w.BlockChance(p) > 0);
        p.Equipped[ItemSlot.Weapon] = Loot.MakeItem(new Random(1), 99, 1, Rarity.Common, weapon: WeaponType.LightTwoHanded);
        Assert.Equal(0, w.BlockChance(p));
    }

    [Fact]
    public void Block_chance_grows_with_shield_skill_and_is_capped()
    {
        var w = new World(Data, "bloodied_fields", 7);
        var p = w.AddPlayer("Kay", ClassId.Knight);
        Assert.Equal(0.075, w.BlockChance(p), 6);
        p.Skills["shield"] = 10;
        Assert.Equal(0.30, w.BlockChance(p), 6);
        for (int i = 0; i < 5; i++) p.MasteryFor("shield").ApplyScroll();
        Assert.True(w.BlockChance(p) <= Data.SkillRules.MaxBlockChance);
    }

    [Fact]
    public void Spells_build_their_field_mastery()
    {
        var w = new World(Data, "bloodied_fields", 8);
        var p = w.AddPlayer("Merle", ClassId.Wizard);
        w.Populate();
        var target = w.Monsters.OrderBy(m => Vec2.Distance(m.Pos, p.Pos)).First();
        p.Pos = target.Pos + new Vec2(200, 0);
        for (int i = 0; i < 60 && !target.Dead; i++)
        {
            w.SetInput(p.Id, new PlayerInput(Vec2.Zero, PlayerAction.UseSkill, 1, target.Id, target.Pos)); // slot 1 = Firebolt
            w.Tick(0.05f);
        }
        var m = p.MasteryFor("alchemy");
        Assert.True(m.Hits > 0 || m.Level > 0);
    }

    [Fact]
    public void Buffs_expire()
    {
        var w = new World(Data, "bloodied_fields", 9);
        var p = w.AddPlayer("Ysolde", ClassId.Healer); // Warding 1 = Iron Skin, bar slot 2
        Assert.Contains("iron_skin", p.SpellBar);
        int slot = p.SpellBar.IndexOf("iron_skin") + 1;
        w.SetInput(p.Id, new PlayerInput(Vec2.Zero, PlayerAction.UseSkill, (byte)slot, p.Id, p.Pos));
        w.Tick(0.05f);
        Assert.True(p.BuffDefense > 0);
        w.SetInput(p.Id, default);
        for (int i = 0; i < 20 * 31; i++) w.Tick(0.05f);
        Assert.Equal(0, p.BuffDefense);
    }
}
