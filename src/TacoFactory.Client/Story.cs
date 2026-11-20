using Silk.NET.Input;
using TacoFactory.Client.Rendering;
using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;
using TacoFactory.Net;
using Color = TacoFactory.Client.Rendering.Color;

namespace TacoFactory.Client;

/// <summary>Story on screen: cutscenes, NPC dialogue, party votes on yielded bosses, disguise and the act-tabbed waypoints.</summary>
public sealed partial class GameClient
{
    private readonly Queue<CutsceneView> _cutscenes = new();
    private CutsceneView? _cutscene;
    private int _cutsceneLine;
    private double _cutsceneShownAt;
    private ChoiceView? _choice;
    private double _choiceAt, _choiceClosesAt;
    private DialogueView? _dialogue;
    private Vec2 _dialogueAt;
    private int _waypointAct;
    private string? _pendingWaypoint; // a destination picked away from the waypoint: travel on arrival

    private bool StoryBlocking => _cutscene is not null;

    private void HandleStoryUi(string kind, string json)
    {
        switch (kind)
        {
            case UiKind.Cutscene:
                if (UiKind.Deserialize<CutsceneView>(json) is { } c) _cutscenes.Enqueue(c);
                break;
            case UiKind.Choice:
                _choice = UiKind.Deserialize<ChoiceView>(json);
                _choiceAt = Now;
                _choiceClosesAt = _choice is { Final: true } ? Now + 5 : double.MaxValue;
                break;
            case UiKind.Dialogue:
                _dialogue = UiKind.Deserialize<DialogueView>(json);
                if (_conn.Entities.TryGetValue(_conn.YourId, out var me)) _dialogueAt = me.Current.Pos;
                break;
        }
    }

    /// <summary>Space, Enter, Escape or a click moves the cutscene on; the last line closes it.</summary>
    private bool StoryKey(Key key)
    {
        if (_cutscene is not null && key is Key.Space or Key.Enter or Key.KeypadEnter or Key.Escape)
        {
            AdvanceCutscene(skipAll: key == Key.Escape);
            return true;
        }
        if (_dialogue is not null && key == Key.Escape) { _dialogue = null; return true; }
        return false;
    }

    private void AdvanceCutscene(bool skipAll = false)
    {
        if (_cutscene is null || Now - _cutsceneShownAt < 0.25) return;
        _cutsceneLine++;
        _cutsceneShownAt = Now;
        Sfx("page", 0.6f);
        if (skipAll || _cutsceneLine >= _cutscene.Lines.Count) { _cutscene = null; _cutsceneLine = 0; }
    }

    /// <summary>Drawn last, over everything.</summary>
    private void DrawStory(RemoteEntity me)
    {
        if (_cutscene is null && _cutscenes.TryDequeue(out var next)) { _cutscene = next; _cutsceneLine = 0; _cutsceneShownAt = Now; }
        if (_options.Autoplay && _cutscene is not null && Now - _cutsceneShownAt > 1.5) { _cutscene = null; _cutsceneLine = 0; }
        if (_dialogue is not null && Vec2.Distance(me.Current.Pos, _dialogueAt) > 260) _dialogue = null; // walked away
        if (_dialogue is { } dlg) DrawDialogue(dlg);
        if (_choice is { } ch)
        {
            if (Now > _choiceClosesAt) _choice = null;
            else DrawChoice(ch);
        }
        if ((me.Current.Flags & EntityFlags.Disguised) != 0)
            _ui.DrawCentered(_batch, "{#C9B88A}Disguised{/} {#7A7060}· walk, don't fight, don't linger near guards{/}", VW / 2, 120, Color.White);
        if (_cutscene is { } c) DrawCutscene(c);
    }

