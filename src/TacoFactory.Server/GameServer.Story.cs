using LiteNetLib;
using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;
using TacoFactory.Net;

namespace TacoFactory.Server;

/// <summary>
/// The story layer: cutscenes, NPC dialogue and main quests per act, act gates, disguises, allied
/// Templars, party votes on yielded bosses, and the four endings.
/// </summary>
public sealed partial class GameServer
{
    public const string PrologueZone = "payns_courtyard", PrologueDone = "prologue_done", FinalBoss = "philip_iv";

    /// <summary>Called on every zone entry: first-visit cutscene and disguise.</summary>
    private void StoryOnEnter(Session s, Entity p, ZoneDef z)
    {
        if (z.Cutscene is string cs && s.Unlocks.Add("seen:" + cs)) SendCutscene(s, cs);
        if (z.Infiltration && (z.DisguiseUnlock is null || s.Unlocks.Contains(z.DisguiseUnlock)))
        {
            s.Instance!.World.Disguise(p);
            Tell(s, "{#C9B88A}You wear a cleric's robes. Guards will not look twice from afar; don't linger near them, and don't draw steel.{/}");
        }
        else if (z.Infiltration) Tell(s, "{#E0A060}Without a disguise every guard here knows a Templar on sight.{/}");
    }

    /// <summary>Allies a party brings to zones with an army (Act IV): one per story unlock any member holds.</summary>
    private int AllyBonus(Session s, ZoneDef z)
    {
        if (z.AllyUnlocks.Count == 0) return 0;
        var members = s.Party.Members.Where(m => _sessions.ContainsValue(m)).ToList();
        return z.AllyUnlocks.Count(u => members.Any(m => m.Unlocks.Contains(u)));
    }

    private void SendCutscene(Session s, string id)
    {
        if (!_data.Cutscenes.TryGetValue(id, out var c)) return;
        SendUi(s, UiKind.Cutscene, new CutsceneView(c.Id, c.Title, c.Image, c.Lines.Select(l => new CutsceneLineView(l.Speaker, l.Text)).ToList()));
    }

    /// <summary>The furthest act this character has opened; they return to its town on login.</summary>
    private int HomeAct(Session s) => s.Unlocks.Contains("act4") ? 4 : s.Unlocks.Contains("act3") ? 3 : s.Unlocks.Contains("act2") ? 2 : 1;

    private bool Has(Session s, string unlock) => s.QuestsDone.Contains(unlock) || s.Unlocks.Contains(unlock);

    // ------------------------------------------------------------------ dialogue and quests

    /// <summary>The next main quest of an act this character can take.</summary>
    private QuestDef? NextQuest(Session s, int act, string? giver = null) =>
        _data.Quests.Where(q => q.Act == act && !q.Optional && !s.QuestsDone.Contains(q.Id)
                                && (q.After is null || s.QuestsDone.Contains(q.After))
                                && (giver is null || q.Giver is null || q.Giver == giver))
            .FirstOrDefault();

    /// <summary>An NPC's turn to speak: quest briefing for quest givers, otherwise their lines in turn.</summary>
    private void Speak(Session s, NpcDef npc, string text, string? quest = null) =>
        SendUi(s, UiKind.Dialogue, new DialogueView(npc.Name, npc.Sprite.Length > 0 ? npc.Sprite : $"sprites/npc_{npc.Id}.png", text, quest));

    private string NextLine(Session s, NpcDef npc)
    {
        if (npc.Lines.Count == 0) return "";
        int i = s.NpcLine.GetValueOrDefault(npc.Id, -1) + 1;
        if (i >= npc.Lines.Count) i = 0;
        s.NpcLine[npc.Id] = i;
        return npc.Lines[i];
    }

    private void QuestGiver(Session s, NpcDef npc)
    {
        int act = s.Instance!.Zone.Act;
        var next = NextQuest(s, act, npc.Id);
        if (next is null)
        {
            bool actDone = _data.Quests.Where(q => q.Act == act && !q.Optional).All(q => s.QuestsDone.Contains(q.Id));
            string line = NextLine(s, npc);
            Speak(s, npc, actDone
                ? (act < _data.Acts ? $"{(line.Length > 0 ? line + " " : "")}There is nothing more for you here. The road on is open."
                                    : $"{(line.Length > 0 ? line + " " : "")}It is finished. Whatever comes, the Temple remembers you.")
                : line.Length > 0 ? line : "Not now, brother. Others need you first.");
            return;
        }
        string where = _data.Zones.TryGetValue(next.Zone, out var z) ? z.Name : next.Zone;
        Speak(s, npc, $"{next.Briefing ?? next.Objective.Text}", $"{next.Name}: {next.Objective.Text} ({where})");
    }

