using TacoFactory.Core.Math;

namespace TacoFactory.Client.Rendering;

/// <summary>One particle. Positions are in world units on the ground plane; Z is height above the ground.</summary>
public struct Particle
{
    public Vec2 Pos, Vel;
    public float Z, VZ;
    /// <summary>Downward pull on Z (world units/s²); negative floats up (embers, motes).</summary>
    public float Gravity;
    /// <summary>Fraction of velocity lost per second.</summary>
    public float Drag;
    public float Age, Life;
    public float Size0, Size1;
    public Color C0, C1;
    public bool Glow;     // drawn additively into the emissive target (sparks, flames, magic)
    public bool Streak;   // stretched along its motion (sparks, rain)
    public bool Stain;    // stays where it lands and fades slowly (blood)
    public bool Ring;     // drawn as an expanding ring (shockwaves)
    public float Wobble;  // sideways sway (embers, snow)
    public float Seed;
}

/// <summary>
/// A pooled particle system: fixed capacity, no allocation per particle, oldest particles give way when full.
/// Particles live in world space; the caller supplies the world→screen mapping and the zoom when drawing.
/// </summary>
public sealed class ParticleSystem
{
    private readonly Particle[] _p;
    private int _count;
    private readonly Random _rng = new();

    public ParticleSystem(int capacity = 6000) => _p = new Particle[capacity];

    public int Count => _count;
    public Random Rng => _rng;

    public void Clear() => _count = 0;

    public void Add(in Particle p)
    {
        if (_count == _p.Length)
        {
            // Full: replace a random older one rather than drop the new effect.
            _p[_rng.Next(_p.Length)] = p;
            return;
        }
        _p[_count++] = p;
    }

    public void Update(float dt)
    {
        for (int i = 0; i < _count; i++)
        {
            ref var p = ref _p[i];
            p.Age += dt;
            if (p.Age >= p.Life) { _p[i] = _p[--_count]; i--; continue; }
            if (p.Stain && p.Z <= 0) continue; // landed: stays put while it fades
            float drag = MathF.Max(0, 1 - p.Drag * dt);
            p.Vel *= drag;
            p.VZ = p.VZ * drag - p.Gravity * dt;
            p.Pos += p.Vel * dt;
            if (p.Wobble != 0) p.Pos += new Vec2(MathF.Sin(p.Age * 2.3f + p.Seed) * p.Wobble * dt, 0);
            p.Z += p.VZ * dt;
            if (p.Z < 0 && p.Gravity > 0)
            {
                p.Z = 0;
                if (p.Stain) { p.Vel = Vec2.Zero; p.VZ = 0; p.Life = p.Age + 3.5f; p.Size0 = p.Size1 = p.Size1 * 1.6f; p.C0 = p.C1; }
                else { p.VZ = -p.VZ * 0.3f; p.Vel *= 0.5f; }
            }
        }
    }

    /// <summary>Draws the alpha (glow = false) or additive (glow = true) particles.</summary>
    public void Draw(SpriteBatch batch, Texture2D dot, Texture2D ring, Func<Vec2, Vec2> toScreen, float zoom, bool glow)
    {
        for (int i = 0; i < _count; i++)
        {
            ref var p = ref _p[i];
            if (p.Glow != glow) continue;
            float t = p.Age / p.Life;
            float size = (p.Size0 + (p.Size1 - p.Size0) * t) * zoom;
            var c = Lerp(p.C0, p.C1, t);
            if (p.Stain && p.Z <= 0) c = c with { A = (byte)(c.A * MathF.Min(1, (p.Life - p.Age) / 1.5f)) };
            var s = toScreen(p.Pos);
            float sy = s.Y - p.Z * zoom;
            if (s.X < -200 || s.X > 2120 || sy < -200 || sy > 1280) continue;
            if (p.Ring)
                batch.Draw(ring, s.X - size, sy - size * 0.55f, size * 2, size * 1.1f, c);
            else if (p.Streak)
            {
                float vx = p.Vel.X, vy = p.Vel.Y - p.VZ;
                float speed = MathF.Sqrt(vx * vx + vy * vy);
                float len = MathF.Max(size, speed * 0.035f * zoom);
                batch.DrawRotated(dot, s.X, sy, len, size, MathF.Atan2(vy, vx), c);
            }
            else if (p.Stain && p.Z <= 0)
                batch.Draw(dot, s.X - size / 2, sy - size * 0.25f, size, size * 0.5f, c); // splat lies flat
            else
                batch.Draw(dot, s.X - size / 2, sy - size / 2, size, size, c);
        }
    }

    public static Color Lerp(Color a, Color b, float t) => new(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t), (byte)(a.A + (b.A - a.A) * t));
}
