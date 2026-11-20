using System.Collections.Concurrent;
using System.Security.Cryptography;
using LiteNetLib;
using LiteNetLib.Utils;
using TacoFactory.Core.Sim;
using TacoFactory.Net;

namespace TacoFactory.Server;

/// <summary>
/// The front door: encrypted login, accounts and purchase keys, character select; plus rate limits,
/// moderation (kick, ban, mute) and the server console.
/// </summary>
public sealed partial class GameServer
{
    private sealed class Pending
    {
        public byte[] Nonce = [];
        public Account? Account;
        public int Attempts;
        public double Since;
    }

    private AccountStore _accounts = null!;
    private KeyStore _keys = null!;
    private ECDiffieHellman _identity = null!;
    private readonly Dictionary<int, Pending> _pending = new();
    private readonly Dictionary<string, (int Fails, double Until)> _lockouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _console = new();

    public const int MaxAuthAttempts = 5, MaxInputsPerSecond = 75, MaxCommandsPerSecond = 15, MaxChatsPerSecond = 3, MaxMessagesPerSecond = 200;
    public const double FrontDoorTimeout = 300;

    public AccountStore Accounts => _accounts;
    public KeyStore Keys => _keys;

    private void InitAccounts()
    {
        _accounts = new AccountStore(_opt.SaveDirectory);
        _keys = new KeyStore(_opt.SaveDirectory);
        _identity = ServerIdentity.LoadOrCreate(_opt.SaveDirectory);
        Log($"server key {AuthCrypto.Fingerprint(_identity.ExportSubjectPublicKeyInfo())}; {_accounts.Count} accounts; " +
            (_opt.OpenRegistration ? "open registration (no purchase key needed)" : $"purchase key needed to register ({_keys.Unused} unused)"));
    }

    // ------------------------------------------------------------------ login and character select

    private void FrontDoor(NetPeer peer, MsgType type, NetDataReader r)
    {
        if (type == MsgType.Hello)
        {
            int version = r.GetInt();
            if (version != Protocol.Version) { Kick(peer, $"This game is version {version}; the server needs {Protocol.Version}. Update the game."); return; }
            var p = new Pending { Nonce = AuthCrypto.Nonce(), Since = Clock };
            _pending[peer.Id] = p;
            Msg.ServerHello(_w, new ServerHello(_identity.ExportSubjectPublicKeyInfo(), p.Nonce, _opt.OpenRegistration, _opt.ServerName));
            peer.Send(_w, DeliveryMethod.ReliableOrdered);
            return;
        }
        if (!_pending.TryGetValue(peer.Id, out var pending)) return;
        switch (type)
        {
            case MsgType.Auth:
            {
                byte[] pub = r.GetBytesWithLength(), nonce = r.GetBytesWithLength(), sealedBytes = r.GetBytesWithLength();
                if (++pending.Attempts > MaxAuthAttempts) { Kick(peer, "Too many login attempts."); return; }
                var req = AuthCrypto.Open(_identity, pending.Nonce, pub, nonce, sealedBytes);
                if (req is null) { Kick(peer, "Login could not be read."); return; }
                Authenticate(peer, pending, req);
                break;
            }
            case MsgType.CreateCharacter:
            {
                string name = r.GetString(32).Trim();
                var cls = (ClassId)r.GetByte();
                if (pending.Account is { } acc) CreateCharacter(peer, acc, name, cls);
                break;
            }
            case MsgType.DeleteCharacter:
            {
                string name = r.GetString(32).Trim();
                r.GetByte();
                if (pending.Account is { } acc) DeleteCharacter(peer, acc, name);
                break;
            }
            case MsgType.EnterWorld:
            {
                string name = r.GetString(32).Trim();
                r.GetByte();
                if (pending.Account is { } acc) EnterWorld(peer, acc, name);
                break;
            }
        }
    }

