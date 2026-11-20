using System.Diagnostics;
using TacoFactory.Core.Content;
using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;
using TacoFactory.Net;

/// <summary>
/// Networked story check: start the server with --open --start-zone town_in_shadow --start-level 34. Three bots party up,
/// take the waypoint to the Royal Garrison and fight
/// Captain Réginald; when he yields, two vote to spare him and one to kill. The majority spares him; each bot keeps
/// the outcome it voted for; the quest completes for all.
/// </summary>
public static class StoryTest
{
    public static int Run(string host, int port)
    {
        var data = GameData.Load(ContentRoot.Locate(AppContext.BaseDirectory) ?? ContentRoot.Locate(Environment.CurrentDirectory)!);
        var bots = new[] { ("Story Hugo", ClassId.Knight, "spare"), ("Story Pons", ClassId.Paladin, "spare"), ("Story Ines", ClassId.Healer, "kill") }
            .Select(b => (Net: new ClientConnection(b.Item1, b.Item2), Brain: new BotBrain(b.Item2), Vote: b.Item3)).ToList();
        foreach (var b in bots) b.Net.Connect(host, port);
        var clock = Stopwatch.StartNew();
        bool invited = false, voted = false, yielded = false, final = false;
        var chat = new List<string>();
        var learnAt = new Dictionary<string, double>();
        var gen = new Dictionary<string, int>();
        while (clock.Elapsed.TotalSeconds < 400)
        {
            if (!invited && bots.All(b => b.Net.Welcome is not null))
            {
                foreach (var b in bots.Skip(1)) bots[0].Net.SendChat($"/invite {b.Net.Name}");
                invited = true;
            }
            foreach (var (net, brain, vote) in bots)
            {
                net.Poll();
                if (net.Failed) { Console.WriteLine($"[story] {net.Name} failed: {net.RejectReason}"); return 1; }
                while (net.Chat.TryDequeue(out var line))
                {
                    if (line.Text.Contains("invites you")) net.SendChat("/accept");
                    if (line.Channel == "system") chat.Add($"{net.Name}: {line.Text}");
                }
                while (net.Events.TryDequeue(out var e)) if (e.Type == SimEventType.Yielded) yielded = true;
                while (net.Ui.TryDequeue(out var ui))
                {
                    if (ui.Kind != UiKind.Choice) continue;
                    var c = UiKind.Deserialize<ChoiceView>(ui.Json)!;
                    if (c.Final) final = true;
                    else if (c.MyVote is null) { net.Command("vote", vote); voted = true; }
                }
                if (net.Welcome is not { } w || !net.Entities.TryGetValue(net.YourId, out var me)) continue;
                if (w.IsTown)
                {
                    // Party first, then everyone takes the town waypoint to the garrison.
                    if (bots[0].Net.Party.Count < bots.Count - 1 || w.Waypoint is not { } wp) { net.SendInput(default); continue; }
                    var to = new Vec2(wp[0], wp[1]) - me.Current.Pos;
                    if (to.Length > 60) net.SendInput(new PlayerInput(to.Normalized(), PlayerAction.None, 0, 0, Vec2.Zero));
                    else net.Travel(TravelKind.Waypoint, "royal_garrison");
                    continue;
                }
                if (net.Stats is { } st)
                {
                    var lv = st.Skills.ToDictionary(k => k.SkillId, k => k.Level);
                    if (BotBrain.ChooseLearn(net.Class, id => lv.GetValueOrDefault(id), st.BuildPoints, id => data.LearnCost(net.Class.ToString(), id)) is string learn
                        && learnAt.GetValueOrDefault(net.Name) + 0.5 < clock.Elapsed.TotalSeconds)
                    { net.Learn(learn); learnAt[net.Name] = clock.Elapsed.TotalSeconds; }
                    brain.Bar = st.Bar.Where(data.Spells.ContainsKey).Select(id => data.Spells[id]).ToList();
                }
                if (gen.GetValueOrDefault(net.Name) != net.ZoneGeneration) { gen[net.Name] = net.ZoneGeneration; brain.SetExplore(w.ZoneWidth, w.ZoneHeight); }
                var view = net.Entities.Values.Select(r => new BotView(r.Current.Id, r.Current.Kind, r.Current.Pos, r.Current.Hp, r.Current.MaxHp,
                    r.Current.Dead, r.Current.Kind == EntityKind.Player ? r.Current.PlayerClass : default, r.Current.Flags)).ToList();
                net.SendInput(brain.Decide(net.YourId, view, 0.05f));
            }
            if ((int)clock.Elapsed.TotalSeconds % 30 == 0 && clock.Elapsed.TotalMilliseconds % 30000 < 60)
                foreach (var (net, _, _) in bots)
                    if (net.Entities.TryGetValue(net.YourId, out var dbg))
                        Console.WriteLine($"[story] t={clock.Elapsed.TotalSeconds:F0} {net.Name} in {net.Welcome?.ZoneId} at {dbg.Current.Pos.X:F0},{dbg.Current.Pos.Y:F0} hp {dbg.Current.Hp}/{dbg.Current.MaxHp} " +
                            $"monsters near {net.Entities.Values.Count(r => r.Current.Kind == EntityKind.Monster && !r.Current.Dead)} deaths {net.Stats?.Deaths}");
            if (final && bots.All(b => b.Net.Stats?.QuestList.Any(q => q.Id == "q8_reginalds_choice" && q.Done) == true)) break;
            Thread.Sleep(50);
        }
        for (int i = 0; i < 20; i++) { foreach (var b in bots) b.Net.Poll(); Thread.Sleep(50); }
        foreach (var l in chat.Where(l => l.Contains("Réginald") || l.Contains("Quest"))) Console.WriteLine("[story] " + l);
        var checks = new List<(string, bool)>
        {
            ("Réginald yielded", yielded),
            ("everyone voted", voted),
            ("the vote finished", final),
            ("quest complete for all three", bots.All(b => b.Net.Stats?.QuestList.Any(q => q.Id == "q8_reginalds_choice" && q.Done) == true)),
            ("spare voters keep reginald_spared", bots.Where(b => b.Vote == "spare").All(b => b.Net.Stats?.UnlockList.Contains("reginald_spared") == true)),
            ("the kill voter keeps reginald_killed", bots.Where(b => b.Vote == "kill").All(b => b.Net.Stats?.UnlockList.Contains("reginald_killed") == true)),
        };
        foreach (var (c, ok) in checks) Console.WriteLine($"[story] {(ok ? "ok  " : "FAIL")} {c}");
        bool pass = checks.All(c => c.Item2);
        Console.WriteLine(pass ? "[story] PASS" : "[story] FAIL");
        foreach (var b in bots) b.Net.Dispose();
        return pass ? 0 : 1;
    }
}