    // ------------------------------------------------------------------ act gates

    private bool ExitAllowed(Session s, ExitDef exit)
    {
        if (exit.RequiresUnlock is not string u || Has(s, u)) return true;
        s.ArrivedAt = Clock + 3;
        Tell(s, exit.LockedText ?? "The way is barred for now.");
        return false;
    }

    // ------------------------------------------------------------------ votes on yielded bosses

    private void StartVote(Instance inst, string uniqueId)
    {
        if (!_data.Bosses.TryGetValue(uniqueId, out var boss) || boss.Choice is not { } choice || inst.Vote is not null) return;
        inst.Vote = new Vote { Boss = boss, Choice = choice, Deadline = Clock + choice.Seconds };
        Log($"{inst.Key}: {boss.Id} yielded; the party votes");
        SendVote(inst);
    }

    private void CastVote(Session s, string optionId)
    {
        if (s.Instance?.Vote is not { } v || v.Choice.Options.All(o => o.Id != optionId)) return;
        v.Votes[s] = optionId;
        SendVote(s.Instance);
        if (s.Instance.Sessions.All(x => v.Votes.ContainsKey(x))) FinishVote(s.Instance);
    }

    private void SendVote(Instance inst, string result = "")
    {
        if (inst.Vote is not { } v) return;
        var counts = v.Choice.Options.ToDictionary(o => o.Id, o => v.Votes.Values.Count(x => x == o.Id));
        string name = _data.Superuniques.TryGetValue(v.Boss.Id, out var su) ? su.Name : v.Boss.Id;
        foreach (var s in inst.Sessions)
            SendUi(s, UiKind.Choice, new ChoiceView(name, v.Choice.Prompt, v.Choice.Options.Select(o => new ChoiceOptionView(o.Id, o.Text)).ToList(),
                System.Math.Max(0, v.Deadline - Clock), v.Votes.GetValueOrDefault(s), counts, result.Length > 0, result));
    }

    private void TickVotes(Instance inst)
    {
        if (inst.Vote is { } v && Clock >= v.Deadline) FinishVote(inst);
    }

    /// <summary>
    /// Majority decides what happens to the boss; a tie goes to the party leader's vote (or the first option).
    /// Each player keeps the outcome they voted for in their own story; those who did not vote take the result.
    /// </summary>
    private void FinishVote(Instance inst)
    {
        if (inst.Vote is not { } v) return;
        var opts = v.Choice.Options;
        int best = opts.Max(o => v.Votes.Values.Count(x => x == o.Id));
        var top = opts.Where(o => v.Votes.Values.Count(x => x == o.Id) == best).ToList();
        var leaderVote = inst.Sessions.Select(x => x.Party.Leader).FirstOrDefault(l => v.Votes.ContainsKey(l)) is { } leader ? v.Votes[leader] : null;
        var winner = top.Count == 1 ? top[0] : top.FirstOrDefault(o => o.Id == leaderVote) ?? top[0];
        SendVote(inst, winner.Text);
        foreach (var s in inst.Sessions)
        {
            var mine = opts.FirstOrDefault(o => o.Id == v.Votes.GetValueOrDefault(s)) ?? winner;
            if (mine.Unlock.Length > 0) s.Unlocks.Add(mine.Unlock);
            if (winner.Line.Length > 0) Tell(s, winner.Line);
        }
        int credit = inst.Sessions.FirstOrDefault(x => x.Entity is not null)?.EntityId ?? 0;
        inst.World.ResolveYield(winner.Outcome, credit);
        Log($"{inst.Key}: the party chose '{winner.Id}' for {v.Boss.Id}");
        inst.Vote = null;
    }

    // ------------------------------------------------------------------ boss deaths and the endings

    private void StoryOnUniqueKilled(Instance inst, string uniqueId)
    {
        string? cs = _data.Bosses.TryGetValue(uniqueId, out var boss) && boss.DeathCutscene is string b ? b
            : _data.Superuniques.TryGetValue(uniqueId, out var su) ? su.DeathCutscene : null;
        if (cs is not null) foreach (var s in inst.Sessions) SendCutscene(s, cs);
        if (uniqueId != FinalBoss) return;
        foreach (var s in inst.Sessions)
        {
            string ending = EndingFor(s);
            s.Unlocks.Add("ending_" + ending);
            s.Unlocks.Add("game_complete");
            SendCutscene(s, "ending_" + ending);
            Log($"{s.Name} reached the {ending} ending");
        }
    }

    private static string EndingFor(Session s) => Story.Ending(s.Unlocks);
}