    private void DrawCutscene(CutsceneView c)
    {
        _buttons.Clear();
        _batch.FillRect(0, 0, VW, VH, new Color(0, 0, 0, 255));
        var img = _assets.Texture(c.Image.Length > 0 ? c.Image : "ui/title.png");
        float fade = (float)Math.Clamp((Now - _cutsceneShownAt) * 3, 0, 1);
        _batch.Draw(img, 0, 0, VW, VH, new Color(255, 255, 255, 230));
        _batch.FillRect(0, VH - 330, VW, 330, new Color(8, 6, 4, 215));
        _batch.FillRect(0, VH - 330, VW, 2, Color.FromHex("#C9A227"));
        _title.DrawCentered(_batch, c.Title.ToUpperInvariant(), VW / 2, 70, Color.FromHex("#E6C14A"));
        var line = c.Lines[Math.Min(_cutsceneLine, c.Lines.Count - 1)];
        float y = VH - 290;
        if (line.Speaker.Length > 0) { _ui.DrawCentered(_batch, $"{{#E6C14A}}{line.Speaker}{{/}}", VW / 2, y, Color.White); y += 46; }
        foreach (var l in WrapUi(line.Text, 1300))
        {
            _ui.DrawCentered(_batch, l, VW / 2, y, new Color(232, 228, 218, (byte)(255 * fade)));
            y += 38;
        }
        _small.DrawCentered(_batch, $"{{#7A7060}}{_cutsceneLine + 1} / {c.Lines.Count}   ·   Space or click to continue, Esc to skip{{/}}", VW / 2, VH - 48, Color.White);
        _buttons.Add((0, 0, VW, VH, () => AdvanceCutscene()));
        _blockRects.Add((0, 0, VW, VH));
    }

    private IEnumerable<string> WrapUi(string text, float width)
    {
        var line = "";
        foreach (var word in text.Split(' '))
        {
            string trial = line.Length == 0 ? word : line + " " + word;
            if (_ui.Measure(trial) > width && line.Length > 0) { yield return line; line = word; }
            else line = trial;
        }
        if (line.Length > 0) yield return line;
    }

    private void DrawDialogue(DialogueView d)
    {
        const float w = 1100, h = 200, x = (VW - w) / 2, y = 700;
        _blockRects.Add((x, y, w, h));
        _batch.FillRect(x, y, w, h, new Color(16, 13, 10, 240));
        _batch.FillRect(x, y, w, 2, Color.FromHex("#C9A227"));
        _batch.Draw(_assets.Texture(d.Sprite), x + 10, y + 10, 180, 180, Color.White);
        _ui.Draw(_batch, d.Npc, x + 210, y + 16, Color.FromHex("#E6C14A"));
        float ty = y + 56;
        foreach (var l in Wrap(d.Text, w - 260)) { _small.Draw(_batch, $"{{#E8E4DA}}{l}{{/}}", x + 210, ty, Color.White); ty += 26; }
        if (d.Quest is string q) _small.Draw(_batch, $"{{#F2D25A}}Quest:{{/}} {{#C9B88A}}{q}{{/}}", x + 210, Math.Max(ty + 6, y + h - 40), Color.White);
        Button(x + w - 44, y + 12, 30, 30, "X", () => _dialogue = null, tone: "plain");
        _buttons.Add((x, y, w - 50, h, () => _dialogue = null));
    }

    private void DrawChoice(ChoiceView c)
    {
        float w = 900, h = 170 + c.Options.Count * 64, x = (VW - w) / 2, y = 220;
        _blockRects.Add((x, y, w, h));
        _batch.FillRect(x, y, w, h, new Color(16, 13, 10, 245));
        _batch.FillRect(x, y, w, 3, Color.FromHex("#E8892B"));
        _title.Draw(_batch, c.Boss.ToUpperInvariant(), x + 28, y + 18, Color.FromHex("#E8892B"));
        float ty = y + 72;
        foreach (var l in Wrap(c.Prompt, w - 56)) { _small.Draw(_batch, $"{{#E8E4DA}}{l}{{/}}", x + 28, ty, Color.White); ty += 26; }
        ty = y + 130;
        foreach (var o in c.Options)
        {
            int votes = c.Votes.GetValueOrDefault(o.Id);
            bool mine = c.MyVote == o.Id;
            string id = o.Id;
            Button(x + 28, ty, w - 180, 48, o.Text, () => _conn.Command("vote", id), enabled: !c.Final, tone: mine ? "gold" : "plain");
            _ui.Draw(_batch, $"{{#C9B88A}}{votes} vote{(votes == 1 ? "" : "s")}{{/}}", x + w - 140, ty + 8, Color.White);
            ty += 64;
        }
        double left = c.SecondsLeft - (Now - _choiceAt);
        _small.Draw(_batch, c.Final
            ? $"{{#F2D25A}}The party has chosen: {c.Result}{{/}}"
            : $"{{#9A9080}}Your party decides together; a tie goes to the party leader. {Math.Max(0, left):F0} s left. You keep the choice you voted for.{{/}}",
            x + 28, y + h - 34, Color.White);
    }

