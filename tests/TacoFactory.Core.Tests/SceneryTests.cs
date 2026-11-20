using LiteNetLib.Utils;
using TacoFactory.Core.Content;
using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;
using TacoFactory.Net;
using Xunit;

namespace TacoFactory.Core.Tests;

/// <summary>Scenery (tools/art/zone_ambience.py) must never shut a way, and player snapshots carry the weapon in hand.</summary>
public class SceneryTests
{
    private static readonly GameData Data = GameData.Load(ContentRoot.Locate(AppContext.BaseDirectory)!);

    [Fact]
    public void Heroes_can_walk_from_arrival_to_every_exit_waypoint_and_npc()
    {
        foreach (var z in Data.Zones.Values)
        {
            var w = new World(Data, z.Id, 1);
            var start = new Vec2(z.PlayerSpawn[0], z.PlayerSpawn[1]);
            var ends = z.Exits.Select(e => (Name: "exit to " + e.To, At: new Vec2(e.X, e.Y)))
                .Concat(z.Npcs.Select(n => (Name: n.Name, At: new Vec2(n.X, n.Y))))
                .ToList();
            if (z.Waypoint is { } wp) ends.Add(("waypoint", new Vec2(wp[0], wp[1])));
            Assert.False(w.Blocked(start, 20), $"{z.Id}: arrival point is inside scenery");
            foreach (var (name, at) in ends)
            {
                // The straight way there is open, apart from the last stretch (an NPC may stand by a prop of their own).
                float len = Vec2.Distance(start, at);
                for (float d = 0; d < len - 140; d += 20)
                {
                    var p = start + (at - start) * (d / len);
                    Assert.False(w.Blocked(p, 18), $"{z.Id}: the way to {name} is blocked at {p.X:F0},{p.Y:F0}");
                }
            }
        }
    }

    [Fact]
    public void Light_bearing_scenery_has_light_and_grass_is_walk_through()
    {
        foreach (var k in new[] { "brazier_1", "torch_post_2" }) Assert.True(Data.Props[k].Light is { Flame: true }, k);
        foreach (var k in new[] { "grass_tuft_1", "grass_flowers_2", "dry_grass_1" }) Assert.Null(Data.Props[k].Footprint);
        Assert.NotNull(Data.Props["oak_tree_1"].Footprint);
    }

    [Fact]
    public void Player_snapshots_carry_class_and_weapon_family()
    {
        var e = new Entity { Id = 7, Kind = EntityKind.Player, Class = ClassId.Paladin, Hp = 10, MaxHp = 10, Level = 3 };
        e.Equipped[ItemSlot.Weapon] = Loot.MakeNamed(new Random(1), "Great Maul", 3, Rarity.Common);
        var w = new NetDataWriter();
        EntityState.From(e).Write(w);
        var s = EntityState.Read(new NetDataReader(w.Data));
        Assert.Equal(ClassId.Paladin, s.PlayerClass);
        Assert.Equal(WeaponType.HeavyTwoHanded, s.PlayerWeapon);
        e.Equipped.Clear();
        Assert.Equal(WeaponType.None, EntityState.From(e).PlayerWeapon);
    }
}
