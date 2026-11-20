using System.Diagnostics;
using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using StbImageWriteSharp;
using TacoFactory.Client.Assets;
using TacoFactory.Client.Rendering;
using TacoFactory.Core;
using TacoFactory.Core.Content;
using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;
using TacoFactory.Net;
using Color = TacoFactory.Client.Rendering.Color;

namespace TacoFactory.Client;

/// <summary>
/// The Templar Knights game client. Connects to a Taco Factory server, draws what the server's snapshots show,
/// and sends the player's inputs. It never decides an outcome itself.
/// </summary>
public sealed partial class GameClient : IDisposable
{
    private const float VW = EngineInfo.VirtualWidth, VH = EngineInfo.VirtualHeight;
    private const double TickSeconds = 1.0 / EngineInfo.TickRate;

    private readonly ClientOptions _options;
    private readonly IWindow _window;
    private GL _gl = null!;
    private IInputContext _input = null!;
    private SpriteBatch _batch = null!;
    private AssetCache _assets = null!;
    private GameData _data = null!;
    private BitmapFont _title = null!, _ui = null!, _small = null!;
    private Texture2D _disc = null!;
    private ClientConnection _conn = null!;
    private BotBrain? _autopilot;

    private Vec2 _camera;
    private int _lockedTarget;
    private bool _mapOpen, _charOpen, _invOpen, _typing;
    private PlayerInput _lastSentInput;
    private double _lastInputSentAt;
    private const float GameServerWaypointReach = 140; // a little inside the server's 160
    private Vec2? _moveTarget;   // click-to-move destination (a click on open ground walks there)
    private double _moveTargetAt;
    private readonly System.Text.StringBuilder _chatInput = new();
    private readonly float[] _localCooldown = new float[Entity.BarSlots];
    private readonly List<(float X, float Y, float W, float H, Action OnClick)> _buttons = [];
    private bool _skillsOpen, _questsOpen, _waypointsOpen;
    private int _talkTarget;
    private int _seenGeneration;
    private readonly List<(Vec2 At, float Radius, double Until, double Born)> _telegraphs = [];
    private readonly List<FloatingText> _floaters = [];
    private readonly List<(string Text, double At)> _log = [];
    private readonly Dictionary<int, double> _hitFlash = new();
    private readonly Dictionary<int, AnimState> _anim = new();
    private string? _banner;
    private double _bannerUntil;

    private readonly Stopwatch _fpsClock = Stopwatch.StartNew();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _framesThisSecond;
    private long _frameNumber;
    public double LastFps { get; private set; }

    private sealed class AnimState
    {
        public Vec2 LastPos;
        public Vec2 PrevPos;
        public double SettledAt;
        public float Facing = 90;
        public double MovingUntil, AttackAt = -10, HitAt = -10, DeathAt = -10, AnimStart, NextStep;
        public string Current = "idle";
    }

    private AnimState AnimFor(EntityState s, Vec2 pos)
    {
        if (!_anim.TryGetValue(s.Id, out var a)) _anim[s.Id] = a = new AnimState { LastPos = pos, PrevPos = pos };
        // Facing comes from movement over a few world units, not from one frame's step: at 144 fps a step is under a
        // unit, and interpolation rounding would point the figure anywhere.
        if ((pos - a.PrevPos).LengthSquared > 0.01f) a.MovingUntil = Now + 0.15;
        a.PrevPos = pos;
        var delta = pos - a.LastPos;
        if (delta.LengthSquared > 16f)
        {
            a.Facing = MathF.Atan2(delta.Y, delta.X) * 180 / MathF.PI;
            a.LastPos = pos; a.SettledAt = Now;
        }
        else if (Now - a.SettledAt > 0.4) { a.LastPos = pos; a.SettledAt = Now; } // standing or shuffling: start afresh
        if (s.Dead && a.DeathAt < 0) a.DeathAt = Now;
        if (!s.Dead) a.DeathAt = -10;
        string next = s.Dead ? "death" : AnimNow - a.AttackAt < 0.45 ? "attack" : AnimNow - a.HitAt < 0.2 ? "hit" : Now < a.MovingUntil ? "walk" : "idle";
        if (next != a.Current) { a.Current = next; a.AnimStart = AnimNow; }
        return a;
    }

    private const float PixelsPerMetre = 180f / 2.6f;
    /// <summary>
    /// World zoom: the camera shows the world at twice its native scale, so figures, scenery, ground, loot and markers
    /// all grow together. Gameplay (the server's world units) is unchanged; the screen just shows half as much ground.
    /// </summary>
    private const float Zoom = 2f;
    private const float CharacterScale = Zoom;
    /// <summary>Height above the feet of a figure's head (with helm), in metres, for names and bars.</summary>
    private const float FigureHeadMetres = 1.6f;

    /// <summary>Draws an entity from its sheet if one exists (animated, 8 directions), else its still image.</summary>
    private void DrawFigure(string key, EntityState s, Vec2 feet, float size, Color tint, float scale = 1f)
    {
        var sheet = _assets.Sheet(key);
        if (sheet is null)
        {
            float sz = size * scale;
            _batch.Draw(_assets.Texture(key), feet.X - sz / 2, feet.Y - sz + 12 * scale, sz, sz, tint);
            return;
        }
        var a = AnimFor(s, feet + _camera);
        // Rendered sheets share one world scale (about 69 px per metre), so figures keep their true sizes.
        float drawn = (sheet.Info.Metres > 0 ? sheet.Info.Metres * PixelsPerMetre : size) * scale;
        sheet.Draw(_batch, a.Current, a.Facing, AnimNow - a.AnimStart, feet.X, feet.Y, drawn / sheet.Info.FrameHeight, tint);
        if (a.Current == "walk" && Now >= a.NextStep && _conn.Welcome is { } fw)
        {
            a.NextStep = Now + 0.32;
            Footstep((feet - new Vec2(VW / 2, VH / 2)) / Zoom + _camera, Look(fw));
            if (s.Id == _conn.YourId) StepSound();
        }
    }

    private sealed class FloatingText
    {
        public required string Text;
        public Vec2 Pos;
        public Color Color;
        public double Born;
        public bool Big;
    }

    private readonly ClientSettings _settings;
    private readonly string _settingsPath;
    private string _fontRoot = "";

    public GameClient(ClientOptions options)
    {
        _options = options;
        _settingsPath = options.SettingsPath ?? ClientSettings.DefaultPath;
        _settings = ClientSettings.Load(_settingsPath);
        var opts = WindowOptions.Default with
        {
            Size = new Vector2D<int>(options.Width, options.Height),
            Title = $"Templar Knights — {EngineInfo.Name} {EngineInfo.Version}",
            VSync = options.VSync && _settings.VSync,
            WindowState = _settings.Fullscreen ? WindowState.Fullscreen : WindowState.Normal,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
        };
        _window = Window.Create(opts);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Closing += OnClosing;
    }

    public void Run() => _window.Run();

    private double Now => _clock.Elapsed.TotalSeconds;

    // ------------------------------------------------------------------ setup

    private void OnLoad()
    {
        _gl = _window.CreateOpenGL();
        _input = _window.CreateInput();
        foreach (var kb in _input.Keyboards)
        {
            kb.KeyDown += (_, key, _) => OnKey(key);
            kb.KeyChar += (_, ch) =>
            {
                if (_focus is not null) { FieldChar(ch); return; }
                if (_typing && !char.IsControl(ch) && _chatInput.Length < Msg.MaxChatLength) _chatInput.Append(ch);
            };
        }
        foreach (var mouse in _input.Mice)
        {
            mouse.MouseDown += (m, b) =>
            {
                if (b == MouseButton.Right && _conn.Welcome is not null && OverPanel(m.Position)) { RightClick(m.Position); return; }
                if (b != MouseButton.Left) return;
                if (_conn.Welcome is null) { Click(m.Position); return; } // login and character screens
                if (OverPanel(m.Position)) { BeginPress(); Click(m.Position); return; }
                _talkTarget = (Hovered(EntityKind.Npc) ?? Hovered(EntityKind.Chest))?.Current.Id ?? 0;
                if (_extras?.InWorldBoss == true && Hovered(EntityKind.Player, includeDead: true) is { Current.Dead: true } downed && downed.Current.Id != _conn.YourId)
                    _conn.Command("revive", downed.Current.Id.ToString());
                if (_conn.Welcome?.Waypoint is { } wp && Vec2.Distance(MouseWorld(), new Vec2(wp[0], wp[1])) < 130) { _waypointsOpen = true; _invOpen = _questsOpen = false; }
                _lockedTarget = DuelFoe() ?? HoveredMonster()?.Current.Id ?? 0;
            };
            mouse.MouseUp += (_, b) => { if (b == MouseButton.Left) { _lockedTarget = 0; EndPress(); } };
        }

        string root = ContentRoot.Locate(AppContext.BaseDirectory)
                      ?? ContentRoot.Locate(Environment.CurrentDirectory)
                      ?? throw new DirectoryNotFoundException("Could not find an 'assets' folder above the game.");
        _data = GameData.Load(root);
        _assets = new AssetCache(_gl, root);
        _batch = new SpriteBatch(_gl);
        _fontRoot = root;
        BakeFonts();
        InitAudio(root);
        _disc = MakeDisc(128);
        InitFx();

        if (_options.QuickPlay)
        {
            _conn = new ClientConnection(_options.Name!, _options.Class);
            _conn.Connect(_options.Host, _options.Port);
        }
        else
        {
            _conn = new ClientConnection(); // not connected until the login screen asks
            if (_options.HostGiven) _serverField.Text = _options.Port == Protocol.DefaultPort ? _options.Host : $"{_options.Host}:{_options.Port}";
        }
        if (_options.Autoplay) _autopilot = new BotBrain(_options.Class);
        _skillsOpen = _questsOpen = _options.OpenPanels;

        Console.WriteLine($"[client] {EngineInfo.Name} {EngineInfo.Version}");
        Console.WriteLine($"[client] GL {_gl.GetStringS(StringName.Version)} on {_gl.GetStringS(StringName.Renderer)}");
        if (_options.QuickPlay) Console.WriteLine($"[client] quick play on {_options.Host}:{_options.Port} as {_options.Name} the {_options.Class}");
    }

