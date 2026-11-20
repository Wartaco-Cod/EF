using TacoFactory.Core.Data;
using TacoFactory.Core.Math;

namespace TacoFactory.Core.Sim;

/// <summary>
/// Solid scenery. Each decor piece whose prop has a footprint becomes an ellipse on the ground (world units, the
/// same squash as the camera's view). Moving entities are pushed out of it, which slides them along its edge; a
/// mostly-blocked step tries turning either way so figures work their way around props instead of sticking.
/// </summary>
public sealed partial class World
{
    private readonly record struct Obstacle(Vec2 Centre, float Rx, float Ry);

    private List<Obstacle>? _obstacles;

    private List<Obstacle> Obstacles => _obstacles ??= BuildObstacles();

    private List<Obstacle> BuildObstacles()
    {
        var list = new List<Obstacle>();
        foreach (var d in Zone.Decor)
            if (_data.Props.TryGetValue(d.Prop, out var prop) && prop.Footprint is { Length: 2 } f && f[0] > 0 && f[1] > 0)
                list.Add(new Obstacle(new Vec2(d.X, d.Y), f[0], f[1]));
        return list;
    }

    /// <summary>True if a body of radius <paramref name="r"/> at <paramref name="p"/> overlaps any scenery.</summary>
    public bool Blocked(Vec2 p, float r)
    {
        foreach (var o in Obstacles)
        {
            float k = o.Rx / o.Ry;
            var d = new Vec2(p.X - o.Centre.X, (p.Y - o.Centre.Y) * k);
            float min = o.Rx + r;
            if (d.LengthSquared < min * min) return true;
        }
        return false;
    }

    /// <summary>Moves a point out of every footprint it overlaps (twice, for props that touch).</summary>
    private Vec2 PushOut(Vec2 p, float r)
    {
        var obs = Obstacles;
        if (obs.Count == 0) return p;
        for (int pass = 0; pass < 2; pass++)
        {
            bool moved = false;
            foreach (var o in obs)
            {
                // Work in a space where the ellipse is a circle of radius Rx.
                float k = o.Rx / o.Ry;
                var d = new Vec2(p.X - o.Centre.X, (p.Y - o.Centre.Y) * k);
                float min = o.Rx + r;
                float len2 = d.LengthSquared;
                if (len2 >= min * min) continue;
                var n = len2 > 1e-6f ? d / MathF.Sqrt(len2) : new Vec2(0, 1);
                var outP = n * min;
                p = new Vec2(o.Centre.X + outP.X, o.Centre.Y + outP.Y / k);
                moved = true;
            }
            if (!moved) break;
        }
        return p;
    }

    private Vec2 ClampAndPush(Vec2 p, float r)
    {
        p = new Vec2(System.Math.Clamp(p.X, r, Zone.Width - r), System.Math.Clamp(p.Y, r, Zone.Height - r));
        return PushOut(p, r);
    }

    private static Vec2 Rotate(Vec2 v, float radians)
    {
        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return new Vec2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    /// <summary>
    /// Steps by <paramref name="delta"/>, sliding along scenery. If scenery stops most of the step, tries turning
    /// 50 and then 90 degrees either way and takes whichever gets furthest along the wanted direction.
    /// </summary>
    private void MoveClamped(Entity e, Vec2 delta)
    {
        var start = e.Pos;
        var best = ClampAndPush(start + delta, e.Radius);
        float want = delta.Length;
        if (want < 1e-4f || Obstacles.Count == 0) { e.Pos = best; return; }
        var dir = delta / want;
        float Progress(Vec2 to) => Vec2.Dot(to - start, dir);
        if (Progress(best) < want * 0.35f)
        {
            foreach (float deg in new[] { 50f, -50f, 90f, -90f })
            {
                var tryTo = ClampAndPush(start + Rotate(delta, deg * MathF.PI / 180f), e.Radius);
                // Prefer a step that still heads roughly the right way; sideways steps count by distance moved.
                float score = Progress(tryTo) + 0.3f * Vec2.Distance(tryTo, start);
                if (score > Progress(best) + 0.3f * Vec2.Distance(best, start)) best = tryTo;
                if (Progress(best) >= want * 0.6f) break;
            }
        }
        e.Pos = best;
    }
}
