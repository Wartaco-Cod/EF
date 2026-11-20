using System.Text.Json;
using System.Text.Json.Serialization;

namespace TacoFactory.Client.Rendering;

/// <summary>
/// An animated, 8-direction sprite sheet made by tools/art/pack_sheet.py: one PNG plus a JSON description.
/// Rows are animation × direction, columns are frames. Drawn with the feet on the anchor point.
/// </summary>
public sealed class SpriteSheet
{
    public sealed class Anim
    {
        public int Row { get; set; }
        public int Frames { get; set; }
        public float Fps { get; set; } = 12;
        public bool Loop { get; set; } = true;
        public int? HitFrame { get; set; }
    }

    public sealed class Meta
    {
        public string Image { get; set; } = "";
        public int FrameWidth { get; set; }
        public int FrameHeight { get; set; }
        public int Directions { get; set; } = 8;
        public float[] DirectionAngles { get; set; } = [];
        public int[] Anchor { get; set; } = [0, 0];
        /// <summary>World height a frame covers in metres (rendered sheets). 0 = unknown: the caller's size is used.</summary>
        public float Metres { get; set; }
        public Dictionary<string, Anim> Animations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };

    public Meta Info { get; }
    public Texture2D Texture { get; }

    public SpriteSheet(Meta info, Texture2D texture) { Info = info; Texture = texture; }

    public static Meta LoadMeta(string jsonPath) =>
        JsonSerializer.Deserialize<Meta>(File.ReadAllText(jsonPath), Json) ?? throw new InvalidDataException(jsonPath);

    public bool Has(string anim) => Info.Animations.ContainsKey(anim);

    /// <summary>Picks the direction whose angle is closest to the facing angle (degrees, 0 = right, 90 = down).</summary>
    public int DirectionFor(float facingDegrees)
    {
        int best = 0;
        float bestDiff = float.MaxValue;
        for (int d = 0; d < Info.DirectionAngles.Length; d++)
        {
            float diff = MathF.Abs(((facingDegrees - Info.DirectionAngles[d]) % 360 + 540) % 360 - 180);
            if (diff < bestDiff) { bestDiff = diff; best = d; }
        }
        return best;
    }

    /// <summary>Draws one frame with the feet at (x, y). <paramref name="seconds"/> is time since the animation started.</summary>
    public void Draw(SpriteBatch batch, string anim, float facingDegrees, double seconds, float x, float y, float scale, Color tint)
    {
        if (!Info.Animations.TryGetValue(anim, out var a) && !Info.Animations.TryGetValue("idle", out a))
            a = Info.Animations.Values.First();
        int frame = (int)(seconds * a.Fps);
        frame = a.Loop ? frame % System.Math.Max(1, a.Frames) : System.Math.Min(frame, a.Frames - 1);
        int dir = DirectionFor(facingDegrees);
        int row = a.Row + dir;
        float fw = Info.FrameWidth, fh = Info.FrameHeight;
        float u0 = frame * fw / Texture.Width, v0 = row * fh / Texture.Height;
        float u1 = (frame + 1) * fw / Texture.Width, v1 = (row + 1) * fh / Texture.Height;
        batch.Draw(Texture, x - Info.Anchor[0] * scale, y - Info.Anchor[1] * scale, fw * scale, fh * scale, u0, v0, u1, v1, tint);
    }
}
