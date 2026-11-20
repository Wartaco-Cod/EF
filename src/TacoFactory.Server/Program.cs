using System.Diagnostics;
using TacoFactory.Core;
using TacoFactory.Core.Content;
using TacoFactory.Core.Data;
using TacoFactory.Core.Time;
using TacoFactory.Net;
using TacoFactory.Server;

// Taco Factory dedicated server. Runs on your own server machine; players connect over UDP.
int port = Protocol.DefaultPort, seed = Environment.TickCount;
string saves = "saves";
string? startZone = null, exitOnUnique = null;
bool exitOnClear = false, open = false;
string serverName = "Templar Knights";
int? makeKeys = null;
int startLevel = 1;
bool demoKit = false;
int mistMinutes = 60, bossMinutes = 30;
string? mistZone = null;
string? updates = null;
int updatePort = Protocol.DefaultPort + 1;
int? stopAfter = null;
for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--port": port = int.Parse(Next()); break;
        case "--seed": seed = int.Parse(Next()); break;
        case "--saves": saves = Next(); break;
        case "--start-zone": startZone = Next(); break;          // testing: new arrivals skip the town
        case "--exit-on-clear": exitOnClear = true; break;       // testing: stop when any zone copy is cleared
        case "--exit-on-unique": exitOnUnique = Next(); break;   // testing: stop when this superunique dies
        case "--ticks": stopAfter = int.Parse(Next()); break;
        case "--open": open = true; break;
        case "--start-level": startLevel = int.Parse(Next()); break; // testing: new characters begin at this level
        case "--updates": updates = Next(); break;                 // serve game updates to launchers from this folder
        case "--update-port": updatePort = int.Parse(Next()); break; // TCP port for updates (default game port + 1)                         // anyone may make an account (playtests); otherwise a purchase key is needed
        case "--demo-kit": demoKit = true; break;
        case "--mist-minutes": mistMinutes = int.Parse(Next()); break;      // how long the Mist holds each zone (default 60)
        case "--world-boss-minutes": bossMinutes = int.Parse(Next()); break; // how often La Tarasque rises (default 30)
        case "--mist-zone": mistZone = Next(); break;                       // testing: the Mist holds this zone                  // demo: every character gets one of each base item in the strongbox
        case "--name": serverName = Next(); break;                 // shown on the login screen
        case "--make-keys": makeKeys = int.Parse(Next()); break;   // print new purchase keys and exit
        default: Console.Error.WriteLine($"Unknown option {args[i]}"); return 2;
    }
}

string assets = ContentRoot.Locate(AppContext.BaseDirectory) ?? ContentRoot.Locate(Environment.CurrentDirectory)
                ?? throw new DirectoryNotFoundException("No 'assets' folder found above the server.");
var data = GameData.Load(assets);
if (startZone is not null && !data.Zones.ContainsKey(startZone))
{
    Console.Error.WriteLine($"Unknown zone '{startZone}'. Known: {string.Join(", ", data.Zones.Keys)}");
    return 2;
}

Console.WriteLine($"[server] {EngineInfo.Name} {EngineInfo.Version} — {EngineInfo.TickRate} Hz, protocol v{Protocol.Version}");
Directory.CreateDirectory(saves);
if (makeKeys is int count)
{
    var store = new KeyStore(saves);
    foreach (var key in store.Make(count)) Console.WriteLine(key);
    Console.WriteLine($"[server] {count} purchase key(s) added to {Path.Combine(saves, "keys.json")}; {store.Unused} unused in total");
    return 0;
}
using var server = new GameServer(new ServerOptions(port, seed, exitOnClear, saves, stopAfter, exitOnUnique, startZone, open, serverName, startLevel, demoKit, mistMinutes, bossMinutes, mistZone), data);
server.Start();
server.StartConsole();
UpdateHost? updateHost = null;
if (updates is not null)
{
    updateHost = new UpdateHost(updates, updatePort);
    updateHost.Start();
    Console.WriteLine($"[server] serving updates from {Path.GetFullPath(updates)} on TCP {updatePort}: version {updateHost.Manifest.Version}, {updateHost.Manifest.Files.Count} files");
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var step = new FixedStep(EngineInfo.TickRate);
var clock = Stopwatch.StartNew();
double last = 0;
while (!cts.IsCancellationRequested && !server.ShouldExit)
{
    double now = clock.Elapsed.TotalSeconds;
    int steps = step.Advance(now - last);
    last = now;
    for (int s = 0; s < steps && !server.ShouldExit; s++) server.Tick((float)step.StepSeconds);
    double untilNext = step.StepSeconds * (1 - step.Alpha);
    Thread.Sleep(TimeSpan.FromSeconds(Math.Max(0.001, untilNext * 0.9)));
}
updateHost?.Dispose();
Console.WriteLine($"[server] stopped after {step.TotalSteps} ticks; characters saved to '{saves}'");
return 0;