    private void Authenticate(NetPeer peer, Pending pending, AuthRequest req)
    {
        void Refuse(string why) => SendAccount(peer, new AccountView(false, why, req.Account ?? "", false, [], AccountStore.MaxCharacters));
        string name = req.Account?.Trim() ?? "";
        if (_lockouts.TryGetValue(name, out var lo) && lo.Until > Clock) { Refuse("Too many wrong passwords. Wait a minute and try again."); return; }
        var account = _accounts.Get(name);
        string mode = req.Mode;
        if (mode == AuthMode.Auto)
        {
            if (!_opt.OpenRegistration) { Refuse("Quick play only works on servers with open registration."); return; }
            mode = account is null ? AuthMode.Register : AuthMode.Login;
        }
        if (mode == AuthMode.Register)
        {
            if (account is not null) { Refuse("That account name is taken."); return; }
            if (!AccountStore.IsValidName(name)) { Refuse("Account names are 3-24 letters, digits, '.', '-' or '_'."); return; }
            if ((req.Password ?? "").Length < AccountStore.MinPassword) { Refuse($"Passwords need at least {AccountStore.MinPassword} characters."); return; }
            if (!_opt.OpenRegistration && !_keys.IsUnused(req.Key ?? "")) { Refuse("That purchase key is not valid or has been used."); return; }
            string? key = _opt.OpenRegistration ? null : KeyStore.Normalize(req.Key!);
            account = _accounts.Create(name, req.Password!, key);
            if (key is not null) _keys.Use(key, account.Name);
            Log($"account '{account.Name}' created{(account.Admin ? " (administrator: first account on this server)" : "")}");
        }
        else
        {
            if (account is null || !AccountStore.Verify(account, req.Password ?? ""))
            {
                var f = _lockouts.GetValueOrDefault(name);
                f.Fails++;
                if (f.Fails >= MaxAuthAttempts) { f.Until = Clock + 60; f.Fails = 0; }
                _lockouts[name] = f;
                Refuse("Wrong account name or password.");
                return;
            }
            _lockouts.Remove(name);
        }
        if (account.Banned) { Kick(peer, $"This account is banned{(account.BanReason is { } why ? ": " + why : ".")}"); return; }
        if (_sessions.Values.Any(x => x.Account == account) || _pending.Values.Any(x => x != pending && x.Account == account))
        {
            Refuse("This account is already signed in.");
            return;
        }
        account.LastLoginUtc = DateTime.UtcNow;
        _accounts.Save();
        pending.Account = account;
        SendAccountFor(peer, account, mode == AuthMode.Register ? "Account created. Make your first knight." : "");
    }

    private void SendAccountFor(NetPeer peer, Account account, string message)
    {
        var chars = new List<CharacterSummary>();
        foreach (var c in account.Characters.ToList())
        {
            var rec = _store.Load(c);
            if (rec is null) { account.Characters.Remove(c); continue; }
            chars.Add(new CharacterSummary(rec.Name, rec.Class, rec.Level, rec.LastZone ?? _data.Town.Name));
        }
        SendAccount(peer, new AccountView(true, message, account.Name, account.Admin, chars, AccountStore.MaxCharacters));
    }

