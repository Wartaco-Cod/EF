using Silk.NET.Input;
using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;
using TacoFactory.Net;

namespace TacoFactory.Client;

/// <summary>
/// Controller play. Left stick moves; A attacks the nearest foe (or talks to a nearby NPC);
/// X, Y, B, LB, RB, LT, RT cast spells 1-7; d-pad up/down drink potions, left goes to town,
/// right opens the bag; Start opens the menu, Back the map. Windows still use the mouse; B closes them.
/// </summary>
public sealed partial class GameClient
{
    private const float StickDeadzone = 0.25f, TriggerThreshold = 0.5f, PadTargetRange = 650;
    private readonly HashSet<ButtonName> _padHeld = [];
    private Vec2 _padFacing = new(0, 1);

    private static readonly (ButtonName Button, byte Slot)[] PadSpellButtons =
        [(ButtonName.X, 1), (ButtonName.Y, 2), (ButtonName.B, 3), (ButtonName.LeftBumper, 4), (ButtonName.RightBumper, 5)];

    private IGamepad? Pad => _input.Gamepads.FirstOrDefault(g => g.IsConnected);

    /// <summary>Menu-style buttons fire once per press; handled every frame before input is read.</summary>
    private void PadButtons()
    {
        if (Pad is not { } pad) return;
        foreach (var b in pad.Buttons)
        {
            bool was = _padHeld.Contains(b.Name);
            if (b.Pressed && !was) { _padHeld.Add(b.Name); PadPressed(b.Name); }
            else if (!b.Pressed && was) _padHeld.Remove(b.Name);
        }
    }

    /// <summary>Menus, windows, cutscenes and the login screens are driven with the d-pad and A when a controller is used.</summary>
    private bool PadNavigating => _conn.Welcome is null || _openWindow != Win.None || _cutscene is not null || _choice is not null || _dialogue is not null;
    private Vec2? _padFocus;

    private void PadPressed(ButtonName b)
    {
        if (_cutscene is not null)
        {
            if (b is ButtonName.A or ButtonName.B or ButtonName.Start) AdvanceCutscene(skipAll: b == ButtonName.Start);
            return;
        }
        if (PadNavigating)
        {
            switch (b)
            {
                case ButtonName.DPadUp: PadMove(new Vec2(0, -1)); break;
                case ButtonName.DPadDown: PadMove(new Vec2(0, 1)); break;
                case ButtonName.DPadLeft: PadMove(new Vec2(-1, 0)); break;
                case ButtonName.DPadRight: PadMove(new Vec2(1, 0)); break;
                case ButtonName.A:
                    if (PadFocused() is { } f) { Sfx("ui_click", 0.6f); f.OnClick(); }
                    break;
                case ButtonName.B or ButtonName.Start:
                    if (_dialogue is not null) _dialogue = null;
                    else if (_openWindow != Win.None) CloseWindow();
                    break;
            }
            return;
        }
        switch (b)
        {
            case ButtonName.Start: Open(Win.Menu); break;
            case ButtonName.Back: _mapOpen = !_mapOpen; break;
            case ButtonName.DPadUp: _conn.Command("drink", "health"); break;
            case ButtonName.DPadDown: _conn.Command("drink", "mana"); break;
            case ButtonName.DPadLeft: _conn.Travel(TravelKind.Town); break;
            case ButtonName.DPadRight: _invOpen = !_invOpen; break;
            case ButtonName.A when PadFoe() is null && NearestNpc() is { } npc: _talkTarget = npc.Current.Id; break;
        }
    }

    private (float X, float Y, float W, float H, Action OnClick)? PadFocused()
    {
        if (_buttons.Count == 0) return null;
        _padFocus ??= new Vec2(_buttons[0].X + _buttons[0].W / 2, _buttons[0].Y + _buttons[0].H / 2);
        var f = _padFocus.Value;
        return _buttons.OrderBy(b => Vec2.Distance(new Vec2(b.X + b.W / 2, b.Y + b.H / 2), f)).First();
    }

    /// <summary>Moves the controller's highlight to the nearest button in that direction.</summary>
    private void PadMove(Vec2 dir)
    {
        if (PadFocused() is not { } cur) return;
        var c = new Vec2(cur.X + cur.W / 2, cur.Y + cur.H / 2);
        (float X, float Y, float W, float H, Action OnClick)? best = null;
        float bestScore = float.MaxValue;
        foreach (var b in _buttons)
        {
            var p = new Vec2(b.X + b.W / 2, b.Y + b.H / 2);
            var d = p - c;
            float along = Vec2.Dot(d, dir);
            if (along < 8) continue;
            float across = MathF.Abs(Vec2.Dot(d, new Vec2(-dir.Y, dir.X)));
            float score = along + across * 2.5f;
            if (score < bestScore) { bestScore = score; best = b; }
        }
        if (best is { } nb) { _padFocus = new Vec2(nb.X + nb.W / 2, nb.Y + nb.H / 2); Sfx("ui_click", 0.25f); }
    }