    // ------------------------------------------------------------------ waypoints by act

    private void DrawWaypointsByAct(WelcomeInfo w, PlayerStats? st, RemoteEntity me)
    {
        const float x = 1040, width = 520;
        Panel(x, width, "WAYPOINTS", () => _waypointsOpen = false);
        if (_waypointAct == 0) _waypointAct = Math.Max(1, w.Act);
        bool onWaypoint = w.Waypoint is { } wp && Vec2.Distance(me.Current.Pos, new Vec2(wp[0], wp[1])) < 160;
        _small.Draw(_batch, onWaypoint ? "Choose where to go." : w.Waypoint is not null ? "Choose where to go; you walk to the waypoint first." : "{#E0A060}No waypoint here.{/}  T: back to this act's town.", x + 24, 164, Color.FromHex("#9A9080"));
        var found = st?.WaypointList.ToHashSet() ?? [];
        string[] roman = ["I", "II", "III", "IV"];
        for (int a = 1; a <= _data.Acts; a++)
        {
            int act = a;
            bool any = _data.Zones.Values.Any(z => z.Act == act && found.Contains(z.Id));
            Button(x + 24 + (a - 1) * 120, 196, 110, 32, $"Act {roman[a - 1]}", () => _waypointAct = act, enabled: any, tone: _waypointAct == a ? "gold" : "plain");
        }
        float y = 246;
        if (found.Count(id => _data.Zones.TryGetValue(id, out var fz) && !fz.IsTown) == 0)
            _small.Draw(_batch, "{#9A9080}Touch a waypoint out in the fields to add it here.{/}", x + 24, 650, Color.White);
        foreach (var z in _data.Zones.Values.Where(z => z.Act == _waypointAct && !z.IsGuildHall && !z.Prologue && (z.IsTown || z.Waypoint is not null))
                     .OrderBy(z => z.IsTown ? 0 : 1).ThenBy(z => z.LevelMin).ThenBy(z => z.Id))
        {
            bool known = found.Contains(z.Id), here = z.Id == w.ZoneId;
            var c = here ? Color.FromHex("#F2D25A") : known ? Color.FromHex("#E8E4DA") : Color.FromHex("#5A5448");
            string label = known ? z.Name + (z.Floor is string f ? $" {{#9A9080}}· {f}{{/}}" : "") : "Not yet found";
            _small.Draw(_batch, label, x + 24, y + 4, c);
            if (known && !z.IsTown) _small.Draw(_batch, $"{{#7A7060}}{z.LevelMin}-{z.LevelMax}{{/}}", x + width - 90, y + 4, Color.White);
            if (known && !here && w.Waypoint is { } wpt)
            {
                string id = z.Id;
                if (_pendingWaypoint == id) _batch.FillRect(x + 16, y - 2, 4, 32, Color.FromHex("#E6C14A"));
                _buttons.Add((x + 16, y - 2, width - 32, 32, () =>
                {
                    if (onWaypoint) { _conn.Travel(TravelKind.Waypoint, id); _waypointsOpen = false; _pendingWaypoint = null; }
                    else { _pendingWaypoint = id; _moveTarget = new Vec2(wpt[0], wpt[1]); _moveTargetAt = Now; } // walk there, then go
                }));
            }
            y += 34;
            if (y > 890) break;
        }
    }
}
