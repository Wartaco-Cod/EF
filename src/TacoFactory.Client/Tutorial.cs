using TacoFactory.Client.Rendering;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;
using TacoFactory.Net;
using Color = TacoFactory.Client.Rendering.Color;

namespace TacoFactory.Client;

/// <summary>
/// First-time hints: one short card at a time, each shown once on this PC, triggered by what the player is doing.
/// Can be turned off in the game menu.
/// </summary>
public sealed partial class GameClient
{
    private string? _hint;
    private double _hintSince;
    private readonly Queue<(string Id, string Text)> _hintQueue = new();

    private static readonly Dictionary<string, string> Hints = new()
    {
        ["move"] = "Move by clicking the ground, or with W A S D. Click a soldier to attack; hold the button to keep swinging.",
        ["potion"] = "You're hurt. Z drinks a health potion, X a mana potion. Merchants sell more.",
        ["loot"] = "Walk over loot to pick it up. I opens your bag; drag items onto your gear to wear them.",
        ["levelup"] = "You have build points to spend. K opens your skills; spells you learn go on 1-7 and the right mouse button.",
        ["town"] = "Click people to talk. Gerard gives you your next task; Q keeps your quests. The strongbox stores items.",
        ["waypoint"] = "This is a waypoint. Stand on it and press V to travel to any waypoint you have found. T takes you back to town.",
        ["party"] = "Press Enter to talk. /invite name forms a party of up to six; /duel name challenges someone in the arena.",
        ["boss"] = "A boss. Watch the ground: red circles mark where blows will land. Step out of them.",
        ["disguise"] = "You are disguised. Walk past the guards; don't stand next to them, and don't attack, or the whole court will come.",
        ["choice"] = "Your enemy yields. Everyone in the party votes; you keep the choice you made in your own story.",
    };

    private void Hint(string id)
    {
        if (!_settings.Hints || _settings.SeenHints.Contains(id) || !Hints.TryGetValue(id, out var text)) return;
        if (_hint == text || _hintQueue.Any(h => h.Id == id)) return;
        _settings.SeenHints.Add(id);
        _settings.Save(_settingsPath);
        _hintQueue.Enqueue((id, text));
    }

    /// <summary>Watches for the moments each hint is for.</summary>
    private void TutorialTick(WelcomeInfo w, RemoteEntity me, PlayerStats? st)
    {
        var zone = _data.Zones.GetValueOrDefault(w.ZoneId);
        if (zone?.Prologue == true) Hint("move");
        if (w.IsTown && !zone!.IsGuildHall) Hint("town");
        if (me.Current.Hp > 0 && me.Current.Hp < me.Current.MaxHp / 2) Hint("potion");
        if (st is { BuildPoints: > 0 } && st.Level > 1) Hint("levelup");
        if (_conn.Entities.Values.Any(e => e.Current.Kind == EntityKind.Loot && Vec2.Distance(e.Current.Pos, me.Current.Pos) < 400)) Hint("loot");
        if (w.Waypoint is { } wp && Vec2.Distance(me.Current.Pos, new Vec2(wp[0], wp[1])) < 400) Hint("waypoint");
        if (w.IsTown && st?.Level >= 3) Hint("party");
        if (_conn.Entities.Values.Any(e => (e.Current.Flags & EntityFlags.Boss) != 0 && !e.Current.Dead && Vec2.Distance(e.Current.Pos, me.Current.Pos) < 900)) Hint("boss");
        if ((me.Current.Flags & EntityFlags.Disguised) != 0) Hint("disguise");
        if (_choice is not null) Hint("choice");
    }

    private void DrawHint()
    {
        if (_hint is null && _hintQueue.TryDequeue(out var next)) { _hint = next.Text; _hintSince = Now; }
        if (_hint is null || _cutscene is not null) return;
        if (Now - _hintSince > 14) { _hint = null; return; }
        const float w = 760, h = 92, x = (VW - w) / 2, y = 150;
        float a = (float)Math.Clamp(Math.Min((Now - _hintSince) * 4, (14 - (Now - _hintSince)) * 2), 0, 1);
        _blockRects.Add((x, y, w, h));
        _batch.FillRect(x, y, w, h, new Color(16, 13, 10, (byte)(225 * a)));
        _batch.FillRect(x, y, 4, h, Color.FromHex("#9FC6FF"));
        float ty = y + 12;
        foreach (var line in Wrap(_hint, w - 160)) { _small.Draw(_batch, $"{{#E8E4DA}}{line}{{/}}", x + 20, ty, Color.White); ty += 24; }
        Button(x + w - 120, y + (h - 34) / 2, 100, 34, "Got it", () => _hint = null, tone: "blue");
    }
}