    private void SendAccount(NetPeer peer, AccountView view)
    {
        Msg.Account(_w, view);
        peer.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    private void CreateCharacter(NetPeer peer, Account account, string name, ClassId cls)
    {
        string? problem =
            account.Characters.Count >= AccountStore.MaxCharacters ? $"An account holds at most {AccountStore.MaxCharacters} characters." :
            !CharacterStore.IsValidName(name) ? "Names are 3-16 letters, digits, spaces, - or _, starting with a letter." :
            !Enum.IsDefined(cls) ? "Unknown class." : null;
        var existing = _store.Load(name);
        // A character from before accounts existed can be claimed by the account of the same name.
        bool claim = existing is not null && existing.Account is null && string.Equals(ClientConnection.QuickAccount(existing.Name), account.Name, StringComparison.OrdinalIgnoreCase);
        if (problem is null && existing is not null && !claim) problem = "That name is taken.";
        if (problem is null && _accounts.OwnerOf(name) is not null) problem = "That name is taken.";
        if (problem is not null) { SendAccountFor(peer, account, problem); return; }
        var rec = claim ? existing! : new CharacterRecord { Name = name, Class = cls };
        if (!claim && _opt.StartLevel > 1)
        {
            // Testing: new characters begin at this level, with the build points they would have earned.
            rec.Level = _opt.StartLevel;
            rec.BuildPoints = _opt.StartLevel - 1 + _opt.StartLevel / 4;
            rec.Unlocks.AddRange(["prologue_done", "forge"]);
            rec.Waypoints.AddRange(_data.Zones.Values.Where(z => z.Waypoint is not null && z.LevelMin <= _opt.StartLevel).Select(z => z.Id));
        }
        rec.Account = account.Name;
        _store.Save(rec);
        account.Characters.Add(rec.Name);
        _accounts.Save();
        Log($"{account.Name} {(claim ? "claimed" : "created")} {rec.Name} the {rec.Class}");
        SendAccountFor(peer, account, claim ? $"{rec.Name} was already here, and is now yours." : $"{rec.Name} the {cls} is ready.");
    }

    private void DeleteCharacter(NetPeer peer, Account account, string name)
    {
        var owned = account.Characters.FirstOrDefault(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        if (owned is null) { SendAccountFor(peer, account, "No such character."); return; }
        if (_guilds.Of(owned) is { } g)
        {
            g.Members.RemoveAll(m => string.Equals(m, owned, StringComparison.OrdinalIgnoreCase));
            g.Officers.RemoveAll(m => string.Equals(m, owned, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(g.Leader, owned, StringComparison.OrdinalIgnoreCase)) g.Leader = g.Officers.FirstOrDefault() ?? g.Members.FirstOrDefault() ?? "";
            _guilds.Save();
        }
        _store.Delete(owned);
        account.Characters.Remove(owned);
        _accounts.Save();
        Log($"{account.Name} deleted {owned} (kept in saves/deleted)");
        SendAccountFor(peer, account, $"{owned} has been laid to rest.");
    }

    private void EnterWorld(NetPeer peer, Account account, string name)
    {
        var owned = account.Characters.FirstOrDefault(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        if (owned is null || _store.Load(owned) is not { } rec) { SendAccountFor(peer, account, "No such character."); return; }
        if (_sessions.Values.Any(x => string.Equals(x.Name, owned, StringComparison.OrdinalIgnoreCase))) { SendAccountFor(peer, account, "That character is already online."); return; }
        Join(peer, account, rec);
    }

    /// <summary>Peers that connect but never log in are dropped after a while.</summary>
    private void SweepFrontDoor()
    {
        foreach (var (id, p) in _pending.ToList())
            if (Clock - p.Since > FrontDoorTimeout && _net.GetPeerById(id) is { } peer) Kick(peer, "Timed out at the login screen.");
    }

    // ------------------------------------------------------------------ rate limits

    /// <summary>Drops messages beyond each kind's rate; a client flooding far beyond any game need is disconnected.</summary>
    private bool Allow(Session s, MsgType type)
    {
        if (Clock - s.RateWindowStart >= 1)
        {
            s.RateWindowStart = Clock;
            s.InputsThisSecond = s.CommandsThisSecond = s.ChatsThisSecond = s.MessagesThisSecond = 0;
        }
        if (++s.MessagesThisSecond > MaxMessagesPerSecond) { Kick(s.Peer, "Too many messages."); return false; }
        switch (type)
        {
            case MsgType.Input: return ++s.InputsThisSecond <= MaxInputsPerSecond;
            case MsgType.ChatSend: return ++s.CommandsThisSecond <= MaxCommandsPerSecond; // talk itself is limited in ChatAllowed
            default: return ++s.CommandsThisSecond <= MaxCommandsPerSecond;
        }
    }

    /// <summary>Spoken lines (not slash commands) are limited to a few a second.</summary>
    private bool ChatAllowed(Session s)
    {
        if (++s.ChatsThisSecond <= MaxChatsPerSecond) return true;
        if (Clock - s.LastChatWarning > 5) { s.LastChatWarning = Clock; Tell(s, "Slow down: chat is limited to a few lines a second."); }
        return false;
    }

    private bool Muted(Session s)
    {
        if (s.Account.MutedUntilUtc is not { } until || until <= DateTime.UtcNow) return false;
        Tell(s, $"You are muted for another {(int)System.Math.Ceiling((until - DateTime.UtcNow).TotalMinutes)} minutes.");
        return true;
    }

    // ------------------------------------------------------------------ moderation

    /// <summary>Admin slash commands in chat. Returns false if the text was not one.</summary>
    private bool AdminCommand(Session s, string cmd, string arg)
    {
        if (cmd is not ("/kick" or "/ban" or "/unban" or "/mute" or "/unmute" or "/announce" or "/who" or "/keys")) return false;
        if (!s.Account.Admin) { Tell(s, "Only server administrators can do that."); return true; }
        foreach (var line in Moderate(cmd.TrimStart('/'), arg, s.Name)) Tell(s, line);
        return true;
    }

    /// <summary>Shared by admin chat commands and the server console. Returns lines to show.</summary>
    public IEnumerable<string> Moderate(string verb, string arg, string by)
    {
        // Character names can have spaces: take the longest run of leading words that names someone.
        var words = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int take = 1;
        for (int k = words.Length; k >= 1; k--)
        {
            string candidate = string.Join(' ', words.Take(k));
            if (_sessions.Values.Any(x => string.Equals(x.Name, candidate, StringComparison.OrdinalIgnoreCase))
                || _accounts.Get(candidate) is not null || _accounts.OwnerOf(candidate) is not null) { take = k; break; }
        }
        string target = string.Join(' ', words.Take(take)), rest = string.Join(' ', words.Skip(take));
        Session? online = _sessions.Values.FirstOrDefault(x => string.Equals(x.Name, target, StringComparison.OrdinalIgnoreCase)
                                                            || string.Equals(x.Account.Name, target, StringComparison.OrdinalIgnoreCase));
        Account? account = online?.Account ?? _accounts.Get(target) ?? _accounts.OwnerOf(target);
        switch (verb)
        {
            case "who":
                yield return $"{_sessions.Count} online: " + string.Join(", ", _sessions.Values.Select(x => $"{x.Name} ({x.Account.Name}, L{x.Record.Level}, {x.Instance?.Zone.Name})"));
                break;
            case "announce":
                foreach (var x in _sessions.Values) Tell(x, $"{{#F2D25A}}[Server]{{/}} {arg}");
                yield return "Announced.";
                break;
            case "kick":
                if (online is null) { yield return $"{target} is not online."; break; }
                Kick(online.Peer, rest.Length > 0 ? rest : $"Removed by {by}.");
                yield return $"Kicked {online.Name}.";
                break;
            case "ban":
                if (account is null) { yield return $"No account or character called {target}."; break; }
                account.Banned = true;
                account.BanReason = rest.Length > 0 ? rest : null;
                _accounts.Save();
                if (online is not null) Kick(online.Peer, "This account is banned.");
                Log($"{by} banned {account.Name}{(rest.Length > 0 ? ": " + rest : "")}");
                yield return $"Banned account {account.Name}.";
                break;
            case "unban":
                if (account is null) { yield return $"No account or character called {target}."; break; }
                account.Banned = false;
                account.BanReason = null;
                _accounts.Save();
                yield return $"Unbanned {account.Name}.";
                break;
            case "mute":
                if (account is null) { yield return $"No account or character called {target}."; break; }
                int minutes = int.TryParse(rest, out var m) ? System.Math.Clamp(m, 1, 60 * 24 * 30) : 30;
                account.MutedUntilUtc = DateTime.UtcNow.AddMinutes(minutes);
                _accounts.Save();
                if (online is not null) Tell(online, $"You have been muted for {minutes} minutes.");
                yield return $"Muted {account.Name} for {minutes} minutes.";
                break;
            case "unmute":
                if (account is null) { yield return $"No account or character called {target}."; break; }
                account.MutedUntilUtc = null;
                _accounts.Save();
                yield return $"Unmuted {account.Name}.";
                break;
            case "admin":
                if (account is null) { yield return $"No account called {target}."; break; }
                account.Admin = rest != "off";
                _accounts.Save();
                yield return $"{account.Name} is {(account.Admin ? "now" : "no longer")} an administrator.";
                break;
            case "keys":
                int n = int.TryParse(target, out var k) ? System.Math.Clamp(k, 1, 500) : 1;
                foreach (var key in _keys.Make(n)) yield return key;
                yield return $"{n} new purchase key(s) saved to keys.json; {_keys.Unused} unused in total.";
                break;
            case "save":
                foreach (var x in _sessions.Values) if (x.Entity is { } p) _store.Save(x.Record = Snapshot(x, p));
                yield return $"Saved {_sessions.Count} characters.";
                break;
            case "shutdown":
                foreach (var x in _sessions.Values) Tell(x, "{#E0463C}The server is shutting down. Your character is saved.{/}");
                ShouldExit = true;
                yield return "Shutting down.";
                break;
            default:
                yield return "Commands: who, announce <text>, kick <name> [reason], ban <name> [reason], unban <name>, " +
                             "mute <name> [minutes], unmute <name>, admin <account> [off], keys <count>, save, shutdown";
                break;
        }
    }

    /// <summary>Lines typed into the server window are moderation commands, run on the game thread.</summary>
    public void StartConsole()
    {
        if (Console.IsInputRedirected) return;
        var t = new Thread(() =>
        {
            while (Console.ReadLine() is { } line) if (line.Trim().Length > 0) _console.Enqueue(line.Trim());
        }) { IsBackground = true, Name = "console" };
        t.Start();
        Log("type 'help' here for server commands");
    }

    private void RunConsole()
    {
        while (_console.TryDequeue(out var line))
        {
            var parts = line.Split(' ', 2);
            foreach (var reply in Moderate(parts[0].ToLowerInvariant().TrimStart('/'), parts.ElementAtOrDefault(1) ?? "", "the console"))
                Console.WriteLine("  " + reply);
        }
    }
}
