using Silk.NET.OpenGL;
using StbTrueTypeSharp;

namespace TacoFactory.Client.Rendering;

/// <summary>
/// A TrueType font baked into an RGBA atlas at one pixel size. Covers Latin-1, so names like Mère and Grève render.
/// Text can switch color inline: "{#C9A227}gold words{/} back to the base color".
/// </summary>
public sealed class BitmapFont : IDisposable
{
    private const int FirstChar = 32, CharCount = 224; // U+0020 .. U+00FF
    private readonly StbTrueType.stbtt_bakedchar[] _chars = new StbTrueType.stbtt_bakedchar[CharCount];
    private readonly int _atlasW, _atlasH;

    public Texture2D Atlas { get; }
    public float PixelHeight { get; }
    public float LineHeight => PixelHeight * 1.2f;
    public float Ascent => PixelHeight * 0.78f;

    public BitmapFont(GL gl, string key, string ttfPath, float pixelHeight, int atlasSize = 1024)
    {
        PixelHeight = pixelHeight;
        _atlasW = _atlasH = atlasSize;
        byte[] ttf = File.ReadAllBytes(ttfPath);
        var alpha = new byte[atlasSize * atlasSize];
        bool ok = StbTrueType.stbtt_BakeFontBitmap(ttf, 0, pixelHeight, alpha, atlasSize, atlasSize, FirstChar, CharCount, _chars);
        if (!ok) Console.WriteLine($"[font] {Path.GetFileName(ttfPath)} {pixelHeight}px: atlas too small, some glyphs missing");
        var rgba = new byte[alpha.Length * 4];
        for (int i = 0; i < alpha.Length; i++)
        {
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 255;
            rgba[i * 4 + 3] = alpha[i];
        }
        Atlas = Texture2D.FromPixels(gl, key, atlasSize, atlasSize, rgba);
    }

    public float Measure(string text)
    {
        float w = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (SkipCode(text, ref i)) continue;
            int c = text[i] - FirstChar;
            if (c >= 0 && c < CharCount) w += _chars[c].xadvance;
        }
        return w;
    }

    /// <summary>Draws text with its top-left at (x, y). Returns the width drawn.</summary>
    public float Draw(SpriteBatch batch, string text, float x, float y, Color color, bool shadow = true)
    {
        if (shadow) DrawRun(batch, text, x + 2, y + 2, new Color(0, 0, 0, (byte)(color.A * 0.7f)), forceColor: true);
        return DrawRun(batch, text, x, y, color, forceColor: false);
    }

    public float DrawCentered(SpriteBatch batch, string text, float cx, float y, Color color, bool shadow = true) =>
        Draw(batch, text, cx - Measure(text) / 2, y, color, shadow);

    /// <summary>Text centred on (cx, cy) at a scale (damage numbers that pop in and settle).</summary>
    public void DrawCenteredScaled(SpriteBatch batch, string text, float cx, float cy, Color color, float scale)
    {
        float w = Measure(text) * scale, x = cx - w / 2, y = cy - PixelHeight * scale / 2;
        DrawRun(batch, text, x + 2 * scale, y + 2 * scale, new Color(0, 0, 0, (byte)(color.A * 0.75f)), forceColor: true, scale);
        DrawRun(batch, text, x, y, color, forceColor: false, scale);
    }

    private float DrawRun(SpriteBatch batch, string text, float x, float y, Color baseColor, bool forceColor, float scale = 1f)
    {
        float pen = x, baseline = y + Ascent * scale;
        var color = baseColor;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '{' && TryReadCode(text, i, out int len, out Color? code))
            {
                if (!forceColor) color = code ?? baseColor;
                i += len - 1;
                continue;
            }
            int c = text[i] - FirstChar;
            if (c < 0 || c >= CharCount) continue;
            var g = _chars[c];
            float w = g.x1 - g.x0, h = g.y1 - g.y0;
            if (w > 0 && h > 0)
                batch.Draw(Atlas, scale == 1f ? MathF.Round(pen + g.xoff) : pen + g.xoff * scale, scale == 1f ? MathF.Round(baseline + g.yoff) : baseline + g.yoff * scale, w * scale, h * scale,
                    g.x0 / (float)_atlasW, g.y0 / (float)_atlasH, g.x1 / (float)_atlasW, g.y1 / (float)_atlasH,
                    forceColor ? baseColor : color with { A = baseColor.A });
            pen += g.xadvance * scale;
        }
        return pen - x;
    }

    private static bool SkipCode(string text, ref int i)
    {
        if (text[i] != '{' || !TryReadCode(text, i, out int len, out _)) return false;
        i += len - 1;
        return true;
    }

    // "{#RRGGBB}" or "{#RRGGBBAA}" sets a color; "{/}" returns to the base color.
    private static bool TryReadCode(string text, int at, out int length, out Color? color)
    {
        color = null;
        length = 0;
        if (at + 2 < text.Length && text[at + 1] == '/' && text[at + 2] == '}') { length = 3; return true; }
        foreach (int n in new[] { 8, 6 })
        {
            int close = at + 2 + n;
            if (close < text.Length && text[at + 1] == '#' && text[close] == '}')
            {
                try { color = Color.FromHex(text.Substring(at + 1, n + 1)); length = n + 3; return true; }
                catch (FormatException) { }
            }
        }
        return false;
    }

    public void Dispose() => Atlas.Dispose();
}
