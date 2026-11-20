using System.Diagnostics;
using TacoFactory.Core.Content;
using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;
using TacoFactory.Net;

// Headless playtest bots. Each bot is a full network client that plays like a person would:
// it only sends inputs and chat commands, and only knows what the server shows it.
// They meet in town, form one party, walk out of town together and clear their copy of the zone.
string host = "127.0.0.1", prefix = "Bot", targetZone = "bloodied_fields";
int port = Protocol.DefaultPort, count = 6, timeoutSeconds = 1200;
// run_bots.bat puts "--count 6" first; a mode flag anywhere takes over from there.
string[] modes = ["--campaign", "--simulate", "--duel-test", "--economy-seed", "--auth-test", "--story-test", "--economy-test"];
int modeAt = Array.FindIndex(args, a => modes.Contains(a));
if (modeAt > 0) args = args[modeAt..];
if (args.Length > 0 && args[0] == "--campaign") return CampaignRun.Run(args.Length > 1 ? int.Parse(args[1]) : 4, args.Length > 2 ? args[2] : null);
if (args.Length > 0 && args[0] == "--simulate") return BalanceRun.Run(args.Length > 1 ? args[1] : "all", args.Length > 2 ? int.Parse(args[2]) : 6);
if (args.Length > 0 && args[0] == "--duel-test") return DuelTest.Run(args.Length > 1 ? args[1] : "127.0.0.1", Protocol.DefaultPort);
if (args.Length > 0 && args[0] == "--economy-seed") { EconomyTest.Seed(args[1]); if (args.Contains("--market")) EconomyTest.SeedMarket(args[1]); return 0; }
if (args.Length > 0 && args[0] == "--auth-test") return AuthTest.Run("127.0.0.1", Protocol.DefaultPort, args[1]);
if (args.Length > 0 && args[0] == "--story-test") return StoryTest.Run("127.0.0.1", Protocol.DefaultPort);
if (args.Length > 0 && args[0] == "--economy-test") return EconomyTest.Run(args.Length > 1 ? args[1] : "127.0.0.1", Protocol.DefaultPort);
for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--host": host = Next(); break;
        case "--port": port = int.Parse(Next()); break;
        case "--count": count = Math.Clamp(int.Parse(Next()), 1, 6); break;
        case "--timeout": timeoutSeconds = int.Parse(Next()); break;
        case "--prefix": prefix = Next(); break;
        case "--zone": targetZone = Next(); break;
        default: Console.Error.WriteLine($"Unknown option {args[i]}"); return 2;
    }
}

var data = GameData.Load(ContentRoot.Locate(AppContext.BaseDirectory) ?? ContentRoot.Locate(Environment.CurrentDirectory)
    ?? throw new DirectoryNotFoundException("No assets folder found."));
string[] names = ["Gaspard", "Hugues", "Bertrand", "Odile", "Renaud", "Aymeric"];
var bots = new List<(ClientConnection Net, BotBrain Brain)>();
for (int i = 0; i < count; i++)
{
    var cls = Classes.All[i % 4];
    var c = new ClientConnection($"{prefix} {names[i]}", cls);
    c.Connect(host, port);
    bots.Add((c, new BotBrain(cls)));
}
var leader = bots[0].Net;
Console.WriteLine($"[bots] {count} bots connecting to {host}:{port}: {string.Join(", ", bots.Select(b => $"{b.Net.Name} ({b.Net.Class})"))}");

