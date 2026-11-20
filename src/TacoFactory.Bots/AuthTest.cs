using System.Diagnostics;
using System.Text.Json;
using TacoFactory.Core.Sim;
using TacoFactory.Net;

/// <summary>
/// End-to-end check of accounts against a server that needs purchase keys (start it without --open, after
/// --make-keys 2): registering, keys, wrong passwords, character create/delete, entering the world, one login
/// per account, and admin mute, kick and ban.
/// </summary>
public static class AuthTest
{
    private static readonly List<(string, bool)> Checks = [];

    public static int Run(string host, int port, string saves)
    {
        var keys = JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(Path.Combine(saves, "keys.json")))!
            .Where(k => k.GetProperty("UsedBy").ValueKind == JsonValueKind.Null).Select(k => k.GetProperty("Key").GetString()!).ToList();
        if (keys.Count < 2) { Console.WriteLine("[auth] need 2 unused keys: run the server with --make-keys 2 first"); return 1; }

        // Account 1: no key, then a good key; becomes the administrator.
        var a = Open(host, port);
        a.Register("grandmaster", "correct horse", "");
        Check("registering without a key is refused", Wait(a, () => a.Account is { Ok: false }) && a.AccountMessage!.Contains("purchase key"));
        a.Register("grandmaster", "short", keys[0]);
        Check("short passwords are refused", Wait(a, () => a.AccountMessage?.Contains("at least") == true));
        a.Register("grandmaster", "correct horse", keys[0]);
        Check("register with a key works and the first account is admin", Wait(a, () => a.Account is { Ok: true }) && a.Account?.Admin == true);
        a.CreateCharacter("x", ClassId.Knight);
        Check("bad character names are refused", Wait(a, () => a.AccountMessage?.Contains("Names are") == true));
        a.CreateCharacter("Sir Amaury", ClassId.Paladin);
        Check("character created", Wait(a, () => a.Account?.Characters.Any(c => c.Name == "Sir Amaury" && c.Class == ClassId.Paladin) == true));
        a.CreateCharacter("Spare Brother", ClassId.Healer);
        Wait(a, () => a.Account?.Characters.Count == 2);
        a.DeleteCharacter("Spare Brother");
        Check("character deleted", Wait(a, () => a.Account?.Characters.Count == 1));

        // Account 2: key reuse refused, wrong password locked out, name clash refused.
        var b = Open(host, port);
        b.Register("squire", "squire password", keys[0]);
        Check("a used key is refused", Wait(b, () => b.AccountMessage?.Contains("not valid") == true));
        b.Register("squire", "squire password", keys[1]);
        Wait(b, () => b.Account is { Ok: true });
        b.CreateCharacter("Sir Amaury", ClassId.Knight);
        Check("names are unique across accounts", Wait(b, () => b.AccountMessage?.Contains("taken") == true));
        b.CreateCharacter("Squire Pons", ClassId.Knight);
        Wait(b, () => b.Account?.Characters.Count == 1);
        b.Dispose();

        var wrong = Open(host, port);
        wrong.Login("squire", "not the password");
        Check("wrong password refused", Wait(wrong, () => wrong.AccountMessage?.Contains("Wrong") == true));
        wrong.Dispose();

        // Both enter the world.
        a.EnterWorld("Sir Amaury");
        var b2 = Open(host, port);
        b2.Login("squire", "squire password");
        Wait(b2, () => b2.Account is { Ok: true });
        b2.EnterWorld("Squire Pons");
        Check("both characters entered the world", Wait(a, () => a.InWorld && b2.InWorld, also: b2));
        var dup = Open(host, port);
        dup.Login("squire", "squire password");
        Check("one login per account", Wait(dup, () => dup.AccountMessage?.Contains("already signed in") == true));
        dup.Dispose();

        // Moderation by the administrator.
        a.SendChat("/mute squire 5");
        Wait(a, () => false, 1, b2);
        b2.SendChat("hello");
        Check("muted players cannot talk", Wait(b2, () => Said(b2, "muted"), also: a));
        a.SendChat("/kick Squire Pons misbehaving");
        Check("kick disconnects", Wait(b2, () => b2.Failed, also: a) && b2.RejectReason!.Contains("misbehaving"));
        a.SendChat("/ban squire cheating");
        Wait(a, () => false, 1);
        var banned = Open(host, port);
        banned.Login("squire", "squire password");
        Check("banned accounts cannot log in", Wait(banned, () => banned.Failed) && banned.RejectReason!.Contains("banned"));
        banned.Dispose();
        b2.Dispose();
        a.Dispose();

        foreach (var (c, ok) in Checks) Console.WriteLine($"[auth] {(ok ? "ok  " : "FAIL")} {c}");
        bool pass = Checks.All(c => c.Item2);
        Console.WriteLine(pass ? $"[auth] PASS ({Checks.Count} checks)" : "[auth] FAIL");
        return pass ? 0 : 1;
    }

    private static readonly Dictionary<ClientConnection, List<string>> Lines = new();

    private static bool Said(ClientConnection c, string text) => Lines.TryGetValue(c, out var l) && l.Any(x => x.Contains(text));

    private static ClientConnection Open(string host, int port)
    {
        var c = new ClientConnection();
        c.Connect(host, port);
        Wait(c, () => c.Hello is not null);
        return c;
    }

    private static void Check(string what, bool ok) => Checks.Add((what, ok));

    private static bool Wait(ClientConnection c, Func<bool> until, double seconds = 5, ClientConnection? also = null)
    {
        var sw = Stopwatch.StartNew();
        string? before = c.AccountMessage;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            foreach (var x in also is null ? [c] : new[] { c, also })
            {
                x.Poll();
                if (!Lines.TryGetValue(x, out var l)) Lines[x] = l = [];
                while (x.Chat.TryDequeue(out var line)) l.Add(line.Text);
                while (x.Events.TryDequeue(out _)) { }
                while (x.Ui.TryDequeue(out _)) { }
                if (x.InWorld) x.SendInput(default);
            }
            if (until()) return true;
            Thread.Sleep(20);
        }
        _ = before;
        return until();
    }
}
