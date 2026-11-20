namespace TacoFactory.Client.Rendering;

/// <summary>Packed 32-bit RGBA color. Strings in the UI use the same hex form (#RRGGBB or #RRGGBBAA).</summary>
public readonly record struct Color(byte R, byte G, byte B, byte A = 255)
{
    public static readonly Color White = new(255, 255, 255);
    public static readonly Color Black = new(0, 0, 0);

    public uint Packed => (uint)(R | (G << 8) | (B << 16) | (A << 24));

    public static Color FromHex(string hex)
    {
        string s = hex.TrimStart('#');
        if (s.Length is not (6 or 8)) throw new FormatException($"Expected #RRGGBB or #RRGGBBAA, got '{hex}'.");
        byte P(int i) => byte.Parse(s.AsSpan(i, 2), System.Globalization.NumberStyles.HexNumber);
        return new Color(P(0), P(2), P(4), s.Length == 8 ? P(6) : (byte)255);
    }
}
