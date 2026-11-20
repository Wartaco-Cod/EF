using TacoFactory.Client.Rendering;
using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;
using TacoFactory.Net;
using Color = TacoFactory.Client.Rendering.Color;

namespace TacoFactory.Client;

/// <summary>
/// Presentation layer: lighting, post-processing, particles and camera feel. None of it touches the simulation;
/// it reads the server's events and snapshots and dresses them up. Turned off with Settings → Lighting and effects.
/// </summary>
public sealed partial class GameClient
{
    private PostProcess? _post;
    private bool _fxFailed;
    private readonly ParticleSystem _particles = new(7000);
    private Texture2D _softDot = null!, _softDotPm = null!, _lightTex = null!, _ringTex = null!, _beamTex = null!;
    private readonly List<Action> _overlay = [], _emissive = [];
    private readonly List<(Vec2 At, float Radius, Color Colour, float Intensity)> _frameLights = [];
    private readonly List<FlashLight> _flashes = [];
    private readonly List<Bolt> _bolts = [];
    private readonly HashSet<(Vec2, double)> _landedTelegraphs = [];

    // Camera feel.
    private Vec2 _camSmooth, _camLead, _lastMePos;
    private bool _camInit;
    private float _trauma;
    private double _animClock, _hitStopUntil;
    private float _hurtPulse, _whiteFlash;
    private int _lastHp = -1;
    private double _zoneArrivedAt = -10;

    private sealed class FlashLight { public Vec2 At; public float Radius; public Color Colour; public float Intensity; public double Born, Life; }
    private sealed class Bolt { public int From, To; public Vec2 A, B; public double Born, Life; public Palette Pal; public bool Arrived; public float Size; }

    private readonly record struct Palette(Color Core, Color Edge, Color Light);

    private static readonly Palette Fire = new(Color.FromHex("#FFE9A0"), Color.FromHex("#FF4A0A00"), Color.FromHex("#FF8A30"));
    private static readonly Palette Ice = new(Color.FromHex("#F0FDFF"), Color.FromHex("#3FA8FF00"), Color.FromHex("#7FC8FF"));
    private static readonly Palette Storm = new(Color.FromHex("#FFFFFF"), Color.FromHex("#6F8FFF00"), Color.FromHex("#B0C8FF"));
    private static readonly Palette Holy = new(Color.FromHex("#FFF8D8"), Color.FromHex("#E8A830" + "00"), Color.FromHex("#FFD878"));
    private static readonly Palette Arcane = new(Color.FromHex("#F4E4FF"), Color.FromHex("#8A4AFF00"), Color.FromHex("#B07AFF"));
    private static readonly Palette Ward = new(Color.FromHex("#EEF4FF"), Color.FromHex("#6F98D000"), Color.FromHex("#A8C4F0"));
    private static readonly Palette Grave = new(Color.FromHex("#E0FFB0"), Color.FromHex("#3A7A2000"), Color.FromHex("#8FD860"));
    private static readonly Palette Sparks = new(Color.FromHex("#FFFBE8"), Color.FromHex("#FF7A1A00"), Color.FromHex("#FFC070"));

    private static Palette PaletteFor(string spell) => spell switch
    {
        "firebolt" or "greek_fire" or "inferno" => Fire,
        "frost_nova" => Ice,
        "thunderbolt" => Storm,
        "smite" or "holy_light" or "sanctuary" or "resurrection" or "mend" or "consecrated_blade" => Holy,
        "arcane_dart" or "confound" or "mirror_step" or "mana_siphon" or "revelation" => Arcane,
        "iron_skin" or "aegis" or "repel" or "bulwark" => Ward,
        "wither" or "leech" or "plague_cloud" or "bone_ward" or "deaths_toll" => Grave,
        _ => Sparks,
    };

    /// <summary>Animation clock: runs slow for a few frames on a heavy blow (hit-stop), so impacts land.</summary>
    private double AnimNow => _animClock;

    private bool FxOn => _settings.Effects && !_fxFailed;

    // ------------------------------------------------------------------ setup

    private void InitFx()
    {
        _softDot = MakeRadial("__softdot", 64, (d) => MathF.Pow(MathF.Max(0, 1 - d), 2.2f), premultiplied: false);
        _softDotPm = MakeRadial("__softdotpm", 64, (d) => MathF.Pow(MathF.Max(0, 1 - d), 1.6f), premultiplied: true);
        _lightTex = MakeRadial("__light", 128, (d) => { float f = MathF.Max(0, 1 - d * d); return f * f; }, premultiplied: false);
        _ringTex = MakeRadial("__ring", 128, (d) => MathF.Max(0, 1 - MathF.Abs(d - 0.82f) / 0.14f) * (d < 1 ? 1 : 0), premultiplied: false);
        var beam = new byte[32 * 256 * 4];
        for (int y = 0; y < 256; y++)
        for (int x = 0; x < 32; x++)
        {
            float v = y / 255f, h = MathF.Abs(x - 15.5f) / 16f;
            float a = MathF.Pow(v, 1.6f) * MathF.Pow(MathF.Max(0, 1 - h * h), 2f);
            int i = (y * 32 + x) * 4;
            beam[i] = beam[i + 1] = beam[i + 2] = 255; beam[i + 3] = (byte)(a * 255);
        }
        _beamTex = Texture2D.FromPixels(_gl, "__beam", 32, 256, beam);
        try { _post = new PostProcess(_gl); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _fxFailed = true;
            Console.WriteLine($"[client] lighting and effects unavailable on this graphics card: {ex.Message}");
        }
    }

