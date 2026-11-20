using TacoFactory.Core.Content;
using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;
using Xunit;

namespace TacoFactory.Core.Tests;

public class ObstacleTests
{
    private static readonly GameData Data = GameData.Load(ContentRoot.Locate(AppContext.BaseDirectory)!);

    private static (ZoneDef zone, DecorDef piece, float rx) SolidPiece(string prefix)
    {
        var zone = Data.Zones["bloodied_fields"];
        var piece = zone.Decor.First(d => d.Prop.StartsWith(prefix) && Data.Props[d.Prop].Footprint is not null);
        return (zone, piece, Data.Props[piece.Prop].Footprint![0]);
    }

    [Fact]
    public void Scenery_with_a_footprint_is_solid()
    {
        var (zone, piece, _) = SolidPiece("hay_cart");
        var w = new World(Data, zone.Id, 1);
        Assert.True(w.Blocked(new Vec2(piece.X, piece.Y), 22));
        Assert.False(w.Blocked(new Vec2(piece.X, piece.Y - 400), 22));
    }

    [Fact]
    public void Walking_into_a_prop_never_enters_it_and_steers_round_it()
    {
        var (zone, piece, rx) = SolidPiece("hay_cart");
        var w = new World(Data, zone.Id, 1);
        var p = w.AddPlayer("Kay", ClassId.Knight, 60, new Vec2(piece.X - rx - 120, piece.Y));
        float startX = p.Pos.X;
        for (int i = 0; i < 200; i++)
        {
            w.SetInput(p.Id, new PlayerInput(new Vec2(1, 0), PlayerAction.None, 0, 0, default));
            w.Tick(0.05f);
            Assert.False(w.Blocked(p.Pos, p.Radius - 0.5f), $"inside the prop at tick {i}: {p.Pos}");
        }
        Assert.True(p.Pos.X > piece.X + rx, $"did not get past the prop: started {startX}, ended {p.Pos.X}, prop at {piece.X}");
    }
}
