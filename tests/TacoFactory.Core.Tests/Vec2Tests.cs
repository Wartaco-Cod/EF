using TacoFactory.Core.Math;

namespace TacoFactory.Core.Tests;

public class Vec2Tests
{
    [Fact]
    public void Normalized_has_unit_length()
    {
        var v = new Vec2(3, 4).Normalized();
        Assert.Equal(1f, v.Length, 5);
        Assert.Equal(0.6f, v.X, 5);
    }

    [Fact]
    public void Normalized_zero_stays_zero() => Assert.Equal(Vec2.Zero, Vec2.Zero.Normalized());

    [Fact]
    public void Lerp_halfway() => Assert.Equal(new Vec2(5, 10), Vec2.Lerp(Vec2.Zero, new Vec2(10, 20), 0.5f));

    [Fact]
    public void Distance_is_symmetric() =>
        Assert.Equal(Vec2.Distance(new Vec2(1, 1), new Vec2(4, 5)), Vec2.Distance(new Vec2(4, 5), new Vec2(1, 1)));
}