    private void OnKey(Key key)
    {
        if (_focus is not null) { FieldKey(key); return; }
        if (_conn.Welcome is null) { if (!_options.QuickPlay) FrontKey(key); return; }
        if (StoryKey(key)) return;
        if (key == Key.Escape && _openWindow != Win.None) { CloseWindow(); return; }
        if (key == Key.F11) { SetFullscreen(!_settings.Fullscreen); return; }
        if (!_typing && key == Key.Z) { _conn.Command("drink", "health"); Sfx("potion", 0.6f); return; }
        if (!_typing && key == Key.X) { _conn.Command("drink", "mana"); Sfx("potion", 0.6f); return; }
        if (!_typing && key == Key.G) { _conn.Command("guild.info"); return; }
        if (_typing)
        {
            switch (key)
            {
                case Key.Enter or Key.KeypadEnter:
                    _conn.SendChat(_chatInput.ToString());
                    _chatInput.Clear();
                    _typing = false;
                    break;
                case Key.Escape:
                    _chatInput.Clear();
                    _typing = false;
                    break;
                case Key.Backspace when _chatInput.Length > 0:
                    _chatInput.Length--;
                    break;
            }
            return;
        }
        switch (key)
        {
            case Key.Enter or Key.KeypadEnter: _typing = true; break;
            case Key.Tab: _mapOpen = !_mapOpen; break;
            case Key.Q: _questsOpen = !_questsOpen; if (_questsOpen) _invOpen = false; break;
            case Key.V: _waypointsOpen = !_waypointsOpen; if (_waypointsOpen) _invOpen = _questsOpen = false; break;
            case Key.T: _conn.Travel(TravelKind.Town); break;
            case Key.C: _charOpen = !_charOpen; if (_charOpen) _skillsOpen = false; break;
            case Key.K: _skillsOpen = !_skillsOpen; if (_skillsOpen) _charOpen = false; break;
            case Key.I or Key.B: _invOpen = !_invOpen; break;
            case Key.J: if (_openWindow == Win.Book) CloseWindow(); else Open(Win.Book); break;
            case Key.F when _reviveTarget != 0: _conn.Command("revive", _reviveTarget.ToString()); break;
            case Key.Escape:
                if (_charOpen || _invOpen || _mapOpen || _skillsOpen || _questsOpen || _waypointsOpen)
                    _charOpen = _invOpen = _mapOpen = _skillsOpen = _questsOpen = _waypointsOpen = false;
                else Open(Win.Menu);
                break;
        }
    }

    private Texture2D MakeDisc(int size)
    {
        var px = new byte[size * size * 4];
        float r = size / 2f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float d = MathF.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
            float a = Math.Clamp(r - d, 0, 1);
            int i = (y * size + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = 255;
            px[i + 3] = (byte)(a * 255);
        }
        return Texture2D.FromPixels(_gl, "__disc", size, size, px);
    }

    // ------------------------------------------------------------------ update

    private void OnUpdate(double dt)
    {
        _assets.ProcessReloads();
        _conn.Poll();
        UpdateFx(dt);
        UpdateMusic(dt);
        PadButtons();
        for (int i = 0; i < _localCooldown.Length; i++) _localCooldown[i] = Math.Max(0, _localCooldown[i] - (float)dt);
        while (_conn.Events.TryDequeue(out var e)) { HandleEvent(e); FxForEvent(e); }
        while (_conn.Ui.TryDequeue(out var ui)) HandleUi(ui.Kind, ui.Json);
        while (_conn.Chat.TryDequeue(out var line))
        {
            // Player text never carries color codes: braces are neutralised before display.
            string text = line.Text.Replace('{', '(').Replace('}', ')');
            if (line.Channel == "system" && line.Text.Contains("Quest complete")) Sfx("quest_complete");
            Log(line.Channel switch
            {
                "system" => $"{{#9A9080}}{line.Text}{{/}}", // server text may carry colour codes; player text never does
                "guild" => $"{{#9FD68A}}[Guild] {line.From}:{{/}} {text}",
                _ => $"{{#7FB2FF}}{line.From}:{{/}} {text}",
            });
        }
        _floaters.RemoveAll(f => Now - f.Born > 1.1);
        _telegraphs.RemoveAll(t => Now > t.Until);
        if (_conn.ZoneGeneration != _seenGeneration && _conn.Welcome is { } arrived)
        {
            _seenGeneration = _conn.ZoneGeneration;
            _anim.Clear(); _floaters.Clear(); _hitFlash.Clear(); _telegraphs.Clear();
            _particles.Clear(); _bolts.Clear(); _flashes.Clear(); _camInit = false; _lastHp = -1; _zoneArrivedAt = Now;
            _lockedTarget = _talkTarget = 0;
            _waypointsOpen = false;
            _moveTarget = null; _pendingWaypoint = null;
            _focus = null; // a login or name field left focused would swallow every key in the world
            Banner(_data.Zones.TryGetValue(arrived.ZoneId, out var az) && az.Floor is string fl ? $"{arrived.ZoneName} · {fl}" : arrived.ZoneName);
            _autopilot?.SetExplore(arrived.ZoneWidth, arrived.ZoneHeight);
            if (arrived.ZoneId == Endgame.WorldBossZone) Sfx("horn", 0.9f);
        }

        if (_conn.Welcome is null || !_conn.Entities.TryGetValue(_conn.YourId, out var me)) return;
        var cls = Classes.Get(_conn.Class);

        PlayerInput input;
        if (_autopilot is not null)
        {
            var view = _conn.Entities.Values.Select(r => new BotView(r.Current.Id, r.Current.Kind, r.Current.Pos, r.Current.Hp,
                r.Current.MaxHp, r.Current.Dead, r.Current.Kind == EntityKind.Player ? r.Current.PlayerClass : default, r.Current.Flags)).ToList();
            if (_conn.Stats is { } ast) _autopilot.Bar = ast.Bar.Where(_data.Spells.ContainsKey).Select(id => _data.Spells[id]).ToList();
            // In town the demo autopilot walks out through the first exit.
            if (_conn.Welcome.IsTown && _conn.Welcome.ExitList.FirstOrDefault() is { } ex)
                input = new PlayerInput((new Vec2(ex.X, ex.Y) - me.Current.Pos).Normalized(), PlayerAction.None, 0, 0, Vec2.Zero);
            else input = _autopilot.Decide(_conn.YourId, view, (float)dt);
        }
        else input = ReadPlayerInput(me, cls);
        if (_pendingWaypoint is string dest && _conn.Welcome.Waypoint is { } wpos)
        {
            if (Vec2.Distance(me.Current.Pos, new Vec2(wpos[0], wpos[1])) < GameServerWaypointReach)
            {
                _conn.Travel(TravelKind.Waypoint, dest);
                _pendingWaypoint = null; _waypointsOpen = false; _moveTarget = null;
            }
            else if (_moveTarget is null) _pendingWaypoint = null; // walked off somewhere else
        }

        if (input.Action == PlayerAction.UseSkill && input.SkillSlot < Entity.BarSlots && _localCooldown[input.SkillSlot] <= 0
            && SlotCooldown(input.SkillSlot) is float cd)
            _localCooldown[input.SkillSlot] = cd;
        // The server simulates at 20 Hz and limits inputs per second, so send at most 30 a second however fast the
        // screen draws (a 144 Hz monitor would otherwise flood it). A changed action goes out at once.
        bool changed = input.Action != _lastSentInput.Action || input.SkillSlot != _lastSentInput.SkillSlot || input.TargetId != _lastSentInput.TargetId
                       || (input.Move.LengthSquared > 0) != (_lastSentInput.Move.LengthSquared > 0);
        if (changed ? Now - _lastInputSentAt >= 1.0 / 60 : Now - _lastInputSentAt >= 1.0 / 30)
        {
            _conn.SendInput(input);
            _lastSentInput = input;
            _lastInputSentAt = Now;
        }
    }

