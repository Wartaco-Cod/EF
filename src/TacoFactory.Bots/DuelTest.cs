using System.Diagnostics;
using TacoFactory.Core.Sim;
using TacoFactory.Net;

/// <summary>
/// End-to-end arena check over the network: challenge, accept, fight, win, earn a scroll, read it,
/// then a rematch that must not pay a second scroll within 24 hours.
/// </summary>
public static class DuelTest
{
    public static int Run(string host, int port)
    {
        var a = new ClientConnection("Duelist Hugo", ClassId.Knight);
        var b = new ClientConnection("Duelist Pons", ClassId.Wizard);
        a.Connect(host, port);
        b.Connect(host, port);
        var log = new List<string>();
        var clock = Stopwatch.StartNew();
        int duelsFinished = 0, scrollsAfterFirst = -1;
        bool challenged = false, read = false;
        while (clock.Elapsed.TotalSeconds < 240 && duelsFinished < 2)
        {
            foreach (var c in new[] { a, b })
            {
                c.Poll();
                if (c.Failed) { Console.Error.WriteLine($"[duel] {c.Name} failed: {c.RejectReason}"); return 1; }
                while (c.Chat.TryDequeue(out var line))
                {
                    if (line.Channel != "system") continue;
                    log.Add($"{c.Name}: {line.Text}");
                    if (line.Text.Contains("challenges you")) c.SendChat("/accept");
                    if (c == a && line.Text.Contains("You win")) duelsFinished++;
                    if (c == b && line.Text.Contains("wins the duel") && !line.Text.StartsWith(a.Name)) duelsFinished++;
                }
                while (c.Events.TryDequeue(out _)) { }
            }
            if (a.Welcome is null || b.Welcome is null) { Thread.Sleep(50); continue; }
            if (!challenged && b.Entities.Count > 0) { a.SendChat($"/duel {b.Name}"); challenged = true; }
            if (duelsFinished == 1 && scrollsAfterFirst < 0 && a.Stats is { Scrolls: > 0 } st)
            {
                scrollsAfterFirst = st.Scrolls;
                a.UseScroll("one_handed_sword");
                read = true;
                a.SendChat($"/duel {b.Name}"); // rematch: no second scroll within 24 hours
            }
            // The Knight attacks; the Wizard stands still so the result is certain.
            bool aDueling = a.Entities.TryGetValue(a.YourId, out var ae) && (ae.Current.Flags & EntityFlags.Dueling) != 0;
            a.SendInput(aDueling ? new PlayerInput(default, PlayerAction.UseSkill, 0, b.YourId, default) : default);
            b.SendInput(default);
            Thread.Sleep(50);
        }
        for (int i = 0; i < 20; i++) { a.Poll(); b.Poll(); while (a.Chat.TryDequeue(out var l)) log.Add($"{a.Name}: {l.Text}"); Thread.Sleep(50); }
        foreach (var l in log) Console.WriteLine("[duel] " + l);
        var mastery = a.Stats?.Skills.FirstOrDefault(s => s.SkillId == "one_handed_sword");
        Console.WriteLine($"[duel] duels finished {duelsFinished}, scrolls after first win {scrollsAfterFirst}, scrolls now {a.Stats?.Scrolls}, " +
                          $"sword mastery {mastery?.MasteryLevel} ({mastery?.ScrollLevels} from scrolls)");
        bool ok = duelsFinished == 2 && scrollsAfterFirst == 1 && read && a.Stats?.Scrolls == 0 && mastery?.ScrollLevels == 1;
        Console.WriteLine(ok ? "[duel] PASS" : "[duel] FAIL");
        a.Dispose();
        b.Dispose();
        return ok ? 0 : 1;
    }
}