    private Texture2D MakeRadial(string key, int size, Func<float, float> falloff, bool premultiplied)
    {
        var px = new byte[size * size * 4];
        float r = size / 2f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float d = MathF.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r)) / r;
            float a = Math.Clamp(falloff(d), 0, 1);
            int i = (y * size + x) * 4;
            byte c = premultiplied ? (byte)(a * 255) : (byte)255;
            px[i] = px[i + 1] = px[i + 2] = c;
            px[i + 3] = (byte)(a * 255);
        }
        return Texture2D.FromPixels(_gl, key, size, size, px);
    }

    // ------------------------------------------------------------------ zone look

    private static readonly AmbienceDef Daylight = new();

    private AmbienceDef Look(WelcomeInfo w)
    {
        var a = _data.Zones.TryGetValue(w.ZoneId, out var z) && z.Ambience is { } za ? za : Daylight;
        return _extras is { MistHere: true } e && e.Zone == w.ZoneId ? MistLook(w.ZoneId, a) : a;
    }

    private static (float R, float G, float B) Rgb(string hex, float k = 1f)
    {
        var c = Color.FromHex(hex);
        return (c.R / 255f * k, c.G / 255f * k, c.B / 255f * k);
    }

    private GradeSettings GradeFor(AmbienceDef a, RemoteEntity me)
    {
        var g = GradeSettings.Neutral;
        (g.TintR, g.TintG, g.TintB) = Rgb(a.Tint);
        g.Saturation = a.Saturation; g.Contrast = a.Contrast; g.Vignette = a.Vignette; g.Bloom = a.Bloom;
        g.BloomThreshold = a.Level < 0.6f ? 0.7f : 0.88f;
        float hpFrac = me.Current.MaxHp > 0 ? me.Current.Hp / (float)me.Current.MaxHp : 1;
        float low = me.Current.Dead ? 0 : Math.Clamp((0.35f - hpFrac) / 0.35f, 0, 1);
        g.Hurt = MathF.Max(_hurtPulse * 0.6f, low * (0.55f + 0.25f * MathF.Sin((float)Now * 5f)));
        g.Flash = _whiteFlash;
        g.Time = (float)Now;
        if (me.Current.Dead) { g.Saturation *= 0.15f; g.Exposure = 0.8f; }
        return g;
    }

    // ------------------------------------------------------------------ per-frame update

    private void UpdateFx(double dt)
    {
        float fdt = (float)Math.Min(dt, 0.1);
        _animClock += dt * (Now < _hitStopUntil ? 0.08 : 1.0);
        _trauma = MathF.Max(0, _trauma - fdt * 1.6f);
        _hurtPulse = MathF.Max(0, _hurtPulse - fdt * 2.2f);
        _whiteFlash = MathF.Max(0, _whiteFlash - fdt * 1.8f);
        _particles.Update(fdt);
        _flashes.RemoveAll(f => Now - f.Born > f.Life);

        foreach (var b in _bolts) UpdateBolt(b, fdt);
        _bolts.RemoveAll(b => b.Arrived);

        // Boss slams land when their warning circle runs out.
        foreach (var t in _telegraphs)
            if (Now >= t.Until - 0.03 && _landedTelegraphs.Add((t.At, t.Born))) Impact(t.At, t.Radius);
        if (_landedTelegraphs.Count > 64) _landedTelegraphs.Clear();

        if (_conn.Welcome is { } w && _conn.Entities.TryGetValue(_conn.YourId, out var me))
        {
            if (_lastHp >= 0 && me.Current.Hp < _lastHp && !me.Current.Dead) _hurtPulse = 1;
            _lastHp = me.Current.Hp;
            if (FxOn) AmbientParticles(Look(w), fdt);
        }
    }

    private void Shake(float amount) => _trauma = MathF.Min(1, _trauma + amount);

    /// <summary>Camera: follows the hero smoothly, leads a little in the direction of travel, and shakes on impacts.</summary>
    private Vec2 CameraFor(WelcomeInfo w, Vec2 mePos, double dt)
    {
        var hv = HalfView;
        if (!_camInit || Vec2.Distance(mePos, _lastMePos) > 400)
        {
            _camSmooth = mePos; _camLead = Vec2.Zero; _camInit = true;
        }
        float fdt = (float)Math.Clamp(dt, 0, 0.1);
        var vel = fdt > 0 ? (mePos - _lastMePos) / fdt : Vec2.Zero;
        _lastMePos = mePos;
        var wantLead = vel.LengthSquared > 100 ? vel.Normalized() * 70 : Vec2.Zero;
        _camLead += (wantLead - _camLead) * (1 - MathF.Exp(-fdt * 2.2f));
        var target = mePos + _camLead;
        _camSmooth += (target - _camSmooth) * (1 - MathF.Exp(-fdt * 9f));
        var cam = new Vec2(Math.Clamp(_camSmooth.X, hv.X, Math.Max(hv.X, w.ZoneWidth - hv.X)),
                           Math.Clamp(_camSmooth.Y, hv.Y, Math.Max(hv.Y, w.ZoneHeight - hv.Y)));
        if (_trauma > 0 && FxOn)
        {
            float s = _trauma * _trauma * 16f, t = (float)Now * 31f;
            cam += new Vec2((MathF.Sin(t) + MathF.Sin(t * 2.31f + 1.7f)) * 0.5f * s, (MathF.Sin(t * 1.73f + 0.4f) + MathF.Sin(t * 2.9f)) * 0.5f * s);
        }
        return cam;
    }

    // ------------------------------------------------------------------ the frame

    /// <summary>Draws the world through the lighting pipeline (or straight to the screen with effects off), then the HUD.</summary>
    private void RenderWorldFrame(WelcomeInfo w, RemoteEntity me, double dt, float scale, float ox, float oy)
    {
        var mePos = me.PositionAt(_conn.Now, TickSeconds);
        _camera = CameraFor(w, mePos, dt);
        var look = Look(w);
        _overlay.Clear(); _emissive.Clear(); _frameLights.Clear();

        bool fx = FxOn;
        int rw = (int)(VW * scale), rh = (int)(VH * scale);
        if (fx)
        {
            try { _post!.Resize(rw, rh); }
            catch (InvalidOperationException ex) { _fxFailed = true; fx = false; Console.WriteLine($"[client] effects off: {ex.Message}"); }
        }
        if (fx) { _post!.Scene.Bind(); _post.Scene.Clear(0, 0, 0, 1); }

        _batch.Begin(VW, VH);
        DrawWorld(w);
        if (fx) _particles.Draw(_batch, _softDot, _ringTex, ToScreen, Zoom, glow: false);
        _batch.End();

        if (fx)
        {
            // Light: the zone's ambient level, cloud shadows drifting over it, then every light added on top.
            var (ar, ag, ab) = Rgb(look.Ambient, look.Level);
            _post!.Light.Bind(); _post.Light.Clear(ar, ag, ab, 1);
            _batch.Begin(VW, VH);
            if (look.Clouds > 0) DrawCloudShadows(look.Clouds);
            _batch.SetBlend(BlendMode.Additive);
            if (look.HeroLight > 0)
                foreach (var r in _conn.Entities.Values)
                    if (r.Current.Kind == EntityKind.Player && !r.Current.Dead)
                        AddLight(r.PositionAt(_conn.Now, TickSeconds) + new Vec2(0, -10), look.HeroLight, Color.FromHex("#FFD9A0"), r.Current.Id == _conn.YourId ? 0.95f : 0.7f);
            foreach (var f in _flashes)
            {
                float t = (float)((Now - f.Born) / f.Life);
                AddLight(f.At, f.Radius * (0.85f + 0.15f * t), f.Colour, f.Intensity * (1 - t) * (1 - t));
            }
            foreach (var (at, radius, colour, intensity) in _frameLights) DrawLight(at, radius, colour, intensity);
            _batch.End();

            _post.Emissive.Bind(); _post.Emissive.Clear(0, 0, 0, 1);
            _batch.Begin(VW, VH);
            _batch.SetBlend(BlendMode.Additive);
            foreach (var a in _emissive) a();
            _particles.Draw(_batch, _softDot, _ringTex, ToScreen, Zoom, glow: true);
            _batch.End();

            _post.Composite(GradeFor(look, me), (int)ox, (int)oy, rw, rh);
        }
        else
        {
            _batch.Begin(VW, VH);
            _particles.Draw(_batch, _softDot, _ringTex, ToScreen, Zoom, glow: false);
            _batch.SetBlend(BlendMode.Additive);
            foreach (var a in _emissive) a();
            _particles.Draw(_batch, _softDot, _ringTex, ToScreen, Zoom, glow: true);
            _batch.End();
        }

        _batch.Begin(VW, VH);
        // Arriving somewhere new fades in from black.
        float arrive = (float)((Now - _zoneArrivedAt) / 0.7);
        if (arrive < 1) _batch.FillRect(0, 0, VW, VH, new Color(0, 0, 0, (byte)(255 * (1 - arrive) * (1 - arrive))));
        foreach (var a in _overlay) a();
        DrawFloaters();
        DrawHud(w, me);
        if (_mapOpen) DrawMinimap(w, 260, 140, 1400, 788, 0.75f);
        _batch.End();
    }

    /// <summary>Queues a light for this frame (drawn into the light target). Radius in world units.</summary>
    private void AddLight(Vec2 world, float radius, Color colour, float intensity) => _frameLights.Add((world, radius, colour, intensity));

    private void DrawLight(Vec2 world, float radius, Color colour, float intensity)
    {
        if (intensity <= 0.01f) return;
        var p = ToScreen(world);
        float r = radius * Zoom;
        if (p.X < -r || p.X > VW + r || p.Y < -r || p.Y > VH + r) return;
        // A draw adds at most 1.0, so strong lights take two passes; the second is tighter, for a hot centre.
        float a1 = MathF.Min(1, intensity);
        _batch.Draw(_lightTex, p.X - r, p.Y - r * 0.8f, r * 2, r * 1.6f, colour with { A = (byte)(a1 * 255) });
        if (intensity > 1)
        {
            float a2 = MathF.Min(1, intensity - 1);
            _batch.Draw(_lightTex, p.X - r * 0.55f, p.Y - r * 0.44f, r * 1.1f, r * 0.88f, colour with { A = (byte)(a2 * 255) });
        }
    }

    private void Flash(Vec2 at, float radius, Color colour, float intensity, double life) =>
        _flashes.Add(new FlashLight { At = at, Radius = radius, Colour = colour, Intensity = intensity, Born = Now, Life = life });

    private void DrawCloudShadows(float amount)
    {
        _batch.SetBlend(BlendMode.Multiply);
        const float cell = 1100;
        var wind = new Vec2(22, 9) * (float)Now;
        var tl = _camera - HalfView - wind;
        int cx0 = (int)MathF.Floor(tl.X / cell) - 1, cy0 = (int)MathF.Floor(tl.Y / cell) - 1;
        for (int cy = cy0; cy < cy0 + 4; cy++)
        for (int cx = cx0; cx < cx0 + 5; cx++)
        {
            uint h = Hash(cx, cy);
            float ox = (h & 1023) / 1023f, oy = ((h >> 10) & 1023) / 1023f, sz = 0.7f + ((h >> 20) & 255) / 255f * 0.9f;
            var world = new Vec2((cx + ox) * cell, (cy + oy) * cell) + wind;
            var p = ToScreen(world);
            float r = cell * 0.55f * sz * Zoom;
            float k = amount * 0.55f; // how much this blob darkens at its centre
            byte a = (byte)(255 * k);
            _batch.Draw(_softDotPm, p.X - r, p.Y - r * 0.7f, r * 2, r * 1.4f, new Color(0, 0, 0, a));
        }
        _batch.SetBlend(BlendMode.Alpha);
    }

    private static uint Hash(int x, int y)
    {
        uint h = (uint)(x * 374761393 + y * 668265263);
        h = (h ^ (h >> 13)) * 1274126177;
        return h ^ (h >> 16);
    }

    /// <summary>Breaks up a repeating floor with large soft patches of grime and wear, fixed to the ground.</summary>
    private void DrawGroundVariation(WelcomeInfo w)
    {
        const float cell = 260;
        var tl = _camera - HalfView;
        int cx0 = (int)MathF.Floor(tl.X / cell) - 1, cy0 = (int)MathF.Floor(tl.Y / cell) - 1;
        int nx = (int)(VW / Zoom / cell) + 3, ny = (int)(VH / Zoom / cell) + 3;
        for (int cy = cy0; cy < cy0 + ny; cy++)
        for (int cx = cx0; cx < cx0 + nx; cx++)
        {
            uint h = Hash(cx * 7 + 3, cy * 13 + 5);
            if ((h & 3) == 0) continue;
            float ox = ((h >> 2) & 255) / 255f, oy = ((h >> 10) & 255) / 255f, sz = 0.6f + ((h >> 18) & 255) / 255f * 1.1f;
            var world = new Vec2((cx + ox) * cell, (cy + oy) * cell);
            if (world.X < 0 || world.Y < 0 || world.X > w.ZoneWidth || world.Y > w.ZoneHeight) continue;
            var p = ToScreen(world);
            float r = cell * 0.75f * sz * Zoom;
            bool light = (h >> 27) % 5 == 0;
            var c = light ? new Color(255, 236, 200, 14) : new Color(10, 6, 2, (byte)(30 + (h >> 28) * 3));
            _batch.Draw(_softDot, p.X - r, p.Y - r * 0.7f, r * 2, r * 1.4f, c);
        }
    }

    // ------------------------------------------------------------------ props, markers and loot that glow

    private void DecorLight(DecorDef d, PropDef prop)
    {
        if (prop.Light is not { } L) return;
        var at = new Vec2(d.X, d.Y);
        float seed = d.X * 0.013f + d.Y * 0.007f;
        float t = (float)Now;
        float flick = L.Flicker > 0 ? 1 - L.Flicker * 0.35f * (0.5f + 0.5f * MathF.Sin(t * 9.1f + seed) * MathF.Sin(t * 5.3f + seed * 2)) : 1;
        var colour = Color.FromHex(L.Colour);
        AddLight(at, L.Radius * (0.97f + 0.03f * flick), colour, L.Intensity * flick);
        if (L.Flame)
        {
            var p = ToScreen(at);
            float hy = L.Height * PixelsPerMetre * Zoom;
            float r = 30 * Zoom * flick;
            _emissive.Add(() =>
            {
                // Halo around the flames, then a small hot core.
                _batch.Draw(_softDot, p.X - r, p.Y - hy - r * 0.8f, r * 2, r * 1.6f, colour with { A = 95 });
                _batch.Draw(_softDot, p.X - r * 0.3f, p.Y - hy - r * 0.38f, r * 0.6f, r * 0.76f, new Color(255, 214, 150, 170));
            });
        }
        if (L.Embers > 0 && FxOn && _particles.Rng.NextDouble() < L.Embers / Math.Max(30, LastFps))
            _particles.Add(new Particle
            {
                Pos = at + new Vec2((float)_particles.Rng.NextDouble() * 40 - 20, 0), Z = L.Height * 69 + 5,
                Vel = new Vec2((float)_particles.Rng.NextDouble() * 20 - 10, 0), VZ = 40 + (float)_particles.Rng.NextDouble() * 50,
                Gravity = -15, Drag = 0.3f, Life = 1.6f + (float)_particles.Rng.NextDouble() * 1.4f, Size0 = 3.2f, Size1 = 0.8f,
                C0 = Color.FromHex("#FFD080"), C1 = Color.FromHex("#FF400000"), Glow = true, Wobble = 22, Seed = (float)_particles.Rng.NextDouble() * 9,
            });
    }

    private void ZoneLights(WelcomeInfo w)
    {
        if (!_data.Zones.TryGetValue(w.ZoneId, out var z)) return;
        foreach (var pl in z.Lights)
        {
            var p = ToScreen(new Vec2(pl.X, pl.Y));
            float reach = pl.Light.Radius * Zoom + 200;
            if (p.X < -reach || p.X > VW + reach || p.Y < -reach || p.Y > VH + reach) continue;
            DecorLight(new DecorDef { X = pl.X, Y = pl.Y }, new PropDef { Light = pl.Light });
        }
    }

    private void LootGlow(EntityState s, Vec2 screen, Vec2 world)
    {
        if (s.TypeIndex == 255)
        {
            AddLight(world, 70, Color.FromHex("#FFD060"), 0.35f);
            return;
        }
        var rarity = (Rarity)s.TypeIndex;
        if (rarity < Rarity.Uncommon) return;
        var c = Color.FromHex(RarityHex(rarity));
        AddLight(world, rarity >= Rarity.Rare ? 140 : 90, c, rarity >= Rarity.Rare ? 0.7f : 0.4f);
        float pulse = 0.8f + 0.2f * MathF.Sin((float)Now * 3 + s.Id);
        _emissive.Add(() =>
        {
            _batch.Draw(_softDot, screen.X - 34 * Zoom, screen.Y - 18 * Zoom, 68 * Zoom, 36 * Zoom, c with { A = (byte)(90 * pulse) });
            if (rarity >= Rarity.Rare)
            {
                // A pillar of light marks rare and better drops from across the screen.
                float h = (rarity >= Rarity.Epic ? 260 : 190) * Zoom;
                _batch.Draw(_beamTex, screen.X - 9 * Zoom, screen.Y - h, 18 * Zoom, h, c with { A = (byte)(170 * pulse) });
                _batch.Draw(_beamTex, screen.X - 3 * Zoom, screen.Y - h * 0.9f, 6 * Zoom, h * 0.9f, new Color(255, 255, 255, (byte)(120 * pulse)));
            }
        });
        if (FxOn && _particles.Rng.NextDouble() < (rarity >= Rarity.Rare ? 6 : 2) / Math.Max(30, LastFps))
            _particles.Add(new Particle
            {
                Pos = world + new Vec2((float)_particles.Rng.NextDouble() * 30 - 15, 0), Z = 4, VZ = 30 + (float)_particles.Rng.NextDouble() * 40,
                Gravity = -10, Life = 1.2f, Size0 = 3f, Size1 = 0.5f, C0 = c, C1 = c with { A = 0 }, Glow = true,
            });
    }

    // ------------------------------------------------------------------ air

    private void AmbientParticles(AmbienceDef a, float dt)
    {
        if (a.Particles.Length == 0) return;
        var rng = _particles.Rng;
        var tl = _camera - HalfView - new Vec2(100, 100);
        float vw = VW / Zoom + 200, vh = VH / Zoom + 300;
        float rate = a.ParticleDensity * a.Particles switch { "snow" => 90, "rain" => 220, "ash" => 40, "embers" => 30, "dust" => 18, "fireflies" => 6, "spores" => 12, _ => 0 };
        int n = (int)(rate * dt) + (rng.NextDouble() < rate * dt % 1 ? 1 : 0);
        for (int i = 0; i < n; i++)
        {
            var pos = tl + new Vec2((float)rng.NextDouble() * vw, (float)rng.NextDouble() * vh);
            float r = (float)rng.NextDouble();
            var p = a.Particles switch
            {
                "snow" => new Particle { Pos = pos, Z = 260 + r * 200, VZ = -(55 + r * 40), Vel = new Vec2(-18 + r * 10, 6), Life = (260 + r * 200) / (55 + r * 40), Size0 = 3 + r * 2.5f, Size1 = 3, C0 = new Color(240, 246, 255, 210), C1 = new Color(240, 246, 255, 120), Wobble = 30, Seed = r * 9 },
                "rain" => new Particle { Pos = pos, Z = 360, VZ = -950, Vel = new Vec2(-90, 40), Life = 0.38f, Size0 = 1.6f, Size1 = 1.6f, C0 = new Color(170, 190, 220, 110), C1 = new Color(170, 190, 220, 60), Streak = true },
                "ash" => new Particle { Pos = pos, Z = 200 + r * 160, VZ = -(22 + r * 18), Vel = new Vec2(14, 4), Life = (200 + r * 160) / (22 + r * 18), Size0 = 2.4f + r * 2, Size1 = 2, C0 = new Color(70, 64, 60, 170), C1 = new Color(90, 84, 80, 0), Wobble = 20, Seed = r * 9 },
                "embers" => new Particle { Pos = pos, Z = r * 40, VZ = 30 + r * 40, Gravity = -6, Vel = new Vec2(10 + r * 15, -5), Life = 3 + r * 2, Size0 = 2.6f, Size1 = 0.6f, C0 = Color.FromHex("#FFC060"), C1 = Color.FromHex("#FF300000"), Glow = true, Wobble = 30, Seed = r * 9 },
                "dust" => new Particle { Pos = pos, Z = 30 + r * 180, VZ = 2 - r * 4, Vel = new Vec2(4 - r * 8, 3 - r * 6), Life = 7, Size0 = 2.2f, Size1 = 2.2f, C0 = new Color(255, 236, 200, 0), C1 = new Color(255, 236, 200, 0), Glow = true, Wobble = 6, Seed = r * 9 },
                "fireflies" => new Particle { Pos = pos, Z = 20 + r * 90, VZ = 6 - r * 12, Vel = new Vec2(12 - r * 24, 8 - r * 16), Life = 4, Size0 = 3.2f, Size1 = 2.4f, C0 = Color.FromHex("#E8FF8A"), C1 = Color.FromHex("#A8FF4000"), Glow = true, Wobble = 26, Seed = r * 9 },
                _ => new Particle { Pos = pos, Z = r * 30, VZ = 8 + r * 10, Gravity = -2, Life = 6, Size0 = 2.4f, Size1 = 1.2f, C0 = new Color(190, 255, 170, 90), C1 = new Color(160, 230, 150, 0), Glow = true, Wobble = 14, Seed = r * 9 },
            };
            if (a.Particles == "dust") { p.C0 = new Color(255, 236, 200, 60); p.C1 = new Color(255, 236, 200, 0); }
            _particles.Add(p);
        }
    }

    private void Footstep(Vec2 world, AmbienceDef look)
    {
        if (!FxOn) return;
        var c = Color.FromHex(look.Dust);
        var rng = _particles.Rng;
        for (int i = 0; i < 2; i++)
            _particles.Add(new Particle
            {
                Pos = world + new Vec2((float)rng.NextDouble() * 16 - 8, (float)rng.NextDouble() * 6 - 3), Z = 2,
                Vel = new Vec2((float)rng.NextDouble() * 30 - 15, (float)rng.NextDouble() * 8 - 4), VZ = 12, Gravity = -4, Drag = 2,
                Life = 0.7f, Size0 = 7, Size1 = 15, C0 = c with { A = 70 }, C1 = c with { A = 0 },
            });
    }

    // ------------------------------------------------------------------ combat and spell effects

    private Vec2? PosOf(int id) => _conn.Entities.TryGetValue(id, out var r) ? r.PositionAt(_conn.Now, TickSeconds) : null;

    private void FxForEvent(SimEvent e)
    {
        var rng = _particles.Rng;
        float R() => (float)rng.NextDouble();
        switch (e.Type)
        {
            case SimEventType.Damage:
            {
                if (PosOf(e.Target) is not { } tp) break;
                var sp = PosOf(e.Source) ?? tp - new Vec2(1, 0);
                var dir = (tp - sp).LengthSquared > 1 ? (tp - sp).Normalized() : new Vec2(1, 0);
                bool crit = e.Text == "crit";
                bool meHurt = e.Target == _conn.YourId, meHit = e.Source == _conn.YourId;
                int sparks = crit ? 18 : 8, blood = crit ? 12 : 5;
                for (int i = 0; i < sparks; i++)
                {
                    var v = (dir + new Vec2(R() - 0.5f, R() - 0.5f) * 1.6f).Normalized() * (220 + R() * 360);
                    _particles.Add(new Particle
                    {
                        Pos = tp, Z = 75 + R() * 20, Vel = v, VZ = 40 + R() * 220, Gravity = 900, Drag = 2.5f,
                        Life = 0.18f + R() * 0.2f, Size0 = 4.5f, Size1 = 1.2f, C0 = Sparks.Core, C1 = Sparks.Edge, Glow = true, Streak = true,
                    });
                }
                for (int i = 0; i < blood; i++)
                {
                    var v = (dir + new Vec2(R() - 0.5f, R() - 0.5f)).Normalized() * (80 + R() * 200);
                    _particles.Add(new Particle
                    {
                        Pos = tp, Z = 70 + R() * 25, Vel = v, VZ = 60 + R() * 160, Gravity = 1000, Drag = 1,
                        Life = 2.5f, Size0 = 5 + R() * 3, Size1 = 4, C0 = new Color(120, 10, 8, 230), C1 = new Color(70, 6, 4, 200), Stain = true,
                    });
                }
                Flash(tp, crit ? 200 : 130, Color.FromHex("#FFC890"), crit ? 1.3f : 0.7f, 0.12);
                if (meHurt) Shake(0.16f + MathF.Min(0.35f, e.Value / MathF.Max(1, (float)(_conn.Entities[_conn.YourId].Current.MaxHp)) * 2f));
                if (meHit && crit) { Shake(0.12f); _hitStopUntil = Now + 0.065; }
                else if (meHit) _hitStopUntil = Math.Max(_hitStopUntil, Now + 0.025);
                break;
            }
            case SimEventType.Blocked:
                if (PosOf(e.Target) is { } bp)
                {
                    for (int i = 0; i < 10; i++)
                        _particles.Add(new Particle
                        {
                            Pos = bp + new Vec2(0, 2), Z = 70, Vel = new Vec2(R() - 0.5f, R() - 0.5f).Normalized() * (200 + R() * 200), VZ = 60 + R() * 120,
                            Gravity = 700, Drag = 3, Life = 0.25f, Size0 = 4, Size1 = 1, C0 = Ward.Core, C1 = Ward.Edge, Glow = true, Streak = true,
                        });
                    Flash(bp, 120, Ward.Light, 0.8f, 0.1);
                    if (e.Target == _conn.YourId) Shake(0.08f);
                }
                break;
            case SimEventType.Death:
                if (PosOf(e.Target) is { } dp)
                {
                    bool boss = _conn.Entities.TryGetValue(e.Target, out var dr) && (dr.Current.Flags & EntityFlags.Superunique) != 0;
                    _particles.Add(new Particle { Pos = dp, Z = 2, Life = 0.6f, Size0 = 20, Size1 = boss ? 220 : 110, C0 = new Color(120, 104, 86, 120), C1 = new Color(120, 104, 86, 0), Ring = true });
                    for (int i = 0; i < (boss ? 40 : 16); i++)
                        _particles.Add(new Particle
                        {
                            Pos = dp + new Vec2(R() * 30 - 15, R() * 10 - 5), Z = 6, Vel = new Vec2(R() - 0.5f, (R() - 0.5f) * 0.6f).Normalized() * (60 + R() * 120),
                            VZ = 20 + R() * 40, Gravity = 60, Drag = 2.2f, Life = 0.9f + R() * 0.6f, Size0 = 10, Size1 = 26,
                            C0 = new Color(110, 98, 84, 110), C1 = new Color(110, 98, 84, 0),
                        });
                    for (int i = 0; i < (boss ? 14 : 5); i++)
                        _particles.Add(new Particle
                        {
                            Pos = dp, Z = 40 + R() * 30, Vel = new Vec2(R() - 0.5f, R() - 0.5f).Normalized() * (60 + R() * 140), VZ = 80 + R() * 120,
                            Gravity = 1000, Life = 3f, Size0 = 7, Size1 = 6, C0 = new Color(110, 8, 6, 230), C1 = new Color(70, 6, 4, 210), Stain = true,
                        });
                    if (boss) { Shake(0.6f); _whiteFlash = 0.35f; Flash(dp, 420, Color.FromHex("#FFE0B0"), 2f, 0.6); _hitStopUntil = Now + 0.25; }
                    else if (e.Source == _conn.YourId) { Shake(0.06f); _hitStopUntil = Math.Max(_hitStopUntil, Now + 0.05); }
                }
                break;
            case SimEventType.LevelUp:
                if (PosOf(e.Target) is { } lp)
                {
                    Pillar(lp, Holy, 60);
                    _particles.Add(new Particle { Pos = lp, Z = 2, Life = 0.8f, Size0 = 30, Size1 = 260, C0 = Holy.Core with { A = 220 }, C1 = Holy.Edge, Ring = true, Glow = true });
                    Flash(lp, 380, Holy.Light, 2f, 1.2);
                    if (e.Target == _conn.YourId) _whiteFlash = 0.22f;
                }
                break;
            case SimEventType.Heal:
                if (PosOf(e.Target) is { } hp)
                {
                    for (int i = 0; i < 14; i++)
                        _particles.Add(new Particle
                        {
                            Pos = hp + new Vec2(R() * 60 - 30, R() * 20 - 10), Z = R() * 40, VZ = 60 + R() * 70, Gravity = -20, Drag = 0.5f,
                            Life = 1 + R() * 0.5f, Size0 = 4.5f, Size1 = 1, C0 = Color.FromHex("#D8FFC8"), C1 = Color.FromHex("#60D06000"), Glow = true, Wobble = 18, Seed = R() * 9,
                        });
                    Flash(hp, 160, Color.FromHex("#9AF0A0"), 0.9f, 0.5);
                }
                break;
            case SimEventType.Revived:
                if (PosOf(e.Target) is { } rp) { Pillar(rp, Holy, 40); Flash(rp, 300, Holy.Light, 1.6f, 1.0); }
                break;
            case SimEventType.SkillUsed:
                SpellFx(e);
                break;
        }
    }

    private void Pillar(Vec2 at, Palette pal, int count)
    {
        var rng = _particles.Rng;
        for (int i = 0; i < count; i++)
        {
            float a = (float)rng.NextDouble() * MathF.Tau, r = 20 + (float)rng.NextDouble() * 40;
            _particles.Add(new Particle
            {
                Pos = at + new Vec2(MathF.Cos(a) * r, MathF.Sin(a) * r * 0.5f), Z = (float)rng.NextDouble() * 20,
                VZ = 140 + (float)rng.NextDouble() * 220, Gravity = -40, Drag = 0.8f, Life = 1.1f + (float)rng.NextDouble() * 0.6f,
                Size0 = 5, Size1 = 1, C0 = pal.Core, C1 = pal.Edge, Glow = true, Wobble = 20, Seed = (float)rng.NextDouble() * 9,
            });
        }
    }

    private void Burst(Vec2 at, Palette pal, float radius, int count, float height = 40)
    {
        var rng = _particles.Rng;
        _particles.Add(new Particle { Pos = at, Z = 2, Life = 0.45f, Size0 = 20, Size1 = radius * 1.1f, C0 = pal.Core with { A = 230 }, C1 = pal.Edge, Ring = true, Glow = true });
        for (int i = 0; i < count; i++)
        {
            float a = (float)rng.NextDouble() * MathF.Tau, sp = radius * (1.5f + (float)rng.NextDouble() * 2f);
            _particles.Add(new Particle
            {
                Pos = at, Z = height * (float)rng.NextDouble(), Vel = new Vec2(MathF.Cos(a), MathF.Sin(a) * 0.6f) * sp,
                VZ = 40 + (float)rng.NextDouble() * 160, Gravity = 300, Drag = 3.5f, Life = 0.35f + (float)rng.NextDouble() * 0.35f,
                Size0 = 7, Size1 = 1.5f, C0 = pal.Core, C1 = pal.Edge, Glow = true, Streak = (float)rng.NextDouble() < 0.4f,
            });
        }
        Flash(at, radius * 1.6f + 80, pal.Light, 1.6f, 0.35);
    }

    private void Impact(Vec2 at, float radius)
    {
        var rng = _particles.Rng;
        _particles.Add(new Particle { Pos = at, Z = 2, Life = 0.55f, Size0 = 20, Size1 = radius * 1.2f, C0 = new Color(140, 120, 96, 200), C1 = new Color(140, 120, 96, 0), Ring = true });
        for (int i = 0; i < 28; i++)
        {
            float a = (float)rng.NextDouble() * MathF.Tau;
            _particles.Add(new Particle
            {
                Pos = at + new Vec2(MathF.Cos(a), MathF.Sin(a) * 0.6f) * radius * 0.5f * (float)rng.NextDouble(), Z = 4,
                Vel = new Vec2(MathF.Cos(a), MathF.Sin(a) * 0.6f) * (80 + (float)rng.NextDouble() * 160), VZ = 30 + (float)rng.NextDouble() * 60,
                Gravity = 90, Drag = 2.5f, Life = 1 + (float)rng.NextDouble() * 0.6f, Size0 = 14, Size1 = 34, C0 = new Color(120, 106, 88, 130), C1 = new Color(120, 106, 88, 0),
            });
        }
        Burst(at, Fire, radius * 0.6f, 16, 10);
        if (_conn.Entities.TryGetValue(_conn.YourId, out var me) && Vec2.Distance(me.Current.Pos, at) < 700) Shake(0.45f);
    }

    private void SpellFx(SimEvent e)
    {
        if (!_data.Spells.TryGetValue(e.Text, out var spell) || PosOf(e.Source) is not { } sp) return;
        var pal = PaletteFor(spell.Id);
        var tp = PosOf(e.Target) ?? sp;
        switch (spell.Kind)
        {
            case SpellKind.Bolt:
                _bolts.Add(new Bolt { From = e.Source, To = e.Target, A = sp, B = tp, Born = Now, Life = Math.Clamp(Vec2.Distance(sp, tp) / 1400, 0.08, 0.35), Pal = pal, Size = spell.Id == "thunderbolt" ? 9 : 12 });
                if (spell.Id == "thunderbolt") { Flash(tp, 360, Storm.Light, 2f, 0.15); Shake(0.1f); }
                break;
            case SpellKind.Drain:
                _bolts.Add(new Bolt { From = e.Target, To = e.Source, A = tp, B = sp, Born = Now, Life = 0.3, Pal = pal, Size = 9 });
                break;
            case SpellKind.Burst:
                Burst(tp, pal, MathF.Max(80, spell.Radius), 40);
                if (spell.Id is "greek_fire" or "inferno" or "plague_cloud") Smoke(tp, spell.Radius, spell.Id == "plague_cloud" ? new Color(80, 120, 50, 110) : new Color(50, 44, 40, 120));
                if (e.Source == _conn.YourId) Shake(0.12f);
                break;
            case SpellKind.Nova:
                Burst(sp, pal, MathF.Max(120, spell.Radius), 60, 20);
                if (e.Source == _conn.YourId) Shake(0.15f);
                break;
            case SpellKind.HealArea or SpellKind.BuffArea:
                _particles.Add(new Particle { Pos = sp, Z = 2, Life = 0.7f, Size0 = 30, Size1 = MathF.Max(120, spell.Radius), C0 = pal.Core with { A = 200 }, C1 = pal.Edge, Ring = true, Glow = true });
                Pillar(sp, pal, 30);
                Flash(sp, spell.Radius + 80, pal.Light, 1.2f, 0.7);
                break;
            case SpellKind.BuffSelf:
                Pillar(sp, pal, 24);
                Flash(sp, 180, pal.Light, 1f, 0.6);
                break;
            case SpellKind.Blink:
                Burst(sp, pal, 70, 24);
                break;
            case SpellKind.Heal or SpellKind.Revive:
                Flash(sp, 120, pal.Light, 0.6f, 0.3); // the heal itself arrives as a Heal / Revived event
                break;
        }
    }

    private void Smoke(Vec2 at, float radius, Color c)
    {
        var rng = _particles.Rng;
        for (int i = 0; i < 18; i++)
        {
            float a = (float)rng.NextDouble() * MathF.Tau, r = radius * 0.6f * (float)rng.NextDouble();
            _particles.Add(new Particle
            {
                Pos = at + new Vec2(MathF.Cos(a) * r, MathF.Sin(a) * r * 0.6f), Z = 10, VZ = 25 + (float)rng.NextDouble() * 30, Gravity = -6, Drag = 1,
                Vel = new Vec2(MathF.Cos(a), MathF.Sin(a) * 0.6f) * 20, Life = 1.6f + (float)rng.NextDouble(), Size0 = 30, Size1 = 70, C0 = c, C1 = c with { A = 0 },
            });
        }
    }

    private void UpdateBolt(Bolt b, float dt)
    {
        if (PosOf(b.To) is { } live) b.B = live;
        float t = (float)Math.Clamp((Now - b.Born) / b.Life, 0, 1);
        var head = b.A + (b.B - b.A) * t;
        var rng = _particles.Rng;
        float z = 70;
        int trail = (int)MathF.Ceiling(dt * 260);
        for (int i = 0; i < trail; i++)
            _particles.Add(new Particle
            {
                Pos = head + new Vec2((float)rng.NextDouble() * 8 - 4, (float)rng.NextDouble() * 8 - 4), Z = z + (float)rng.NextDouble() * 6 - 3,
                Vel = new Vec2((float)rng.NextDouble() * 40 - 20, (float)rng.NextDouble() * 40 - 20), VZ = (float)rng.NextDouble() * 30 - 15,
                Life = 0.22f + (float)rng.NextDouble() * 0.12f, Size0 = b.Size, Size1 = 1.5f, C0 = b.Pal.Core, C1 = b.Pal.Edge, Glow = true,
            });
        _particles.Add(new Particle { Pos = head, Z = z, Life = 0.05f, Size0 = b.Size * 2.2f, Size1 = b.Size * 2f, C0 = b.Pal.Core, C1 = b.Pal.Core, Glow = true });
        Flash(head, 150, b.Pal.Light, 0.9f, 0.05);
        if (t >= 1)
        {
            b.Arrived = true;
            Burst(b.B, b.Pal, 60, 18, 70);
        }
    }

    // ------------------------------------------------------------------ text over the world

    private void DrawFloaters()
    {
        if (!_settings.DamageNumbers) return;
        foreach (var f in _floaters)
        {
            float age = (float)(Now - f.Born);
            // Pop in large, settle, then rise and drift aside as they fade.
            float pop = 1 + (f.Big ? 1.0f : 0.6f) * MathF.Max(0, 1 - age / 0.14f);
            float drift = (f.Text.GetHashCode() & 1) == 0 ? 1 : -1;
            var p = ToScreen(f.Pos) + new Vec2(drift * age * 34, -age * 85 + age * age * 30);
            var c = f.Color with { A = (byte)(255 * Math.Clamp(1.5f - age * 1.4f, 0, 1)) };
            (f.Big ? _title : _ui).DrawCenteredScaled(_batch, f.Text, p.X, p.Y, c, pop);
        }
    }
}