    private PlayerInput ReadPlayerInput(RemoteEntity me, ClassDef cls)
    {
        if (_typing || _focus is not null || StoryBlocking) return default;
        if (_talkTarget != 0 && _conn.Entities.TryGetValue(_talkTarget, out var npc))
        {
            var to = npc.Current.Pos - me.Current.Pos;
            if (to.Length > 140) return new PlayerInput(to.Normalized(), PlayerAction.None, 0, 0, npc.Current.Pos);
            _conn.Interact(_talkTarget);
            _talkTarget = 0;
            return default;
        }
        if (PadInput(me) is { } pad && pad != default) { _moveTarget = null; return pad; }
        var kb = _input.Keyboards.Count > 0 ? _input.Keyboards[0] : null;
        var mouse = _input.Mice.Count > 0 ? _input.Mice[0] : null;
        var move = Vec2.Zero;
        if (kb is not null)
        {
            if (kb.IsKeyPressed(Key.W) || kb.IsKeyPressed(Key.Up)) move += new Vec2(0, -1);
            if (kb.IsKeyPressed(Key.S) || kb.IsKeyPressed(Key.Down)) move += new Vec2(0, 1);
            if (kb.IsKeyPressed(Key.A) || kb.IsKeyPressed(Key.Left)) move += new Vec2(-1, 0);
            if (kb.IsKeyPressed(Key.D) || kb.IsKeyPressed(Key.Right)) move += new Vec2(1, 0);
        }
        if (move.LengthSquared > 0) { _moveTarget = null; return new PlayerInput(move, PlayerAction.None, 0, 0, Vec2.Zero); }

        var cursor = MouseWorld();
        // With the cursor over a window, clicks belong to the window; a walk already under way carries on.
        if (mouse is not null && OverPanel(mouse.Position)) return MoveTargetInput(me);
        int slot = (mouse?.IsButtonPressed(MouseButton.Right) ?? false) ? 1 : 0;
        if (kb is not null)
        {
            Key[] keys = [Key.Number1, Key.Number2, Key.Number3, Key.Number4, Key.Number5, Key.Number6, Key.Number7];
            for (int k = 0; k < keys.Length; k++) if (kb.IsKeyPressed(keys[k])) slot = k + 1;
        }
        if (slot > 0 && BarSpell(slot) is SpellDef spell)
        {
            int target = spell.Kind switch
            {
                SpellKind.Heal => HoveredPlayer()?.Current.Id ?? me.Current.Id,
                SpellKind.Revive => Hovered(EntityKind.Player, includeDead: true)?.Current.Id ?? 0,
                SpellKind.Bolt or SpellKind.Burst or SpellKind.Drain =>
                    HoveredMonster()?.Current.Id ?? (_lockedTarget != 0 ? _lockedTarget : NearestMonsterTo(cursor)?.Current.Id ?? 0),
                _ => me.Current.Id,
            };
            _moveTarget = null;
            return new PlayerInput(Vec2.Zero, PlayerAction.UseSkill, (byte)slot, target, cursor);
        }
        if (mouse?.IsButtonPressed(MouseButton.Left) ?? false)
        {
            int target = _lockedTarget != 0 && _conn.Entities.TryGetValue(_lockedTarget, out var t) && !t.Current.Dead
                ? _lockedTarget : DuelFoe() ?? HoveredMonster()?.Current.Id ?? 0;
            if (target != 0) { _moveTarget = null; return new PlayerInput(Vec2.Zero, PlayerAction.UseSkill, 0, target, cursor); }
            // Click (or hold) on open ground: walk there, and keep walking after the button is let go.
            _moveTarget = cursor;
            _moveTargetAt = Now;
        }
        return MoveTargetInput(me);
    }

    /// <summary>Keeps walking to the clicked spot until arrival (or a long time walking into scenery).</summary>
    private PlayerInput MoveTargetInput(RemoteEntity me)
    {
        if (_moveTarget is { } dest)
        {
            var to = dest - me.Current.Pos;
            if (to.Length <= 12 || Now - _moveTargetAt > 25) _moveTarget = null;
            else return new PlayerInput(to.Normalized(), PlayerAction.None, 0, 0, dest);
        }
        return default;
    }