var clock = Stopwatch.StartNew();
double cleared = -1, partyFormedAt = -1, invitesSentAt = -1;
long damageEvents = 0, chatLines = 0;
var view = new List<BotView>(256);
var lastLearn = new Dictionary<string, double>();
var generation = new Dictionary<string, int>();
var questLines = new List<string>();
const float dt = 0.05f;
while (clock.Elapsed.TotalSeconds < timeoutSeconds)
{
    double now = clock.Elapsed.TotalSeconds;
    bool allWelcomed = bots.All(b => b.Net.Welcome is { IsTown: true });
    if (allWelcomed && invitesSentAt < 0 && count > 1)
    {
        foreach (var (net, _) in bots.Skip(1)) leader.SendChat($"/invite {net.Name}");
        invitesSentAt = now;
    }
    if (partyFormedAt < 0 && (count == 1 || leader.Party.Count == count - 1)) partyFormedAt = now;

    foreach (var (net, brain) in bots)
    {
        net.Poll();
        if (net.Failed && cleared < 0 && !net.Events.Any(e => e.Type == SimEventType.ZoneCleared))
        { Console.Error.WriteLine($"[bots] {net.Name} failed: {net.RejectReason}"); return 1; }
        if (net.Welcome is not { } w) continue;
        while (net.Chat.TryDequeue(out var line))
        {
            chatLines++;
            if (line.Channel == "system" && line.Text.Contains("invites you")) net.SendChat("/accept");
            if (line.Text.Contains("Quest complete") && net == leader) questLines.Add(line.Text);
        }
        while (net.Events.TryDequeue(out var e))
        {
            if (e.Type == SimEventType.Damage) damageEvents++;
            if (e.Type == SimEventType.ZoneCleared && w.ZoneId == targetZone && cleared < 0) cleared = now;
        }
        if (generation.GetValueOrDefault(net.Name) != net.ZoneGeneration)
        {
            generation[net.Name] = net.ZoneGeneration;
            brain.SetExplore(w.ZoneWidth, w.ZoneHeight);
        }
        if (net.Stats is { } st)
        {
            var lv = st.Skills.ToDictionary(k => k.SkillId, k => k.Level);
            if (BotBrain.ChooseLearn(net.Class, id => lv.GetValueOrDefault(id), st.BuildPoints, id => data.LearnCost(net.Class.ToString(), id)) is string learn
                && lastLearn.GetValueOrDefault(net.Name) + 1.0 < now)
            {
                net.Learn(learn);
                lastLearn[net.Name] = now;
            }
            brain.Bar = st.Bar.Where(data.Spells.ContainsKey).Select(id => data.Spells[id]).ToList();
        }
        if (!net.Entities.TryGetValue(net.YourId, out var me)) continue;

        if (data.Zones.TryGetValue(w.ZoneId, out var zd) && zd.Prologue
            && !net.Entities.Values.Any(r => r.Current.Kind == EntityKind.Monster && !r.Current.Dead))
        {
            // The prologue courtyard: once the soldiers are down, out through the crypt stair.
            var stair = w.ExitList[0];
            net.SendInput(new PlayerInput((new Vec2(stair.X, stair.Y) - me.Current.Pos).Normalized(), PlayerAction.None, 0, 0, Vec2.Zero));
            continue;
        }
        if (w.IsTown)
        {
            // Wait for the party, then walk to the exit towards the target zone.
            var exit = w.ExitList.FirstOrDefault(x => x.To == targetZone);
            if (partyFormedAt < 0 || exit is null) { net.SendInput(default); continue; }
            var to = new Vec2(exit.X, exit.Y) - me.Current.Pos;
            net.SendInput(new PlayerInput(to.Normalized(), PlayerAction.None, 0, 0, Vec2.Zero));
            continue;
        }

        view.Clear();
        foreach (var r in net.Entities.Values)
        {
            var s = r.Current;
            view.Add(new BotView(s.Id, s.Kind, s.Pos, s.Hp, s.MaxHp, s.Dead, s.Kind == EntityKind.Player ? s.PlayerClass : default, s.Flags));
        }
        net.SendInput(brain.Decide(net.YourId, view, dt));
    }
    if (cleared >= 0) break;
    Thread.Sleep(50);
}

for (int i = 0; i < 10; i++) { foreach (var b in bots) b.Net.Poll(); Thread.Sleep(50); }

Console.WriteLine();
Console.WriteLine(partyFormedAt >= 0 ? $"[bots] party of {count} formed after {partyFormedAt:F1} s" : "[bots] party never formed");
Console.WriteLine(cleared >= 0 ? $"[bots] ZONE CLEARED in {cleared:F0} s of real time" : $"[bots] timed out after {timeoutSeconds} s");
Console.WriteLine($"[bots] {damageEvents / Math.Max(1, count)} damage events and {chatLines / (double)count:F1} chat lines received per bot");
foreach (var q in questLines) Console.WriteLine($"[bots] {q}");
foreach (var (net, _) in bots)
{
    var s = net.Stats;
    string skills = s is null ? "" : string.Join(", ", s.Skills.Where(m => m.Level > 0 || m.MasteryLevel > 0).Select(m => $"{m.SkillId} {m.Level}/M{m.MasteryLevel}"));
    Console.WriteLine($"  {net.Name,-14} {net.Class,-8} level {s?.Level,3}  BP {s?.BuildPoints,2}  kills {s?.Kills,3}  deaths {s?.Deaths,2}  zone {net.Welcome?.ZoneName}  skills: {skills}");
}
foreach (var b in bots) b.Net.Dispose();
return cleared >= 0 ? 0 : 1;
