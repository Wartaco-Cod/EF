namespace TacoFactory.Core.Math;

/// <summary>2D vector used for world positions, velocities and screen coordinates.</summary>
public readonly record struct Vec2(float X, float Y)
{
    public static readonly Vec2 Zero = new(0, 0);
    public static readonly Vec2 One = new(1, 1);

    public float LengthSquared => X * X + Y * Y;
    public float Length => MathF.Sqrt(LengthSquared);

    public Vec2 Normalized()
    {
        float len = Length;
        return len < 1e-6f ? Zero : new Vec2(X / len, Y / len);
    }

    public static float Distance(Vec2 a, Vec2 b) => (a - b).Length;
    public static float Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
    public static Vec2 Lerp(Vec2 a, Vec2 b, float t) => a + (b - a) * t;

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, float s) => new(a.X * s, a.Y * s);
    public static Vec2 operator *(float s, Vec2 a) => new(a.X * s, a.Y * s);
    public static Vec2 operator /(Vec2 a, float s) => new(a.X / s, a.Y / s);
}