    private void HandleEvent(SimEvent e)
    {
        SoundFor(e);
        bool mine = e.Source == _conn.YourId || e.Target == _conn.YourId;
        switch (e.Type)
        {
            case SimEventType.Damage:
                if (_conn.Entities.TryGetValue(e.Target, out var hit))
                {
                    bool playerHurt = hit.Current.Kind == EntityKind.Player;
                    bool crit = e.Text == "crit";
                    _floaters.Add(new FloatingText
                    {
                        Text = e.Value.ToString(), Pos = hit.Current.Pos + new Vec2(Random.Shared.Next(-14, 15), -70), Born = Now, Big = crit,
                        Color = playerHurt ? Color.FromHex("#E0463C") : crit ? Color.FromHex("#F2D25A") : Color.FromHex("#F4EFE3"),
                    });
                    _hitFlash[e.Target] = Now;
                    if (_anim.TryGetValue(e.Target, out var ah)) ah.HitAt = AnimNow;
                }
                break;
            case SimEventType.SkillUsed:
                if (_anim.TryGetValue(e.Source, out var aa))
                {
                    aa.AttackAt = AnimNow;
                    if (_conn.Entities.TryGetValue(e.Target, out var tgt))
                    {
                        var d = tgt.Current.Pos - aa.LastPos;
                        if (d.LengthSquared > 1) aa.Facing = MathF.Atan2(d.Y, d.X) * 180 / MathF.PI;
                    }
                }
                break;
            case SimEventType.Heal:
                if (_conn.Entities.TryGetValue(e.Target, out var healed))
                    _floaters.Add(new FloatingText { Text = "+" + e.Value, Pos = healed.Current.Pos + new Vec2(0, -80), Color = Color.FromHex("#6BD66B"), Born = Now });
                break;
            case SimEventType.Death:
                if (e.Target == _conn.YourId) Log("{#E0463C}You have fallen.{/} Rising again in 5 seconds.");
                else if (_conn.Roster.ContainsKey(e.Target)) Log($"{e.Text} has fallen.");
                else if (_conn.Entities.TryGetValue(e.Target, out var d) && (d.Current.Flags & EntityFlags.Superunique) != 0)
                    Log($"{{#E8892B}}{e.Text}{{/}} is slain.");
                break;
            case SimEventType.LevelUp:
                if (e.Target == _conn.YourId) { Log($"{{#F2D25A}}You reached level {e.Value}.{{/}}"); Banner($"Level {e.Value}"); }
                break;
            case SimEventType.MasteryUp:
                if (mine) Log($"{{#9FC6FF}}{SkillName(e.Text)} mastery {e.Value}{{/}} (+{SkillMastery.TotalBonus(e.Value) * 100:F1}%)");
                break;
            case SimEventType.SkillLearned:
                if (mine)
                {
                    Log($"{{#F2D25A}}{SkillName(e.Text)} is now level {e.Value}.{{/}}");
                    foreach (var sp in _data.Spells.Values.Where(sp => sp.Field == e.Text && sp.RequiredLevel == e.Value))
                        Log($"New spell: {{#9FC6FF}}{sp.Name}{{/}}");
                }
                break;
            case SimEventType.Blocked:
                if (_conn.Entities.TryGetValue(e.Target, out var blocker))
                    _floaters.Add(new FloatingText { Text = "Block", Pos = blocker.Current.Pos + new Vec2(0, -90), Color = Color.FromHex("#C9D6E8"), Born = Now });
                break;
            case SimEventType.Telegraph:
            {
                var parts = e.Text.Split(';');
                if (parts.Length == 3 && float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var tx)
                    && float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var ty)
                    && float.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var delay))
                    _telegraphs.Add((new Vec2(tx, ty), e.Value, Now + delay, Now));
                break;
            }
            case SimEventType.BossPhase:
                if (e.Value >= 1 && e.Target == 0) Banner(e.Text);
                Log($"{{#E8892B}}{e.Text}{{/}}");
                break;
            case SimEventType.DuelStarted:
                if (mine) { Banner("Duel!"); Log($"{{#F2D25A}}Duel in {e.Text}: fight in {e.Value} seconds. Leaving the ring forfeits.{{/}}"); }
                break;
            case SimEventType.Buffed:
                if (e.Target == _conn.YourId && e.Text == "doubt") Log("{#E0463C}Doubt: your blows weaken (−25% for 8 s).{/}");
                break;
            case SimEventType.Revived:
                if (e.Target == _conn.YourId) Log("{#6BD66B}You are raised from the dead.{/}");
                break;
            case SimEventType.LootPicked:
                if (e.Source == _conn.YourId) Log(e.Text == "gold" ? $"{{#E6C14A}}+{e.Value} gold{{/}}" : $"Picked up {RarityCode((Rarity)e.Value)}{e.Text}{{/}}");
                break;
            case SimEventType.Alarm:
                if (e.Target == _conn.YourId) { Banner("Discovered!"); Log($"{{#E0463C}}{e.Text}{{/}}"); }
                break;
            case SimEventType.Yielded:
                Log("{#E8892B}Your enemy yields. The party must decide.{/}");
                break;
            case SimEventType.RuneFound:
                if (mine && _conn.Entities.TryGetValue(e.Target, out var rf))
                    _floaters.Add(new FloatingText { Text = "Rune!", Pos = rf.Current.Pos + new Vec2(0, -80), Color = Color.FromHex("#9FC6FF"), Born = Now, Big = true });
                if (mine) Sfx("rune_found", 0.8f);
                break;
            case SimEventType.Hunted:
                if (e.Source == _conn.YourId) { Banner("Hunted!"); Sfx("horn", 0.7f); }
                break;
            case SimEventType.HuntsmanSlain:
                Banner("The Grey Huntsman falls");
                break;
            case SimEventType.PitFloor:
                Banner($"Floor {e.Value}");
                break;
            case SimEventType.PitFloorCleared:
                Banner($"Floor {e.Value} cleared");
                Sfx("quest_complete", 0.7f);
                break;
            case SimEventType.CofferOpened:
                Sfx("coffer_open", 0.9f);
                break;
            case SimEventType.ZoneCleared:
                if (_conn.Welcome?.ZoneId == "proving_pit") { Banner("The Proving Pit is conquered"); break; }
                Banner($"{e.Text} cleared");
                Log($"{{#F2D25A}}{e.Text} cleared in {e.Value / 60}m {e.Value % 60}s.{{/}} A new instance opens in 20 seconds.");
                break;
        }
    }

    private void Log(string text)
    {
        _log.Add((text, Now));
        if (_log.Count > 8) _log.RemoveAt(0);
    }

    private void Banner(string text) { _banner = text; _bannerUntil = Now + 3; }

    private string SkillName(string id) =>
        _data.Skills.TryGetValue(id, out var sk) ? sk.Name : _data.Spells.TryGetValue(id, out var sp) ? sp.Name : id;

    private static string RarityCode(Rarity r) => "{" + RarityHex(r) + "}";

    private static string RarityHex(Rarity r) => r switch
    {
        Rarity.Common => "#E8E4DA",
        Rarity.Uncommon => "#4CC24C",
        Rarity.Rare => "#4FA3FF",
        Rarity.Elite => "#E8D04A",
        Rarity.Epic => "#E8892B",
        Rarity.Exotic => "#36D6C3",
        Rarity.Mythic => "#FF4FB8",
        _ => "#D8342C",
    };

    // ------------------------------------------------------------------ picking

    private (float Scale, float OffX, float OffY) Letterbox(Vector2D<int> size)
    {
        float scale = MathF.Min(size.X / VW, size.Y / VH);
        return (scale, (size.X - VW * scale) / 2, (size.Y - VH * scale) / 2);
    }

    private Vec2 MouseWorld()
    {
        if (_input.Mice.Count == 0) return _camera;
        var m = _input.Mice[0].Position;
        var (scale, ox, oy) = Letterbox(_window.Size);
        var v = new Vec2((m.X - ox) / scale, (m.Y - oy) / scale);
        return (v - new Vec2(VW / 2, VH / 2)) / Zoom + _camera;
    }

    private RemoteEntity? HoveredMonster() => Hovered(EntityKind.Monster);
    private RemoteEntity? HoveredPlayer() => Hovered(EntityKind.Player);

    /// <summary>While dueling, the other player under the cursor is the target.</summary>
    private int? DuelFoe()
    {
        if (!_conn.Entities.TryGetValue(_conn.YourId, out var me) || (me.Current.Flags & EntityFlags.Dueling) == 0) return null;
        var foe = Hovered(EntityKind.Player);
        return foe is not null && foe.Current.Id != _conn.YourId ? foe.Current.Id : null;
    }

    private RemoteEntity? NearestMonsterTo(Vec2 at)
    {
        RemoteEntity? best = null;
        float bestD = 500;
        foreach (var r in _conn.Entities.Values)
        {
            if (r.Current.Kind != EntityKind.Monster || r.Current.Dead) continue;
            float d = Vec2.Distance(r.Current.Pos, at);
            if (d < bestD) { bestD = d; best = r; }
        }
        return best;
    }

    private SpellDef? BarSpell(int slot) =>
        _conn.Stats is { } st && slot >= 1 && slot - 1 < st.Bar.Count && _data.Spells.TryGetValue(st.Bar[slot - 1], out var sp) ? sp : null;

    private float? SlotCooldown(int slot) => slot == 0
        ? Weapons.Of(_conn.Stats?.Weapon ?? WeaponType.None).Cooldown
        : BarSpell(slot)?.Cooldown;

    private bool OverPanel(System.Numerics.Vector2 mouse)
    {
        var (scale, ox, oy) = Letterbox(_window.Size);
        float x = (mouse.X - ox) / scale, y = (mouse.Y - oy) / scale;
        if (_blockRects.Any(r => x >= r.X && x <= r.X + r.W && y >= r.Y && y <= r.Y + r.H)) return true;
        if (!_charOpen && !_invOpen && !_skillsOpen && !_questsOpen && !_waypointsOpen) return false;
        bool inLeft = (_charOpen || _skillsOpen) && x >= 280 && x <= 800 && y >= 100 && y <= 920;
        bool inRight = (_invOpen || _questsOpen || _waypointsOpen) && x >= 1040 && x <= 1560 && y >= 100 && y <= 920;
        return inLeft || inRight;
    }

    private void Click(System.Numerics.Vector2 mouse)
    {
        var (scale, ox, oy) = Letterbox(_window.Size);
        float x = (mouse.X - ox) / scale, y = (mouse.Y - oy) / scale;
        foreach (var b in _buttons)
            if (x >= b.X && x <= b.X + b.W && y >= b.Y && y <= b.Y + b.H) { Sfx("ui_click", 0.6f); b.OnClick(); return; }
    }

    private RemoteEntity? Hovered(EntityKind kind, bool includeDead = false)
    {
        var at = MouseWorld();
        RemoteEntity? best = null;
        float bestD = float.MaxValue;
        foreach (var r in _conn.Entities.Values)
        {
            var s = r.Current;
            if (s.Kind != kind || (s.Dead && !includeDead)) continue;
            float d = Vec2.Distance(s.Pos - new Vec2(0, 40), at); // world units: the zoom scales the picture, not the world
            if (d < 70 && d < bestD) { bestD = d; best = r; }
        }
        return best;
    }

    // ------------------------------------------------------------------ render

    private void OnRender(double dt)
    {
        _frameDt = dt;
        var fb = _window.FramebufferSize;
        _gl.Viewport(0, 0, (uint)fb.X, (uint)fb.Y);
        _gl.ClearColor(0f, 0f, 0f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        var (scale, ox, oy) = Letterbox(fb);
        _gl.Viewport((int)ox, (int)oy, (uint)(VW * scale), (uint)(VH * scale));

        if (_conn.Welcome is { } w && _conn.Entities.TryGetValue(_conn.YourId, out var me))
            RenderWorldFrame(w, me, dt, scale, ox, oy);
        else
        {
            _batch.Begin(VW, VH);
            if (_options.QuickPlay) DrawConnecting();
            else DrawFrontEnd();
            _batch.End();
        }

        _frameNumber++;
        _framesThisSecond++;
        if (_fpsClock.Elapsed.TotalSeconds >= 0.5)
        {
            LastFps = _framesThisSecond / _fpsClock.Elapsed.TotalSeconds;
            _framesThisSecond = 0;
            _fpsClock.Restart();
            _window.Title = $"Templar Knights — {EngineInfo.Name} {EngineInfo.Version} — {LastFps:F0} fps, {_batch.DrawCalls} draw calls, ping {_conn.Ping} ms";
        }
        if (_options.ExitAfterFrames is int limit && _frameNumber >= limit)
        {
            if (_options.ScreenshotPath is string shot) SaveScreenshot(shot, fb.X, fb.Y);
            Console.WriteLine($"[client] smoke test done: {_frameNumber} frames, {LastFps:F0} fps, {_batch.DrawCalls} draw calls, {_conn.Entities.Count} entities in view");
            _window.Close();
        }
    }

    private Vec2 ToScreen(Vec2 world) => (world - _camera) * Zoom + new Vec2(VW / 2, VH / 2);

    /// <summary>Half the visible world, in world units.</summary>
    private static Vec2 HalfView => new(VW / 2 / Zoom, VH / 2 / Zoom);

    private void DrawConnecting()
    {
        _batch.FillRect(0, 0, VW, VH, Color.FromHex("#0E0C0A"));
        _title.DrawCentered(_batch, "TEMPLAR KNIGHTS", VW / 2, 420, Color.FromHex("#C9A227"));
        string status = _conn.Failed ? $"Could not join: {_conn.RejectReason}" : $"Joining {_options.Host}:{_options.Port} as {_options.Name}...";
        _ui.DrawCentered(_batch, status, VW / 2, 500, _conn.Failed ? Color.FromHex("#E0463C") : Color.FromHex("#D8D2C4"));
    }

    private void DrawWorld(WelcomeInfo w)
    {
        // Ground, only the tiles on screen.
        var ground = _assets.Texture(w.GroundTile);
        // A tile covers its real-world size from tiles/tiles.json (so stones and straw match the figures), else its texture width.
        int tile = TileWorldSize(w.GroundTile, ground.Width);
        var topLeft = _camera - HalfView;
        int x0 = Math.Max(0, (int)(topLeft.X / tile) * tile), y0 = Math.Max(0, (int)(topLeft.Y / tile) * tile);
        for (int y = y0; y < Math.Min(w.ZoneHeight, topLeft.Y + VH / Zoom + tile); y += tile)
        for (int x = x0; x < Math.Min(w.ZoneWidth, topLeft.X + VW / Zoom + tile); x += tile)
        {
            var s = ToScreen(new Vec2(x, y));
            _batch.Draw(ground, s.X, s.Y, tile * Zoom + 1, tile * Zoom + 1, Color.White); // +1 hides hairline gaps
        }

        DrawGroundVariation(w);
        ZoneLights(w);
        DrawMarkers(w);

        // Draw back to front by y so nearer figures overlap farther ones.
        var ordered = _conn.Entities.Values.OrderBy(r => r.Current.Kind == EntityKind.Loot ? 0 : r.Current.Dead ? 1 : 2)
                                           .ThenBy(r => r.Current.Pos.Y).ToList();
        var hoveredId = HoveredMonster()?.Current.Id ?? 0;
        // Scenery is depth-sorted with the living: each piece is drawn just before the first figure standing lower on screen.
        var decor = _data.Zones.TryGetValue(w.ZoneId, out var zdef) ? VisibleDecor(zdef) : [];
        int di = 0;
        foreach (var r in ordered)
        {
            var s = r.Current;
            var p = ToScreen(r.PositionAt(_conn.Now, TickSeconds));
            if (s.Kind != EntityKind.Loot && !s.Dead)
                while (di < decor.Count && decor[di].Y <= r.PositionAt(_conn.Now, TickSeconds).Y) DrawDecor(decor[di++]);
            if (p.X < -400 || p.Y < -150 || p.X > VW + 400 || p.Y > VH + 700) continue;
            switch (s.Kind)
            {
                case EntityKind.Loot:
                    bool gold = s.TypeIndex == 255;
                    LootGlow(s, p, r.PositionAt(_conn.Now, TickSeconds));
                    _batch.Draw(_assets.Texture(gold ? "sprites/loot_gold.png" : "sprites/loot_item.png"), p.X - 20 * Zoom, p.Y - 20 * Zoom, 40 * Zoom, 40 * Zoom, Color.White);
                    if (!gold) _batch.FillRect(p.X - 16 * Zoom, p.Y + 22 * Zoom, 32 * Zoom, 4 * Zoom, Color.FromHex(RarityHex((Rarity)s.TypeIndex)));
                    break;
                case EntityKind.Monster:
                    DrawMonster(s, p, s.Id == hoveredId);
                    break;
                case EntityKind.Npc:
                    DrawNpc(w, s, p);
                    break;
                case EntityKind.Chest:
                    DrawCoffer(s, p);
                    break;
                case EntityKind.Player:
                    DrawPet(s, r.PositionAt(_conn.Now, TickSeconds));
                    DrawPlayer(s, p);
                    break;
            }
        }

        while (di < decor.Count) DrawDecor(decor[di++]);
        if (_extras is { MistHere: true } mx && mx.Zone == w.ZoneId) DrawFog();
    }

    private Dictionary<string, int>? _tileSizes;
    private readonly Dictionary<DecorDef, float> _decorFade = new(ReferenceEqualityComparer.Instance);
    private double _frameDt;

    private int TileWorldSize(string key, int textureWidth)
    {
        if (_tileSizes is null)
        {
            _tileSizes = new(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(_assets.Root, "tiles", "tiles.json");
            try
            {
                if (File.Exists(path))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                    foreach (var p in doc.RootElement.GetProperty("sizes").EnumerateObject()) _tileSizes[p.Name] = p.Value.GetInt32();
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException) { Console.WriteLine($"[assets] bad tiles.json: {ex.Message}"); }
        }
        return Math.Max(64, _tileSizes.TryGetValue(Path.GetFileNameWithoutExtension(key), out int n) ? n : textureWidth);
    }

    /// <summary>Zone scenery near the screen, sorted back to front.</summary>
    private List<DecorDef> VisibleDecor(ZoneDef z)
    {
        var list = new List<DecorDef>();
        foreach (var d in z.Decor)
        {
            var p = ToScreen(new Vec2(d.X, d.Y));
            if (p.X < -500 || p.Y < -200 || p.X > VW + 500 || p.Y > VH + 500) continue;
            list.Add(d);
        }
        list.Sort((a, b) => a.Y.CompareTo(b.Y));
        return list;
    }

    private void DrawDecor(DecorDef d)
    {
        if (!_data.Props.TryGetValue(d.Prop, out var prop)) return;
        DecorLight(d, prop);
        var tex = _assets.Texture(prop.Sprite);
        float size = prop.Metres * PixelsPerMetre * Zoom;
        float w = size * tex.Width / Math.Max(1, tex.Height);
        var p = ToScreen(new Vec2(d.X, d.Y));
        float x = p.X - w * prop.AnchorX, y = p.Y - size * prop.AnchorY;
        var tint = Color.White;
        if (prop.Metres >= 3.5f)
        {
            // Tall scenery (trees, tents, banners) turns see-through while it hides a hero standing behind it.
            bool hides = false;
            foreach (var r in _conn.Entities.Values)
            {
                if (r.Current.Kind != EntityKind.Player || r.Current.Dead) continue;
                var wp = r.PositionAt(_conn.Now, TickSeconds);
                if (wp.Y >= d.Y) continue;
                var sp = ToScreen(wp);
                if (sp.X > x + w * 0.15f && sp.X < x + w * 0.85f && sp.Y > y + size * 0.1f && sp.Y < p.Y) { hides = true; break; }
            }
            float a = _decorFade.GetValueOrDefault(d, 1f);
            a += ((hides ? 0.38f : 1f) - a) * MathF.Min(1, (float)_frameDt * 8);
            _decorFade[d] = a;
            tint = new Color(255, 255, 255, (byte)(255 * a));
        }
        if (d.Flip) _batch.Draw(tex, x, y, w, size, 1, 0, 0, 1, tint);
        else _batch.Draw(tex, x, y, w, size, tint);
    }

    /// <summary>Exits, the waypoint, the arena ring and boss warnings, drawn on the ground under everything.</summary>
    private void DrawMarkers(WelcomeInfo w)
    {
        if (w.Arena is { } a)
        {
            var tl = ToScreen(new Vec2(a[0], a[1]));
            var edge = Color.FromHex("#8A6A3A");
            float aw = a[2] * Zoom, ah = a[3] * Zoom;
            _batch.FillRect(tl.X, tl.Y, aw, ah, new Color(120, 90, 50, 40));
            _batch.FillRect(tl.X, tl.Y, aw, 3, edge); _batch.FillRect(tl.X, tl.Y + ah - 3, aw, 3, edge);
            _batch.FillRect(tl.X, tl.Y, 3, ah, edge); _batch.FillRect(tl.X + aw - 3, tl.Y, 3, ah, edge);
            _overlay.Add(() => _small.DrawCentered(_batch, $"{w.ArenaName}  {{#9A9080}}/duel name{{/}}", tl.X + aw / 2, tl.Y + 8, Color.FromHex("#C9A227")));
        }
        foreach (var ex in w.ExitList)
        {
            var p = ToScreen(new Vec2(ex.X, ex.Y));
            if (p.X < -300 || p.X > VW + 300 || p.Y < -300 || p.Y > VH + 300) continue;
            float pulse = 0.75f + 0.25f * MathF.Sin((float)Now * 3);
            _batch.Draw(_disc, p.X - 70 * Zoom, p.Y - 35 * Zoom, 140 * Zoom, 70 * Zoom, new Color(201, 162, 39, (byte)(55 * pulse)));
            _batch.Draw(_disc, p.X - 34 * Zoom, p.Y - 17 * Zoom, 68 * Zoom, 34 * Zoom, new Color(240, 220, 160, (byte)(80 * pulse)));
            string exName = ex.Name;
            _overlay.Add(() => _small.DrawCentered(_batch, $"To {exName}", p.X, p.Y - 96, Color.FromHex("#F2D25A")));
            AddLight(new Vec2(ex.X, ex.Y), 170, Color.FromHex("#FFD27A"), 0.45f * pulse);
            _emissive.Add(() => _batch.Draw(_softDot, p.X - 44 * Zoom, p.Y - 22 * Zoom, 88 * Zoom, 44 * Zoom, new Color(255, 200, 110, (byte)(28 * pulse))));
        }
        if (w.Waypoint is { } wp)
        {
            var p = ToScreen(new Vec2(wp[0], wp[1]));
            _batch.Draw(_disc, p.X - 80 * Zoom, p.Y - 40 * Zoom, 160 * Zoom, 80 * Zoom, new Color(80, 140, 255, 70));
            _batch.Draw(_disc, p.X - 40 * Zoom, p.Y - 20 * Zoom, 80 * Zoom, 40 * Zoom, new Color(160, 200, 255, 140));
            _overlay.Add(() => _small.DrawCentered(_batch, "Waypoint  {#9A9080}click or V{/}", p.X, p.Y - 66, Color.FromHex("#9FC6FF")));
            float wpulse = 0.85f + 0.15f * MathF.Sin((float)Now * 2.2f);
            AddLight(new Vec2(wp[0], wp[1]), 240, Color.FromHex("#8FB8FF"), 1.1f * wpulse);
            _emissive.Add(() =>
            {
                _batch.Draw(_softDot, p.X - 70 * Zoom, p.Y - 34 * Zoom, 140 * Zoom, 68 * Zoom, new Color(110, 160, 255, (byte)(110 * wpulse)));
                _batch.Draw(_beamTex, p.X - 26 * Zoom, p.Y - 120 * Zoom, 52 * Zoom, 120 * Zoom, new Color(140, 180, 255, (byte)(70 * wpulse)));
            });
        }
        foreach (var (at, radius, until, born) in _telegraphs)
        {
            var p = ToScreen(at);
            float t = (float)Math.Clamp((Now - born) / Math.Max(0.01, until - born), 0, 1);
            float zr = radius * Zoom;
            _batch.Draw(_disc, p.X - zr, p.Y - zr * 0.6f, zr * 2, zr * 1.2f, new Color(220, 40, 30, 70));
            _batch.Draw(_disc, p.X - zr * t, p.Y - zr * 0.6f * t, zr * 2 * t, zr * 1.2f * t, new Color(255, 80, 40, 120));
            AddLight(at, radius * 1.1f, Color.FromHex("#FF4020"), 0.6f + 0.6f * t);
            float tt = t;
            _emissive.Add(() => _batch.Draw(_ringTex, p.X - zr, p.Y - zr * 0.6f, zr * 2, zr * 1.2f, new Color(255, 70, 30, (byte)(90 + 120 * tt))));
        }
    }

    private void DrawNpc(WelcomeInfo w, EntityState s, Vec2 p)
    {
        string name = s.TypeIndex < w.NpcList.Count ? w.NpcList[s.TypeIndex] : "?";
        var def = _data.Zones.TryGetValue(w.ZoneId, out var zd) && s.TypeIndex < zd.Npcs.Count ? zd.Npcs[s.TypeIndex] : null;
        string key = def?.Sprite is { Length: > 0 } sp ? sp
            : "sprites/npc_" + string.Concat(name.ToLowerInvariant().Select(c => char.IsLetter(c) ? c : '_')).Trim('_') + ".png";
        if (!File.Exists(Path.Combine(_assets.Root, key))) key = "sprites/npc_townsfolk.png";
        bool hovered = Hovered(EntityKind.Npc)?.Current.Id == s.Id;
        float k = CharacterScale;
        _batch.Draw(_disc, p.X - 46 * k, p.Y - 10 * k, 92 * k, 20 * k, new Color(0, 0, 0, 100));
        DrawFigure(key, s, p, 150, hovered ? new Color(255, 245, 210) : Color.White, k);
        _overlay.Add(() => _small.DrawCentered(_batch, name, p.X, p.Y - FigureHeadMetres * PixelsPerMetre * k - 30, Color.FromHex(hovered ? "#F2D25A" : "#C9A227")));
    }

    private void DrawMonster(EntityState s, Vec2 p, bool hovered)
    {
        string id = s.TypeIndex < _data.MonsterIndex.Count ? _data.MonsterIndex[s.TypeIndex] : "";
        var def = _data.Monsters.GetValueOrDefault(id);
        bool boss = (s.Flags & EntityFlags.Superunique) != 0;
        float size = (def?.Radius ?? 20) * (boss ? 8f : 6f);
        float k = CharacterScale;
        // A champion may have a sprite of its own; otherwise it wears its base monster's.
        string spriteKey = def?.Sprite ?? "";
        if (boss && !id.StartsWith("su_") && _data.Superuniques.Values.FirstOrDefault(u => u.Base == id && u.Sprite is { Length: > 0 }) is { Sprite: { } own }
            && File.Exists(Path.Combine(_assets.Root, own))) spriteKey = own;
        // Height of the head above the feet: from the sheet's world size when rendered, else the old sprite size.
        var sheet = _assets.Sheet(spriteKey);
        float head = sheet is not null && sheet.Info.Metres > 0
            ? FigureHeadMetres * PixelsPerMetre * k * (sheet.Info.Metres / 2.6f)
            : size * k * 0.85f;
        var tint = s.Dead ? new Color(90, 80, 70, 170) : Color.White;
        if (!s.Dead && _hitFlash.TryGetValue(s.Id, out var at) && Now - at < 0.12) tint = new Color(255, 150, 140);
        if ((s.Flags & EntityFlags.Slowed) != 0 && !s.Dead) tint = new Color(170, 210, 255);
        if ((s.Flags & EntityFlags.Ally) != 0 && !s.Dead) tint = new Color(200, 230, 255, 200);
        if ((s.Flags & EntityFlags.Invulnerable) != 0 && !s.Dead) tint = new Color(255, 236, 170);
        if (!s.Dead) _batch.Draw(_disc, p.X - size * 0.3f * k, p.Y - 10 * k, size * 0.6f * k, 20 * k, new Color(0, 0, 0, 90));
        DrawFigure(spriteKey, s, p, size, tint, k);
        if (s.Dead) return;

        if (hovered || boss || s.Hp < s.MaxHp)
        {
            float bw = boss ? 140 : 80;
            float y = p.Y - head - 12;
            float frac = Math.Clamp(s.Hp / (float)Math.Max(1, s.MaxHp), 0, 1);
            _overlay.Add(() =>
            {
                _batch.FillRect(p.X - bw / 2 - 1, y - 1, bw + 2, 9, new Color(0, 0, 0, 200));
                _batch.FillRect(p.X - bw / 2, y, bw * frac, 7, Color.FromHex(boss ? "#E8892B" : "#B8322A"));
                _batch.FillRect(p.X - bw / 2, y, bw * frac, 2, new Color(255, 255, 255, 50));
            });
        }
        if (boss && FxOn)
        {
            // Bosses carry a menacing glow so they read in the dark.
            AddLight(p / Zoom + _camera - new Vec2(VW / 2, VH / 2) / Zoom, 220, Color.FromHex("#FF6A3A"), 0.55f);
        }
        if (hovered || boss)
        {
            string name = def?.Name ?? "?";
            if (boss && !id.StartsWith("su_") && _data.Superuniques.Values.FirstOrDefault(u => u.Base == id) is { } su) name = su.Name;
            if ((s.Flags & EntityFlags.Ally) != 0) name = id == "fallen_templar" ? "Recanted Brother" : name;
            if ((s.Flags & EntityFlags.Yielded) != 0) name += "  {#9FD68A}yields{/}";
            string label = $"{name}  {{#9A9080}}L{s.Level}{{/}}";
            _overlay.Add(() => _small.DrawCentered(_batch, label, p.X, p.Y - head - 38, boss ? Color.FromHex("#E8892B") : Color.FromHex("#E8E4DA")));
        }
    }

    /// <summary>
    /// The class sheet that shows the weapon family in hand ("class_knight_heavy" for a maul), falling back to the
    /// class's own sheet when there is no art for that weapon yet.
    /// </summary>
    private string PlayerSprite(ClassId cls, WeaponType weapon)
    {
        string baseKey = $"sprites/class_{cls.ToString().ToLowerInvariant()}";
        string? family = weapon switch
        {
            WeaponType.Dagger => "dagger", WeaponType.LightTwoHanded => "twohand", WeaponType.HeavyTwoHanded => "heavy",
            WeaponType.Throwing => "throwing", _ => null,
        };
        return family is not null && _assets.Exists($"{baseKey}_{family}.json") ? $"{baseKey}_{family}.png" : baseKey + ".png";
    }

    private void DrawPlayer(EntityState s, Vec2 p)
    {
        var cls = s.PlayerClass;
        const float size = 160;
        float k = CharacterScale;
        float head = FigureHeadMetres * PixelsPerMetre * k;
        var tint = s.Dead ? new Color(255, 255, 255, 70) : Color.White;
        if (!s.Dead && _hitFlash.TryGetValue(s.Id, out var at) && Now - at < 0.12) tint = new Color(255, 160, 150);
        _batch.Draw(_disc, p.X - 46 * k, p.Y - 10 * k, 92 * k, 20 * k, new Color(0, 0, 0, 100));
        if (s.Id == _conn.YourId) _batch.Draw(_disc, p.X - 52 * k, p.Y - 13 * k, 104 * k, 26 * k, Color.FromHex("#C9A22740"));
        DrawFigure(PlayerSprite(cls, s.PlayerWeapon), s, p, size, tint, k);
        string name = _conn.Roster.TryGetValue(s.Id, out var r) ? r.Name : "?";
        bool mine = s.Id == _conn.YourId;
        _overlay.Add(() => _small.DrawCentered(_batch, name, p.X, p.Y - head - 30, mine ? Color.FromHex("#F2D25A") : Color.FromHex("#CFE0FF")));
        if (!mine && !s.Dead)
        {
            float frac = Math.Clamp(s.Hp / (float)Math.Max(1, s.MaxHp), 0, 1);
            _overlay.Add(() =>
            {
                _batch.FillRect(p.X - 41, p.Y - head - 7, 82, 8, new Color(0, 0, 0, 200));
                _batch.FillRect(p.X - 40, p.Y - head - 6, 80 * frac, 6, Color.FromHex("#3FA34D"));
            });
        }
    }

    // ------------------------------------------------------------------ HUD

    private void DrawHud(WelcomeInfo w, RemoteEntity me)
    {
        var s = me.Current;
        var stats = _conn.Stats;
        var cls = Classes.Get(_conn.Class);

        // Top left: who you are.
        _title.Draw(_batch, _conn.Name.ToUpperInvariant(), 28, 20, Color.FromHex("#C9A227"));
        _ui.Draw(_batch, $"Level {s.Level} {cls.Name}   {{#E6C14A}}{stats?.Gold ?? 0} gold{{/}}", 30, 62, Color.FromHex("#D8D2C4"));

        // Left: party frames (members may be in other zones).
        float fy = 120;
        foreach (var m in _conn.Party)
        {
            _batch.FillRect(24, fy, 240, 52, new Color(10, 9, 8, 170));
            string where = m.ZoneName == w.ZoneName || !m.Online ? "" : $"  {{#7A7060}}{m.ZoneName}{{/}}";
            _small.Draw(_batch, $"{(m.Leader ? "{#C9A227}»{/} " : "")}{m.Name}  {{#9A9080}}{m.Class} L{m.Level}{{/}}", 34, fy + 4, Color.FromHex(m.Online ? "#E8E4DA" : "#6A6050"));
            float frac = Math.Clamp(m.Hp / (float)Math.Max(1, m.MaxHp), 0, 1);
            _batch.FillRect(34, fy + 32, 220, 10, new Color(40, 10, 10, 220));
            _batch.FillRect(34, fy + 32, 220 * frac, 10, Color.FromHex(m.Hp <= 0 ? "#555555" : "#3FA34D"));
            if (where.Length > 0) _small.Draw(_batch, where.Trim(), 34, fy + 52, Color.White);
            fy += where.Length > 0 ? 80 : 60;
        }

        // Top centre: boss health while a boss is in view.
        if (_conn.Entities.Values.FirstOrDefault(e => (e.Current.Flags & EntityFlags.Boss) != 0 && !e.Current.Dead) is { } bossEnt)
        {
            var b = bossEnt.Current;
            string bname = _data.Superuniques.Values.FirstOrDefault(u => b.TypeIndex < _data.MonsterIndex.Count && u.Base == _data.MonsterIndex[b.TypeIndex])?.Name ?? "Boss";
            _title.DrawCentered(_batch, bname.ToUpperInvariant(), VW / 2, 24, Color.FromHex("#E8892B"));
            _batch.FillRect(VW / 2 - 302, 70, 604, 20, new Color(0, 0, 0, 220));
            _batch.FillRect(VW / 2 - 300, 72, 600 * Math.Clamp(b.Hp / (float)Math.Max(1, b.MaxHp), 0, 1), 16,
                Color.FromHex((b.Flags & EntityFlags.Invulnerable) != 0 ? "#B8A060" : "#B8322A"));
            if ((b.Flags & EntityFlags.Invulnerable) != 0)
                _small.DrawCentered(_batch, "Shielded by the confessions — kill them first", VW / 2, 94, Color.FromHex("#E8D7A0"));
        }

        // Top right: minimap and zone.
        if (_settings.Minimap) DrawMinimap(w, 1580, 24, 316, 178, 0.85f);
        _ui.Draw(_batch, w.ZoneName, 1580, 208, Color.FromHex("#E8E4DA"));
        _small.Draw(_batch, w.IsTown
            ? $"Act {Roman(w.Act)} · Town · safe   {{#7A7060}}T: town  V: waypoints  Q: quests{{/}}"
            : $"Act {Roman(w.Act)} · Levels {w.LevelMin}-{w.LevelMax} · {_conn.Entities.Values.Count(e => e.Current.Kind == EntityKind.Monster && !e.Current.Dead && (e.Current.Flags & EntityFlags.Ally) == 0)} foes in sight",
            1580, 240, Color.FromHex("#9A9080"));

        // Bottom: health and mana vessels, skill bar, XP bar.
        DrawVessel(470, 985, 74, s.Hp / (float)Math.Max(1, s.MaxHp), Color.FromHex("#9E1B1B"), $"{s.Hp}/{s.MaxHp}");
        float manaFrac = stats is null ? 1 : stats.Mana / (float)Math.Max(1, stats.MaxMana);
        DrawVessel(1450, 985, 74, manaFrac, Color.FromHex("#23468F"), stats is null ? "" : $"{stats.Mana}/{stats.MaxMana}");

        const float slot = 72, barX = VW / 2 - 4 * slot;
        _batch.FillRect(barX - 10, 948, slot * 8 + 20, 92, new Color(12, 10, 8, 210));
        string[] keyHints = ["LMB", "RMB 1", "2", "3", "4", "5", "6", "7"];
        for (int i = 0; i < Entity.BarSlots; i++)
        {
            float x = barX + i * slot + 4;
            _batch.FillRect(x, 958, slot - 8, slot - 8, new Color(52, 45, 36, 240));
            string? label;
            string? masterySkill;
            if (i == 0)
            {
                var wt = stats?.Weapon ?? WeaponType.None;
                label = wt switch
                {
                    WeaponType.OneHandedSword => "Sword", WeaponType.Dagger => "Dagger", WeaponType.LightTwoHanded => "Longsword",
                    WeaponType.HeavyTwoHanded => "Maul", WeaponType.Throwing => "Throw", _ => "Fists",
                };
                masterySkill = _data.SkillForWeapon(wt)?.Id;
            }
            else
            {
                var sp = BarSpell(i);
                label = sp?.Name.Split(' ')[0];
                masterySkill = sp?.Field;
            }
            if (label is null) continue;
            float cd = SlotCooldown(i) is float full && full > 0 ? _localCooldown[i] / full : 0;
            if (cd > 0) _batch.FillRect(x, 958, slot - 8, (slot - 8) * cd, new Color(0, 0, 0, 150));
            _small.DrawCentered(_batch, label, x + (slot - 8) / 2, 970, Color.FromHex("#E8E4DA"));
            _small.DrawCentered(_batch, keyHints[i], x + (slot - 8) / 2, 996, Color.FromHex("#9A9080"));
            var m = masterySkill is null ? null : stats?.Skills.FirstOrDefault(mm => mm.SkillId == masterySkill);
            if (m is not null && m.MasteryLevel > 0) _small.Draw(_batch, $"{{#9FC6FF}}{m.MasteryLevel}{{/}}", x + 4, 958, Color.White);
        }
        if (stats is { BuildPoints: > 0 } && !_skillsOpen)
            _small.DrawCentered(_batch, $"{{#F2D25A}}{stats.BuildPoints} build point{(stats.BuildPoints == 1 ? "" : "s")} to spend — press K{{/}}", VW / 2, 918, Color.White);

        float xpFrac = stats is null || stats.XpToNext <= 0 ? 0 : Math.Clamp(stats.Xp / (float)stats.XpToNext, 0, 1);
        _batch.FillRect(0, 1066, VW, 14, new Color(8, 7, 6, 230));
        _batch.FillRect(0, 1068, VW * xpFrac, 10, Color.FromHex("#C9A227"));

        // Bottom left: log (the chat window arrives with online chat).
        float ly = 930 - _log.Count * 28;
        foreach (var (text, at) in _log)
        {
            byte a = (byte)(255 * Math.Clamp(12 - (Now - at), 0.25, 1));
            _small.Draw(_batch, text, 28, ly, new Color(216, 210, 196, a));
            ly += 28;
        }

        if (_typing)
        {
            _batch.FillRect(24, 938, 600, 36, new Color(10, 9, 8, 220));
            string caret = (int)(Now * 2) % 2 == 0 ? "|" : "";
            _small.Draw(_batch, $"{{#7FB2FF}}Group:{{/}} {_chatInput.ToString().Replace('{', '(')}{caret}", 34, 944, Color.FromHex("#E8E4DA"));
        }
        _buttons.Clear();
        _rightButtons.Clear();
        _blockRects.Clear();
        DrawEndgameHud(w, me);
        if (_charOpen) DrawCharacterPanel(s, stats, cls);
        if (_skillsOpen) DrawSkillsPanel(stats);
        if (_questsOpen) DrawQuestPanel(stats);
        if (_waypointsOpen) DrawWaypointsByAct(w, stats, me);
        // Potions next to the vessels: Z drinks health, X drinks mana.
        _batch.Draw(_assets.Texture("ui/icons/potion_health.png"), 560 - 50, 1012, 34, 34, Color.White);
        _batch.Draw(_assets.Texture("ui/icons/potion_mana.png"), 1360 - 50, 1012, 34, 34, Color.White);
        _small.DrawCentered(_batch, $"{{#E07070}}{stats?.HealthPotions ?? 0}{{/}} {{#9A9080}}Z{{/}}", 560 + 6, 1030, Color.White);
        _small.DrawCentered(_batch, $"{{#7FA0E0}}{stats?.ManaPotions ?? 0}{{/}} {{#9A9080}}X{{/}}", 1360 + 6, 1030, Color.White);
        if (s.Dead) _title.DrawCentered(_batch, "YOU HAVE FALLEN", VW / 2, 420, Color.FromHex("#E0463C"));
        if (_banner is not null && Now < _bannerUntil && _openWindow == Win.None)
            _title.DrawCentered(_batch, _banner.ToUpperInvariant(), VW / 2, 300, Color.FromHex("#F2D25A"));
        DrawWindow(stats, me); // windows sit on top of everything else
    }

    // ------------------------------------------------------------------ panels (slide in from the sides, centre stays clear)

    private void Panel(float x, float width, string title, Action? close = null)
    {
        _blockRects.Add((x, 100, width, 820));
        _batch.FillRect(x, 100, width, 820, new Color(16, 13, 10, 248));
        _batch.FillRect(x, 100, width, 2, Color.FromHex("#C9A227"));
        _batch.FillRect(x, 918, width, 2, Color.FromHex("#C9A227"));
        _title.Draw(_batch, title, x + 24, 116, Color.FromHex("#C9A227"));
        if (close is not null) Button(x + width - 56, 112, 40, 36, "X", close, tone: "plain");
    }

    private void DrawCharacterPanel(EntityState s, PlayerStats? st, ClassDef cls)
    {
        const float x = 280, w = 520;
        Panel(x, w, "CHARACTER", () => _charOpen = false);
        float y = 170;
        void Row(string label, string value)
        {
            _ui.Draw(_batch, label, x + 24, y, Color.FromHex("#9A9080"));
            _ui.Draw(_batch, value, x + 230, y, Color.FromHex("#E8E4DA"));
            y += 34;
        }
        Row("Name", _conn.Name);
        Row("Class", cls.Name);
        Row("Level", $"{s.Level}");
        Row("Experience", st is null ? "-" : $"{st.Xp:N0} / {st.XpToNext:N0}");
        Row("Health", $"{s.Hp} / {s.MaxHp}");
        Row("Mana", st is null ? "-" : $"{st.Mana} / {st.MaxMana}");
        Row("Damage", $"{Progression.Damage(cls, s.Level) + (st?.GearDamage ?? 0):F0}  {{#9A9080}}(gear +{st?.GearDamage ?? 0}){{/}}");
        Row("Defense", $"{st?.GearDefense ?? 0}");
        Row("Kills / deaths", st is null ? "-" : $"{st.Kills} / {st.Deaths}");
        Row("Gold", $"{{#E6C14A}}{st?.Gold ?? 0}{{/}}");

        Row("Weapon", Weapons.Describe(st?.Weapon ?? WeaponType.None));
        Row("Block chance", $"{(st?.BlockChance ?? 0) * 100:F0}%");
        Row("Build points", $"{{#F2D25A}}{st?.BuildPoints ?? 0}{{/}}");
        _small.Draw(_batch, "Skills and spells: press K.", x + 24, 880, Color.FromHex("#9A9080"));
    }

    /// <summary>The 11 skills: learned level (pips, max 10), mastery, and a button to spend build points.</summary>
    private void DrawSkillsPanel(PlayerStats? st)
    {
        const float x = 280, w = 520;
        Panel(x, w, "SKILLS", () => _skillsOpen = false);
        _small.Draw(_batch, $"{{#F2D25A}}{st?.BuildPoints ?? 0} build points{{/}}  ·  {{#9FC6FF}}{st?.Scrolls ?? 0} mastery scrolls{{/}}  ·  {{#9A9080}}levels 0-10, mastery 0-100{{/}}", x + 24, 160, Color.White);
        float y = 196;
        SkillKind? lastKind = null;
        foreach (var sk in _data.SkillList)
        {
            if (lastKind is not null && sk.Kind != lastKind && sk.Kind == SkillKind.SpellField)
            {
                _small.Draw(_batch, "SPELL FIELDS", x + 24, y, Color.FromHex("#C9A227"));
                y += 26;
            }
            lastKind = sk.Kind;
            var info = st?.Skills.FirstOrDefault(i => i.SkillId == sk.Id);
            int level = info?.Level ?? 0;
            int cost = _data.LearnCost(_conn.Class.ToString(), sk.Id);
            _ui.Draw(_batch, sk.Name, x + 24, y, level > 0 ? Color.FromHex("#E8E4DA") : Color.FromHex("#8A8070"));
            for (int i = 0; i < _data.SkillRules.MaxSkillLevel; i++)
                _batch.FillRect(x + 220 + i * 14, y + 12, 10, 10, i < level ? Color.FromHex("#C9A227") : new Color(60, 52, 42, 255));
            if ((info?.MasteryLevel ?? 0) > 0) _small.Draw(_batch, $"{{#9FC6FF}}M{info!.MasteryLevel}{{/}}", x + 370, y + 4, Color.White);
            if ((st?.Scrolls ?? 0) > 0 && (info?.ScrollLevels ?? 0) < SkillMastery.MaxScrollLevels)
            {
                float sx = x + 404, sy = y + 2;
                _batch.FillRect(sx, sy, 44, 28, Color.FromHex("#1E3A5A"));
                _small.DrawCentered(_batch, "{#9FC6FF}Read{/}", sx + 22, sy + 3, Color.White);
                string sid = sk.Id;
                _buttons.Add((sx, sy, 44, 28, () => _conn.UseScroll(sid)));
            }
            if (level < _data.SkillRules.MaxSkillLevel)
            {
                bool afford = (st?.BuildPoints ?? 0) >= cost;
                float bx = x + w - 104, by = y + 2;
                _batch.FillRect(bx, by, 80, 28, afford ? Color.FromHex("#5A4A1E") : new Color(40, 36, 30, 255));
                _small.DrawCentered(_batch, $"+  {cost} BP", bx + 40, by + 3, afford ? Color.FromHex("#F2D25A") : Color.FromHex("#6A6050"));
                string id = sk.Id;
                if (afford) _buttons.Add((bx, by, 80, 28, () => _conn.Learn(id)));
            }
            y += 38;
            if (sk.Kind == SkillKind.SpellField && level > 0)
            {
                var spells = _data.Spells.Values.Where(sp => sp.Field == sk.Id).OrderBy(sp => sp.RequiredLevel)
                    .Select(sp => sp.RequiredLevel <= level ? $"{{#9FC6FF}}{sp.Name}{{/}}" : $"{{#5A5448}}{sp.Name} ({sp.RequiredLevel}){{/}}");
                _small.Draw(_batch, string.Join("  ", spells), x + 40, y - 8, Color.White);
                y += 22;
            }
        }
    }

    private static string ItemLine(ItemInfo i) =>
        $"{{{RarityHex(i.Rarity)}}}{i.Name}{{/}}  {{#9A9080}}i{i.ItemLevel}{(i.Damage > 0 ? $" dmg {i.Damage}" : "")}{(i.Defense > 0 ? $" def {i.Defense}" : "")}{{/}}";

    /// <summary>Quests of the act you are in (prologue quest hidden once done); main quests first.</summary>
    private void DrawQuestPanel(PlayerStats? st)
    {
        const float x = 1040, w = 520;
        int act = Math.Max(1, _conn.Welcome?.Act ?? 1);
        Panel(x, w, $"QUESTS · ACT {Roman(act)}", () => _questsOpen = false);
        float y = 170;
        bool Done(string id) => st?.QuestList.FirstOrDefault(i => i.Id == id)?.Done ?? false;
        foreach (var q in _data.Quests.Where(q => q.Act == act && !(q.Id.StartsWith("q0_") && Done(q.Id))).OrderBy(q => q.Optional))
        {
            bool done = Done(q.Id);
            string zone = _data.Zones.TryGetValue(q.Zone, out var z) ? z.Name + (z.Floor is string f ? $", {f}" : "") : q.Zone;
            _ui.Draw(_batch, $"{(done ? "{#6BD66B}Done:{/} " : "")}{q.Name}{(q.Optional ? "  {#7A7060}optional{/}" : "")}", x + 24, y, Color.FromHex(done ? "#8A8070" : "#E8E4DA"));
            _small.Draw(_batch, Clip(q.Objective.Text, w - 64), x + 40, y + 32, Color.FromHex("#9A9080"));
            string where = q.Objective.Hint is { } h && !done ? h : zone;
            _small.Draw(_batch, $"{{#7A7060}}{where}{{/}}", x + 40, y + 56, Color.White);
            y += 92;
            if (y > 820) break;
        }
        var giver = _data.TownOf(act).Npcs.FirstOrDefault(n => n.Services.Contains("main_quest"));
        _small.Draw(_batch, $"{giver?.Name ?? "Your commander"} in {_data.TownOf(act).Name} knows what comes next.", x + 24, 880, Color.FromHex("#9A9080"));
    }

    private void DrawVessel(float cx, float cy, float r, float frac, Color fill, string label)
    {
        frac = Math.Clamp(frac, 0, 1);
        _batch.Draw(_disc, cx - r - 6, cy - r - 6, (r + 6) * 2, (r + 6) * 2, Color.FromHex("#3A3026"));
        _batch.Draw(_disc, cx - r, cy - r, r * 2, r * 2, new Color(14, 12, 10, 255));
        // Liquid level: the bottom part of the disc, cropped by UV.
        float top = cy + r - 2 * r * frac;
        _batch.Draw(_disc, cx - r, top, r * 2, 2 * r * frac, 0, 1 - frac, 1, 1, fill);
        if (frac > 0.02f && frac < 0.98f)
        {
            // The liquid's surface sways a little: a bright band across the chord at its level.
            float dy = top - cy, half = MathF.Sqrt(MathF.Max(0, r * r - dy * dy));
            float sway = MathF.Sin((float)Now * 2.1f + cx) * 2.5f;
            var light = new Color((byte)Math.Min(255, fill.R + 70), (byte)Math.Min(255, fill.G + 70), (byte)Math.Min(255, fill.B + 70), 150);
            _batch.DrawRotated(_softDot, cx, top + 1, half * 2, 7, sway * 0.02f, light);
        }
        // Inner shadow and a glass highlight, so the orb reads as a filled glass globe.
        _batch.Draw(_ringTex, cx - r * 1.04f, cy - r * 1.04f, r * 2.08f, r * 2.08f, new Color(0, 0, 0, 120));
        _batch.Draw(_softDot, cx - r * 0.62f, cy - r * 0.78f, r * 0.75f, r * 0.42f, new Color(255, 255, 255, 46));
        _batch.Draw(_softDot, cx - r * 0.42f, cy - r * 0.66f, r * 0.26f, r * 0.15f, new Color(255, 255, 255, 70));
        _small.DrawCentered(_batch, label, cx, cy - 12, Color.FromHex("#F4EFE3"));
    }

    private void DrawMinimap(WelcomeInfo w, float x, float y, float width, float height, float alpha)
    {
        byte a = (byte)(alpha * 255);
        _batch.FillRect(x - 2, y - 2, width + 4, height + 4, new Color(58, 48, 38, a));
        _batch.FillRect(x, y, width, height, new Color(22, 20, 16, a));
        float sx = width / w.ZoneWidth, sy = height / w.ZoneHeight;
        // Scenery: trees, rocks and buildings as soft marks, so forests, camps and ruins read on the map.
        if (_data.Zones.TryGetValue(w.ZoneId, out var mz))
            foreach (var d in mz.Decor)
            {
                if (!_data.Props.TryGetValue(d.Prop, out var pd) || pd.Footprint is not { } fp) continue;
                bool tree = d.Prop.Contains("tree");
                var c = tree ? new Color(52, 74, 40, (byte)(a * 0.85f)) : pd.Light is not null ? new Color(200, 120, 50, a) : new Color(86, 78, 66, (byte)(a * 0.8f));
                float r = tree ? 5 : Math.Clamp(fp[0] * sx * 1.6f, 2, 6);
                _batch.Draw(_softDot, x + d.X * sx - r, y + d.Y * sy - r, r * 2, r * 2, c);
            }
        foreach (var r in _conn.Entities.Values)
        {
            var s = r.Current;
            if (s.Dead && s.Kind != EntityKind.Player) continue;
            var (c, d) = s.Kind switch
            {
                EntityKind.Player => (s.Id == _conn.YourId ? Color.FromHex("#F2D25A") : Color.FromHex("#7FB2FF"), 6f),
                EntityKind.Monster => ((s.Flags & EntityFlags.Superunique) != 0 ? Color.FromHex("#E8892B") : Color.FromHex("#C0392B"), (s.Flags & EntityFlags.Superunique) != 0 ? 6f : 3f),
                _ => (Color.FromHex("#E6C14A"), 2f),
            };
            _batch.FillRect(x + s.Pos.X * sx - d / 2, y + s.Pos.Y * sy - d / 2, d, d, c);
        }
        foreach (var ex in w.ExitList)
            _batch.FillRect(x + ex.X * sx - 4, y + ex.Y * sy - 4, 8, 8, Color.FromHex("#F2D25A"));
        if (w.Waypoint is { } mwp) _batch.FillRect(x + mwp[0] * sx - 4, y + mwp[1] * sy - 4, 8, 8, Color.FromHex("#7FB2FF"));
        // Camera frame.
        var tl = _camera - HalfView;
        float fx = x + tl.X * sx, fyy = y + tl.Y * sy, fw = VW / Zoom * sx, fh = VH / Zoom * sy;
        var edge = new Color(232, 228, 218, (byte)(a * 0.6f));
        _batch.FillRect(fx, fyy, fw, 1, edge); _batch.FillRect(fx, fyy + fh, fw, 1, edge);
        _batch.FillRect(fx, fyy, 1, fh, edge); _batch.FillRect(fx + fw, fyy, 1, fh, edge);
    }

    // ------------------------------------------------------------------ teardown

    private unsafe void SaveScreenshot(string path, int w, int h)
    {
        var pixels = new byte[w * h * 4];
        _gl.Viewport(0, 0, (uint)w, (uint)h);
        fixed (byte* p = pixels) _gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        var flipped = new byte[pixels.Length];
        int row = w * 4;
        for (int y = 0; y < h; y++) System.Buffer.BlockCopy(pixels, y * row, flipped, (h - 1 - y) * row, row);
        for (int i = 3; i < flipped.Length; i += 4) flipped[i] = 255; // the screen ignores alpha; the file shouldn't keep it
        using var fs = File.Create(path);
        new ImageWriter().WritePng(flipped, w, h, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, fs);
        Console.WriteLine($"[client] screenshot saved to {path}");
    }

    /// <summary>GL resources are freed here, while the context is still current.</summary>
    private void OnClosing()
    {
        _conn?.Dispose();
        _audio?.Dispose();
        _title?.Dispose(); _ui?.Dispose(); _small?.Dispose(); _disc?.Dispose();
        _assets?.Dispose();
        _batch?.Dispose();
        _input?.Dispose();
        Console.WriteLine("[client] closed cleanly");
    }

    public void Dispose() => _window.Dispose();

    private static string Roman(int act) => act switch { 2 => "II", 3 => "III", 4 => "IV", _ => "I" };
}