    /// <summary>Outline round the button the controller will press.</summary>
    private void DrawPadFocus()
    {
        if (Pad is null || !PadNavigating || PadFocused() is not { } f) return;
        var c = Rendering.Color.FromHex("#9FC6FF");
        _batch.FillRect(f.X - 3, f.Y - 3, f.W + 6, 3, c); _batch.FillRect(f.X - 3, f.Y + f.H, f.W + 6, 3, c);
        _batch.FillRect(f.X - 3, f.Y, 3, f.H, c); _batch.FillRect(f.X + f.W, f.Y, 3, f.H, c);
    }

    /// <summary>Movement and combat from the controller, or null when the controller is idle.</summary>
    private PlayerInput? PadInput(RemoteEntity me)
    {
        if (Pad is not { } pad || _openWindow != Win.None) return null;
        var stick = pad.Thumbsticks.Count > 0 ? new Vec2(pad.Thumbsticks[0].X, pad.Thumbsticks[0].Y) : Vec2.Zero;
        bool lt = pad.Triggers.Count > 0 && pad.Triggers[0].Position > TriggerThreshold;
        bool rt = pad.Triggers.Count > 1 && pad.Triggers[1].Position > TriggerThreshold;
        var moving = stick.Length > StickDeadzone;
        if (moving) _padFacing = stick.Normalized();

        byte slot = 0;
        foreach (var (button, s) in PadSpellButtons) if (_padHeld.Contains(button)) slot = s;
        if (lt) slot = 6;
        if (rt) slot = 7;
        bool attack = _padHeld.Contains(ButtonName.A);

        if (slot > 0 && BarSpell(slot) is SpellDef spell)
        {
            var foe = PadFoe();
            int target = spell.Kind switch
            {
                SpellKind.Heal => WeakestAlly(me)?.Current.Id ?? me.Current.Id,
                SpellKind.Revive => NearestDeadAlly(me)?.Current.Id ?? 0,
                SpellKind.Bolt or SpellKind.Burst or SpellKind.Drain => foe?.Current.Id ?? 0,
                _ => me.Current.Id,
            };
            var at = foe?.Current.Pos ?? me.Current.Pos + _padFacing * 300;
            return new PlayerInput(Vec2.Zero, PlayerAction.UseSkill, slot, target, at);
        }
        if (attack && PadFoe() is { } enemy)
            return new PlayerInput(Vec2.Zero, PlayerAction.UseSkill, 0, enemy.Current.Id, enemy.Current.Pos);
        if (moving) return new PlayerInput(stick, PlayerAction.None, 0, 0, Vec2.Zero);
        return null;
    }

    /// <summary>The foe to hit: the duel opponent, otherwise the nearest monster, favouring the one the stick points at.</summary>
    private RemoteEntity? PadFoe()
    {
        if (DuelFoe() is int duel && _conn.Entities.TryGetValue(duel, out var d)) return d;
        if (!_conn.Entities.TryGetValue(_conn.YourId, out var me)) return null;
        RemoteEntity? best = null;
        float bestScore = float.MaxValue;
        foreach (var r in _conn.Entities.Values)
        {
            if (r.Current.Kind != EntityKind.Monster || r.Current.Dead || (r.Current.Flags & EntityFlags.Ally) != 0) continue;
            var to = r.Current.Pos - me.Current.Pos;
            float dist = to.Length;
            if (dist > PadTargetRange) continue;
            float facing = dist > 1 ? Vec2.Dot(to / dist, _padFacing) : 1; // -1 behind .. 1 straight ahead
            float score = dist * (1.6f - 0.6f * facing);
            if (score < bestScore) { bestScore = score; best = r; }
        }
        return best;
    }

    private RemoteEntity? WeakestAlly(RemoteEntity me) => _conn.Entities.Values
        .Where(r => r.Current.Kind == EntityKind.Player && !r.Current.Dead && Vec2.Distance(r.Current.Pos, me.Current.Pos) < PadTargetRange)
        .OrderBy(r => r.Current.MaxHp == 0 ? 1 : (float)r.Current.Hp / r.Current.MaxHp).FirstOrDefault();

    private RemoteEntity? NearestDeadAlly(RemoteEntity me) => _conn.Entities.Values
        .Where(r => r.Current.Kind == EntityKind.Player && r.Current.Dead && r.Current.Id != me.Current.Id)
        .OrderBy(r => Vec2.Distance(r.Current.Pos, me.Current.Pos)).FirstOrDefault(r => Vec2.Distance(r.Current.Pos, me.Current.Pos) < PadTargetRange);

    private RemoteEntity? NearestNpc()
    {
        if (!_conn.Entities.TryGetValue(_conn.YourId, out var me)) return null;
        return _conn.Entities.Values.Where(r => r.Current.Kind == EntityKind.Npc)
            .OrderBy(r => Vec2.Distance(r.Current.Pos, me.Current.Pos)).FirstOrDefault(r => Vec2.Distance(r.Current.Pos, me.Current.Pos) < 260);
    }
}
