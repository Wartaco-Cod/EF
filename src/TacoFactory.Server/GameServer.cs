using System.Text.Json;
using LiteNetLib;
using LiteNetLib.Utils;
using TacoFactory.Core;
using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;
using TacoFactory.Net;

namespace TacoFactory.Server;

public sealed record ServerOptions(int Port, int Seed, bool ExitOnClear, string SaveDirectory, int? StopAfterTicks,
    string? ExitOnUnique = null, string? StartZone = null, bool OpenRegistration = false, string ServerName = "Templar Knights",
    int StartLevel = 1, bool DemoKit = false, int MistMinutes = 60, int WorldBossMinutes = 30, string? MistZone = null);

/// <summary>
/// Authoritative game server. Everyone shares the town; each party gets its own copy of every field and dungeon.
/// Clients send inputs and requests; the server simulates and sends each player what is near them.
/// </summary>
public sealed partial class GameServer : IDisposable
{
    public const int MaxPlayers = 120;
    public const float ExitRadius = 90, WaypointRadius = 160, NpcRadius = 170;
    public const double EmptyInstanceLifetime = 120, TownCooldown = 20, InviteLifetime = 60;

    private readonly ServerOptions _opt;
    private readonly GameData _data;
    private readonly CharacterStore _store;
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly NetDataWriter _w = new();
    private readonly Dictionary<int, Session> _sessions = new(); // by peer id
    private readonly Dictionary<string, Instance> _instances = new();
    private readonly DuelRewardTracker _duelRewards = new();
    private readonly AuctionHouse _auctions;
    private readonly GuildRegistry _guilds;
    private readonly Dictionary<Session, Trade> _trades = new();
    private readonly string _duelFile;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private int _seed;
    private long _ticks;

    public bool ShouldExit { get; private set; }
    private double Clock => _clock.Elapsed.TotalSeconds;
    public IReadOnlyDictionary<string, Instance> Instances => _instances;

    public GameServer(ServerOptions opt, GameData data)
    {
        _opt = opt;
        _data = data;
        _seed = opt.Seed;
        _store = new CharacterStore(opt.SaveDirectory);
        _duelFile = Path.Combine(opt.SaveDirectory, "duel_rewards.json");
        LoadDuelRewards();
        _auctions = new AuctionHouse(opt.SaveDirectory);
        _guilds = new GuildRegistry(opt.SaveDirectory);
        InitAccounts();
        _net = new NetManager(_listener) { AutoRecycle = true, DisconnectTimeout = 10000 };
        _listener.ConnectionRequestEvent += req =>
        {
            if (_sessions.Count >= MaxPlayers) { req.Reject(); return; }
            req.AcceptIfKey(Protocol.ConnectKey);
        };
        _listener.PeerDisconnectedEvent += (peer, info) => { _pending.Remove(peer.Id); Leave(peer, info.Reason.ToString()); };
        _listener.NetworkReceiveEvent += (peer, reader, _, _) => Receive(peer, reader);
        GetInstance(_data.Town.Id, null);
    }

    public void Start()
    {
        if (!_net.Start(_opt.Port)) throw new InvalidOperationException($"Could not listen on UDP port {_opt.Port}.");
        Log($"listening on UDP {_opt.Port}; town '{_data.Town.Name}', {_data.Zones.Count - 1} zones, parties up to {World.MaxGroupSize}");
    }

    // ------------------------------------------------------------------ instances

    private Instance GetInstance(string zoneId, Party? party, string? guild = null, string? owner = null)
    {
        var zone = _data.Zones[zoneId];
        string key = zone.IsGuildHall ? $"{zone.Id}#{guild}" : zone.IsPersonal ? $"{zone.Id}#{owner?.ToLowerInvariant()}" : zone.IsTown ? zone.Id : $"{zone.Id}#{party!.Id}";
        if (_instances.TryGetValue(key, out var inst)) return inst;
        inst = new Instance { Key = key, World = new World(_data, zone.Id, ++_seed) };
        _instances[key] = inst;
        PrepareInstance(inst, party);
        if (zone.IsGuildHall) Log($"opened the hall of {guild}");
        else if (!zone.IsTown) Log($"new instance {key} for {party!.Leader.Name}'s party of {party.Members.Count}");
        return inst;
    }

    /// <summary>Moves a player into an instance, carrying their character over. Arrival defaults to the zone entrance.</summary>
    private void Enter(Session s, Instance target, Vec2? at = null)
    {
        CancelTrade(s, "You left the area.");
        if (s.Instance is { } old)
        {
            if (s.Entity is { } e) s.Record = Snapshot(s, e);
            old.World.RemovePlayer(s.EntityId);
            old.Sessions.Remove(s);
            if (old.Sessions.Count == 0) old.EmptySince = Clock;
            SendRoster(old);
        }
        // Monsters are scaled to the party members online when a zone copy first fills.
        var p = target.World.AddPlayer(s.Name, s.Class, s.Record.Level, at);
        s.Record.ApplyTo(p);
        ArriveExtras(s, p);
        if (_opt.StartLevel > 1 && p.Equipped.Values.All(i => i.ItemLevel < _opt.StartLevel / 2))
        {
            // Testing with --start-level: gear fit for that level, as a player would have found by then.
            var weapon = target.World.WeaponOf(p);
            foreach (var slot in Enum.GetValues<ItemSlot>().Where(sl => sl != ItemSlot.Pack))
            {
                if (slot == ItemSlot.Shield && !p.Equipped.ContainsKey(ItemSlot.Shield)) continue;
                p.Equipped[slot] = Loot.MakeItem(Random.Shared, Loot.NewId(), _opt.StartLevel, Rarity.Rare, slot, slot == ItemSlot.Weapon ? weapon : null) with { Slot = slot };
            }
        }
        target.World.RefreshPlayer(p);
        if (!target.World.Populated && !target.Zone.IsTown)
        {
            int partyOnline = s.Party.Members.Count(m => _sessions.ContainsValue(m));
            target.World.Populate(groupSize: partyOnline, allyBonus: AllyBonus(s, target.Zone));
        }
        s.Instance = target;
        s.EntityId = p.Id;
        s.ArrivedAt = Clock;
        s.Visit++;
        target.Sessions.Add(s);

        var z = target.Zone;
        Msg.Welcome(_w, new WelcomeInfo(p.Id, z.Id, z.Name, z.Width, z.Height, EngineInfo.TickRate, z.GroundTile,
            z.IsTown, z.Exits.Select(x => new ExitInfo(x.To, _data.Zones[x.To].Name, x.X, x.Y)).ToList(), z.Waypoint,
            z.Arena is { } a ? [a.X, a.Y, a.Width, a.Height] : null, z.Arena?.Name ?? "",
            z.Npcs.Select(n => n.Name).ToList(), z.Act, z.LevelMin, z.LevelMax, s.Visit));
        s.Peer.Send(_w, DeliveryMethod.ReliableOrdered);
        SendRoster(target);
        foreach (var q in _data.Quests.Where(q => q.Objective.Type == ObjectiveType.EnterZone && q.Objective.Target == z.Id)) Complete(s, q);
        if (z.Waypoint is not null && s.Waypoints.Add(z.Id)) Tell(s, $"Waypoint found: {z.Name}.");
        StoryOnEnter(s, p, z);
        EndgameOnEnter(s, p, z);
    }

    /// <summary>Copies a live player back into their saved record (progress plus server-side state).</summary>
    private CharacterRecord Snapshot(Session s, Entity e)
    {
        var rec = CharacterRecord.From(e);
        rec.QuestsDone = s.QuestsDone.ToList();
        rec.Waypoints = s.Waypoints.ToList();
        rec.Unlocks = s.Unlocks.ToList();
        rec.Scrolls = s.Scrolls;
        rec.Stash = s.Stash.ToList();
        rec.Guild = s.Guild;
        rec.PendingGold = 0;
        rec.Account = s.Record.Account;
        rec.LastZone = s.Instance?.Zone.Name ?? s.Record.LastZone;
        return rec;
    }

    private Vec2 ArrivalFrom(ZoneDef dest, string fromZone)
    {
        var back = dest.Exits.FirstOrDefault(x => x.To == fromZone);
        if (back is null) return new Vec2(dest.PlayerSpawn[0], dest.PlayerSpawn[1]);
        var center = new Vec2(dest.Width / 2, dest.Height / 2);
        var pos = new Vec2(back.X, back.Y);
        return pos + (center - pos).Normalized() * 230;
    }

    // ------------------------------------------------------------------ messages

    private void Receive(NetPeer peer, NetDataReader r)
    {
        if (r.AvailableBytes < 1) return;
        var type = (MsgType)r.GetByte();
        try
        {
            if (!_sessions.TryGetValue(peer.Id, out var s) || s.Instance is null)
            {
                FrontDoor(peer, type, r); // login and character select
                return;
            }
            if (!Allow(s, type)) return;
            switch (type)
            {
                case MsgType.Input:
                    s.Instance.World.SetInput(s.EntityId, Msg.ReadInput(r, out _));
                    break;
                case MsgType.ChatSend: Chat(s, r.GetString(16), r.GetString(Msg.MaxChatLength)); break;
                case MsgType.LearnSkill: Learn(s, r.GetString(32)); break;
                case MsgType.Travel: Travel(s, (TravelKind)r.GetByte(), r.GetString(64)); break;
                case MsgType.Interact: Interact(s, r.GetInt()); break;
                case MsgType.UseScroll: UseScroll(s, r.GetString(32)); break;
                case MsgType.Command:
                    var (verb, args) = Msg.ReadCommand(r);
                    HandleCommand(s, verb, args);
                    break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException or KeyNotFoundException)
        {
            Log($"bad message from peer {peer.Id}: {ex.Message}");
            Kick(peer, "Bad message");
        }
    }

    /// <summary>Puts a logged-in account's character into the world.</summary>
    private void Join(NetPeer peer, Account account, CharacterRecord record)
    {
        string name = record.Name;
        var cls = record.Class;
        var s = new Session { Peer = peer, Name = name, Class = cls, Record = record, Account = account };
        s.Party = new Party(s);
        foreach (var x in s.Record.QuestsDone) s.QuestsDone.Add(x);
        foreach (var x in s.Record.Waypoints) s.Waypoints.Add(x);
        foreach (var x in s.Record.Unlocks) s.Unlocks.Add(x);
        s.Scrolls = s.Record.Scrolls;
        s.Stash = s.Record.Stash.ToList();
        s.Guild = _guilds.Of(name)?.Name;
        s.Waypoints.Add(_data.Town.Id);
        if (_opt.DemoKit) // demo: every town and waypoint is open, so any zone can be visited straight away
            foreach (var z in _data.Zones.Values.Where(z => !z.IsGuildHall && !z.Prologue && (z.IsTown || z.Waypoint is not null)))
                s.Waypoints.Add(z.Id);
        _sessions[peer.Id] = s;
        _pending.Remove(peer.Id);
        Log($"{name} the {cls} ({account.Name}) joined at level {s.Record.Level} — {_sessions.Count} online");

        if (_opt.StartZone is string start && _data.Zones.ContainsKey(start))
            Enter(s, GetInstance(start, s.Party));
        else if (!Has(s, PrologueDone) && s.Record.Level <= 1 && s.Record.Xp == 0 && _data.Zones.ContainsKey(PrologueZone))
            Enter(s, GetInstance(PrologueZone, s.Party)); // a new knight starts in the burning courtyard at Payns
        else
            Enter(s, GetInstance(_data.TownOf(HomeAct(s)).Id, null));
        DeliverMail(s);
        if (_opt.DemoKit) GiveDemoKit(s);
        if (s.Guild is not null) Tell(s, $"Welcome back to {s.Guild}. /g talks to your guild.");
    }

    /// <summary>
    /// Demo servers (--demo-kit): each character finds one of every base weapon, armour, helm and shield in the
    /// strongbox, once, so the whole armoury can be tried without waiting for drops.
    /// </summary>
    private void GiveDemoKit(Session s)
    {
        GiveEndgameDemoKit(s);
        if (s.Unlocks.Contains("demo_kit")) return;
        var rng = new Random();
        int level = System.Math.Max(1, s.Record.Level), added = 0, i = 0;
        foreach (var name in Loot.BaseNames)
        {
            if (s.Stash.Count >= Loot.StashLimit) break;
            s.Stash.Add(Loot.MakeNamed(rng, name, level, (Rarity)(i++ % 4))); // Common to Elite, so every colour shows
            added++;
        }
        s.Unlocks.Add("demo_kit");
        Tell(s, added == i
            ? $"Demo: one of every weapon and piece of armour ({added}) waits in the Order's Strongbox in town, and every waypoint is open."
            : $"Demo: {added} items were put in the Order's Strongbox; it was too full for the rest.");
    }

    /// <summary>
    /// Demo (0.8): the forge lit, a pouch of runes (two at level 50, ready to fuse), upgrade materials, a Trophy Relic,
    /// Menagerie tokens, gold, an Exotic piece and two set pieces, so the endgame systems can all be tried at once.
    /// </summary>
    private void GiveEndgameDemoKit(Session s)
    {
        if (s.Unlocks.Contains("demo_kit_08") || s.Entity is not { } p) return;
        var rng = new Random();
        s.Unlocks.Add("demo_kit_08");
        s.Unlocks.Add("forge");
        var x = p.Extras;
        foreach (var (k, n) in new[] { (Materials.Whetstone, 40), (Materials.HolyOil, 10), (Materials.SoulCore, 12), (Materials.TrophyRelic, 2), (Materials.PetToken, 4), (Materials.MistEmber, 180) })
            x.Give(k, n);
        p.Gold += 50_000;
        x.Runes.Add(new Rune { Id = Loot.NewId(), Kind = "might", Level = Runes.MaxLevel });
        x.Runes.Add(new Rune { Id = Loot.NewId(), Kind = "might", Level = Runes.MaxLevel });
        foreach (var kind in new[] { "flame", "vigour", "warding", "keenness" }) x.Runes.Add(new Rune { Id = Loot.NewId(), Kind = kind, Level = rng.Next(1, 20) });
        int level = System.Math.Max(10, p.Level);
        var extra = new List<Item>
        {
            Smithing.MakeHigh(rng, level, Rarity.Exotic, ItemSlot.Helm),
            ItemSets.MakePiece(rng, ItemSets.Get("pilgrim")!, level, Rarity.Epic, ItemSlot.Boots),
            ItemSets.MakePiece(rng, ItemSets.Get("pilgrim")!, level, Rarity.Epic, ItemSlot.Mantle, exalted: true),
        };
        foreach (var it in extra) { p.Extras.Note(it); if (p.Inventory.Count < Gear.BagLimit(p)) p.Inventory.Add(it); }
        Tell(s, "{#F2D25A}Demo (0.8):{/} the forge is lit, and you carry runes, upgrade materials, a Trophy Relic, Menagerie tokens, 50,000 gold, an Exotic helm and two Pilgrim's Road set pieces. Master Aubry's forge is through the Sanctuary's west door.");
    }

    private void Leave(NetPeer peer, string reason)
    {
        if (!_sessions.Remove(peer.Id, out var s)) return;
        RemoveFromWorld(s, reason);
    }

    /// <summary>Back to the character select screen: the character is saved and leaves the world; the login stays.</summary>
    private void LogOut(Session s)
    {
        _sessions.Remove(s.Peer.Id);
        RemoveFromWorld(s, "logged out to character select");
        _pending[s.Peer.Id] = new Pending { Account = s.Account, Nonce = AuthCrypto.Nonce(), Since = Clock };
        SendAccountFor(s.Peer, s.Account, "");
    }

    private void RemoveFromWorld(Session s, string reason)
    {
        CancelTrade(s, $"{s.Name} went offline.");
        if (s.Entity is { } e) s.Record = Snapshot(s, e);
        _store.Save(s.Record);
        if (s.Instance is { } inst)
        {
            inst.World.RemovePlayer(s.EntityId);
            inst.Sessions.Remove(s);
            if (inst.Sessions.Count == 0) inst.EmptySince = Clock;
            SendRoster(inst);
        }
        LeaveParty(s, quiet: true);
        Log($"{s.Name} left ({reason}); saved at level {s.Record.Level}");
    }

    private void Kick(NetPeer peer, string reason)
    {
        Msg.Reject(_w, reason);
        peer.Send(_w, DeliveryMethod.ReliableOrdered);
        _net.TriggerUpdate();
        // The reason also rides on the disconnect itself, in case the disconnect overtakes the message.
        var why = new NetDataWriter();
        why.Put(reason.Length > 200 ? reason[..200] : reason);
        peer.Disconnect(why);
        Log($"rejected peer {peer.Id}: {reason}");
    }

    private void Learn(Session s, string skillId)
    {
        if (s.Entity is not { } p) return;
        var result = s.Instance!.World.LearnSkill(p, skillId);
        if (result != World.LearnResult.Learned)
            Tell(s, result switch
            {
                World.LearnResult.AtMaximum => "That skill is already at level 10.",
                World.LearnResult.NotEnoughPoints => "Not enough build points.",
                _ => "Unknown skill.",
            });
        SendStats(s);
    }

    private void UseScroll(Session s, string skillId)
    {
        if (s.Entity is not { } p || !_data.Skills.TryGetValue(skillId, out var skill)) return;
        if (s.Scrolls <= 0) { Tell(s, "You have no Skill Mastery Experience Scrolls. Win arena duels to earn them."); return; }
        var m = p.MasteryFor(skill.Id);
        if (!m.ApplyScroll())
        {
            Tell(s, m.ScrollLevels >= SkillMastery.MaxScrollLevels
                ? $"{skill.Name} has taken all {SkillMastery.MaxScrollLevels} scroll levels it can."
                : $"{skill.Name} mastery is already at 100.");
            return;
        }
        s.Scrolls--;
        Tell(s, $"You read the scroll: {skill.Name} mastery {m.Level} ({m.ScrollLevels}/{SkillMastery.MaxScrollLevels} from scrolls).");
        SendStats(s);
    }

    // ------------------------------------------------------------------ travel

    private void Travel(Session s, TravelKind kind, string zoneId)
    {
        if (s.Entity is not { } p || p.Dead) return;
        if (s.Instance!.World.InDuel(p)) { Tell(s, "Finish your duel first."); return; }
        switch (kind)
        {
            case TravelKind.Town:
                if (s.Instance.Zone.IsTown) return;
                if (Clock < s.TownReadyAt) { Tell(s, $"You can return to town again in {s.TownReadyAt - Clock:F0} seconds."); return; }
                s.TownReadyAt = Clock + TownCooldown;
                Enter(s, GetInstance(_data.TownOf(s.Instance.Zone.Act).Id, null));
                break;
            case TravelKind.Waypoint:
                var here = s.Instance.Zone;
                if (here.Waypoint is not { } wp || Vec2.Distance(p.Pos, new Vec2(wp[0], wp[1])) > WaypointRadius) { Tell(s, "Stand on a waypoint to use it."); return; }
                if (!_data.Zones.TryGetValue(zoneId, out var dest) || !s.Waypoints.Contains(dest.Id)) { Tell(s, "You haven't found that waypoint yet."); return; }
                if (dest.Id == here.Id) return;
                var at = dest.Waypoint is { } dwp ? new Vec2(dwp[0], dwp[1] - 120) : (Vec2?)null;
                Enter(s, GetInstance(dest.Id, s.Party), at);
                break;
        }
    }

    /// <summary>Walking onto an exit takes you through it (after a moment, so arrivals don't bounce straight back).</summary>
    private void CheckExits(Session s)
    {
        if (s.Entity is not { } p || p.Dead || Clock - s.ArrivedAt < 1.5 || s.Instance!.World.InDuel(p)) return;
        var zone = s.Instance.Zone;
        foreach (var exit in zone.Exits)
        {
            if (Vec2.Distance(p.Pos, new Vec2(exit.X, exit.Y)) > ExitRadius) continue;
            var dest = _data.Zones[exit.To];
            if (dest.IsGuildHall)
            {
                if (s.Guild is null)
                {
                    s.ArrivedAt = Clock + 3; // don't repeat the message every tick
                    Tell(s, "Only members of a Commandery may enter the halls. /guild create name founds one.");
                    return;
                }
                Enter(s, GetInstance(dest.Id, null, s.Guild), ArrivalFrom(dest, zone.Id));
                return;
            }
            if (!ExitAllowed(s, exit)) return;
            var into = GetInstance(dest.Id, dest.IsTown ? null : s.Party, owner: s.Name);
            if (into.World.Players.Count() >= dest.PlayerLimit) { s.ArrivedAt = Clock + 3; Tell(s, $"{dest.Name} is full ({dest.PlayerLimit} knights)."); return; }
            Enter(s, into, ArrivalFrom(dest, zone.Id));
            return;
        }
        if (zone.Waypoint is { } wp && Vec2.Distance(p.Pos, new Vec2(wp[0], wp[1])) <= WaypointRadius && s.Waypoints.Add(zone.Id))
            Tell(s, $"Waypoint found: {zone.Name}.");
        foreach (var q in _data.Quests)
        {
            var o = q.Objective;
            if (o.Type != ObjectiveType.ReachPoint || o.Target != zone.Id || s.QuestsDone.Contains(q.Id)) continue;
            if (Vec2.Distance(p.Pos, new Vec2(o.X * zone.Width, o.Y * zone.Height)) <= o.Radius) Complete(s, q);
        }
    }

    // ------------------------------------------------------------------ quests

    private void Complete(Session s, QuestDef q)
    {
        if (!s.QuestsDone.Add(q.Id)) return;
        var parts = new List<string>();
        if (s.Entity is { } p)
        {
            if (q.Reward.BuildPoints > 0) { p.BuildPoints += q.Reward.BuildPoints; parts.Add($"+{q.Reward.BuildPoints} build point"); }
            if (q.Reward.Item is string rarity && Enum.TryParse<Rarity>(rarity, true, out var r))
            {
                var item = Loot.MakeItem(Random.Shared, Random.Shared.Next(1_000_000, int.MaxValue), System.Math.Max(p.Level, 10), r);
                p.Inventory.Add(item);
                parts.Add($"{item.Rarity} {item.Name}");
            }
        }
        if (q.Reward.Unlock is string u) { s.Unlocks.Add(u); parts.Add(UnlockText(u)); }
        foreach (var f in q.Reward.Flags) s.Unlocks.Add(f);
        if (q.Reward.Gold > 0 && s.Entity is { } pg) { pg.Gold += q.Reward.Gold; parts.Add($"{q.Reward.Gold:N0} gold"); }
        Tell(s, $"{{#F2D25A}}Quest complete: {q.Name}.{{/}} {string.Join(", ", parts)}");
        SendStats(s);
    }

    private static string UnlockText(string u) => u switch
    {
        "forge" => "Master Aubry's forge is lit",
        "identify" => "Brother Thomas identifies for you",
        "free_healing" => "Sister Ysabeau heals for free",
        "eleanor_rare_stock" => "Eleanor stocks rare goods",
        "hermit" => "the Hermit will speak with you",
        "act2" => "the road to Act II is open: the Underground",
        "act3" => "the road to Act III is open: Infiltration",
        "act4" => "the road to Act IV is open: the Reckoning",
        "cleric_robes" => "Marie's cleric robes: you can walk the court in disguise",
        _ => u,
    };

    // ------------------------------------------------------------------ NPCs

    private void Interact(Session s, int entityId)
    {
        if (s.Entity is { } pc && s.Instance!.World.Get(entityId) is { Kind: EntityKind.Chest } chest) { OpenCoffer(s, pc, chest); return; }
        if (s.Entity is not { } p || s.Instance!.World.Get(entityId) is not { Kind: EntityKind.Npc } npcEntity) return;
        if (Vec2.Distance(p.Pos, npcEntity.Pos) > NpcRadius) { Tell(s, "Walk closer to talk."); return; }
        var npc = s.Instance.Zone.Npcs[npcEntity.TypeIndex];
        if (npc.HiddenUntil is string hidden && !Has(s, hidden)) return;
        string Say(string text) => $"{{#C9A227}}{npc.Name}:{{/}} {text}";
        if (npc.Unlock is string needed && !Has(s, needed))
        {
            var q = _data.Quests.FirstOrDefault(x => x.Id == needed);
            if (npc.LockedLine is string locked) { Speak(s, npc, locked); return; }
            Tell(s, Say(npc.Id switch
            {
                "master_aubry" => "Without my hammer there is no forge. Girauld took it into the quarry.",
                "brother_thomas" => "...",
                "the_hermit" => "Not yet. Find what the Order sealed away, then come back.",
                _ => $"Come back when you have finished {q?.Name ?? needed}.",
            }));
            return;
        }
        foreach (var service in npc.Services)
        {
            switch (service)
            {
                case "heal":
                    p.Hp = p.MaxHp;
                    p.Mana = p.MaxMana;
                    Tell(s, Say("Sit. Let me see to those wounds."));
                    if (npc.Services.Contains("potions")) OpenShop(s, npc);
                    return;
                case "main_quest":
                    QuestGiver(s, npc);
                    return;
                case "buy" or "sell" or "gamble" or "potions":
                    OpenShop(s, npc);
                    return;
                case "forge":
                    if (s.Instance.Zone.Id != ForgeZone)
                    {
                        // The anvils of every town went down to Aubry's forge under the Sanctuary.
                        Speak(s, npc, npc.Lines.Count > 0 ? NextLine(s, npc) : "The forge is under the Sanctuary now.");
                        GoToForge(s);
                        return;
                    }
                    if (!s.Unlocks.Contains("forge")) { Tell(s, Say("Without my hammer there is no forge. Girauld took it into the quarry.")); return; }
                    SendUi(s, UiKind.Forge, new ForgeState(npc.Name, true, "forge"));
                    return;
                case "runes":
                    SendUi(s, UiKind.Forge, new ForgeState(npc.Name, true, "runes"));
                    return;
                case "athanor":
                    SendUi(s, UiKind.Forge, new ForgeState(npc.Name, true, "athanor"));
                    return;
                case "daily":
                    SendDaily(s);
                    return;
                case "herald":
                    SendHerald(s);
                    return;
                case "pets":
                    SendUi(s, UiKind.Pets, npc.Name);
                    return;
                case "armoury":
                    SendArmoury(s);
                    return;
                case "stash":
                    SendStash(s);
                    return;
                case "guild_stash":
                    SendGuildStash(s);
                    return;
                case "auction":
                    SendAuctionPage(s, "", null, null, 1, 100, "price", false, 1);
                    return;
                case "guild":
                    SendGuild(s);
                    return;
                case "identify":
                    Speak(s, npc, npc.Lines.Count > 0 ? NextLine(s, npc) : "I know every relic the Order kept. Bring me what you find.");
                    return;
                case "lore" or "talk":
                    Speak(s, npc, npc.Lines.Count > 0 ? NextLine(s, npc) : "They called it heresy because they could not call it what it was: a debt the king could not pay.");
                    return;
            }
        }
    }

    // ------------------------------------------------------------------ chat, parties and duels

    private void Chat(Session s, string channel, string raw)
    {
        string text = new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length == 0) return;
        if (text.StartsWith('/')) { Command(s, text); return; }
        if (Muted(s) || !ChatAllowed(s)) return;
        var to = new HashSet<Session>(s.Instance!.Sessions);
        foreach (var m in s.Party.Members) if (_sessions.ContainsValue(m)) to.Add(m);
        Msg.Chat(_w, new ChatLine(s.Instance.Zone.IsTown ? "town" : "group", s.Name, text));
        foreach (var x in to) x.Peer.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    private Session? Online(string name) =>
        _sessions.Values.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

    private void Command(Session s, string text)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0].ToLowerInvariant(), arg = parts.Length > 1 ? parts[1] : "";
        if (AdminCommand(s, cmd, arg)) return;
        switch (cmd)
        {
            case "/invite":
                if (Online(arg) is not { } who || who == s) { Tell(s, $"No one called '{arg}' is online."); return; }
                if (s.Party.Leader != s) { Tell(s, "Only the party leader can invite."); return; }
                if (s.Party.Members.Count >= World.MaxGroupSize) { Tell(s, "Your party is full (6)."); return; }
                if (who.Party.Members.Count > 1) { Tell(s, $"{who.Name} is already in a party."); return; }
                who.Pending = (s.Name, "party", Clock);
                Tell(who, $"{s.Name} invites you to their party. Type /accept or /decline.");
                Tell(s, $"Invitation sent to {who.Name}.");
                return;
            case "/duel":
                if (Online(arg) is not { } foe || foe == s) { Tell(s, $"No one called '{arg}' is online."); return; }
                if (!s.Instance!.Zone.IsTown || foe.Instance != s.Instance) { Tell(s, "Both duelists must be in town."); return; }
                foe.Pending = (s.Name, "duel", Clock);
                Tell(foe, $"{s.Name} challenges you to a duel in {s.Instance.Zone.Arena?.Name}. Type /accept or /decline.");
                Tell(s, $"Challenge sent to {foe.Name}.");
                return;
            case "/accept":
                if (s.Pending is not { } pend || Clock - pend.At > InviteLifetime || Online(pend.From) is not { } from)
                { Tell(s, "Nothing to accept."); return; }
                s.Pending = null;
                switch (pend.Kind)
                {
                    case "duel": AcceptDuel(from, s); break;
                    case "trade": OpenTrade(from, s); break;
                    case "guild": JoinGuild(s, pend.From); break;
                    default: JoinParty(s, from.Party); break;
                }
                return;
            case "/decline":
                if (s.Pending is { } d && Online(d.From) is { } asker) Tell(asker, $"{s.Name} declined.");
                s.Pending = null;
                return;
            case "/leave":
                LeaveParty(s, quiet: false);
                return;
            case "/party":
                Tell(s, $"Party: {string.Join(", ", s.Party.Members.Select(m => m.Name + (m == s.Party.Leader ? " (leader)" : "")))}");
                return;
            case "/trade":
                RequestTrade(s, arg);
                return;
            case "/guild":
                GuildCommand(s, arg);
                return;
            case "/g":
                if (Muted(s)) return;
                GuildChat(s, arg);
                return;
            default:
                Tell(s, "Commands: /invite, /duel, /trade, /accept, /decline, /leave, /party, /guild, /g");
                return;
        }
    }

    private void JoinParty(Session s, Party party)
    {
        if (party.Members.Count >= World.MaxGroupSize) { Tell(s, "That party is full."); return; }
        LeaveParty(s, quiet: true);
        s.Party = party;
        party.Members.Add(s);
        foreach (var m in party.Members) Tell(m, $"{s.Name} joined the party ({party.Members.Count}/{World.MaxGroupSize}).");
    }

    private void LeaveParty(Session s, bool quiet)
    {
        var party = s.Party;
        if (party.Members.Count <= 1) return;
        party.Members.Remove(s);
        if (party.Leader == s) party.Leader = party.Members[0];
        foreach (var m in party.Members) Tell(m, $"{s.Name} left the party.{(party.Leader == m ? " You lead now." : "")}");
        s.Party = new Party(s);
        if (!quiet) Tell(s, "You left the party.");
    }

    private void AcceptDuel(Session challenger, Session s)
    {
        if (challenger.Instance != s.Instance || !s.Instance!.Zone.IsTown || challenger.Entity is not { } a || s.Entity is not { } b)
        { Tell(s, "You both need to be in town."); return; }
        if (s.Instance.World.StartDuel(a, b) is string why) { Tell(s, why); Tell(challenger, why); }
    }

    private void DuelEnded(Instance inst, SimEvent e)
    {
        var a = inst.Sessions.FirstOrDefault(x => x.EntityId == e.Source);
        var b = inst.Sessions.FirstOrDefault(x => x.EntityId == e.Target);
        if (a is null || b is null) return;
        if (e.Value == 0) { Tell(a, "The duel ends in a draw."); Tell(b, "The duel ends in a draw."); return; }
        Tell(b, $"{a.Name} wins the duel.");
        if (_duelRewards.TryAward(a.Name, b.Name, DateTime.UtcNow))
        {
            a.Scrolls++;
            Tell(a, "{#F2D25A}You win! You earn a Skill Mastery Experience Scroll.{/} Read it from the skills panel (K).");
            SaveDuelRewards();
        }
        else Tell(a, $"You win! No scroll: you already earned one from {b.Name} in the last 24 hours.");
        Log($"duel: {a.Name} beat {b.Name}");
    }

    private void LoadDuelRewards()
    {
        try
        {
            if (File.Exists(_duelFile))
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_duelFile)) ?? [])
                    if (k.Split('|') is [var w, var l]) _duelRewards.Restore(w, l, v);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { Log($"could not read duel rewards: {ex.Message}"); }
    }

    private void SaveDuelRewards()
    {
        var map = _duelRewards.Export().ToDictionary(kv => $"{kv.Key.Winner}|{kv.Key.Loser}", kv => kv.Value);
        File.WriteAllText(_duelFile, JsonSerializer.Serialize(map));
    }

    // ------------------------------------------------------------------ loop

    public void Tick(float dt)
    {
        _net.PollEvents();
        RunConsole();
        _ticks++;
        if (_ticks % (20 * 30) == 0) SweepFrontDoor();
        foreach (var inst in _instances.Values.ToList())
        {
            if (inst.Sessions.Count == 0 && (!inst.Zone.IsTown || inst.Zone.IsPersonal || inst.Zone.IsGuildHall))
            {
                if (Clock - inst.EmptySince > EmptyInstanceLifetime) { _instances.Remove(inst.Key); Log($"closed empty instance {inst.Key}"); }
                continue;
            }
            inst.World.Tick(dt);
            foreach (var e in inst.World.Events)
            {
                Broadcast(inst, e);
                switch (e.Type)
                {
                    case SimEventType.UniqueKilled:
                        foreach (var q in _data.Quests.Where(q => q.Objective.Type == ObjectiveType.KillUnique && q.Objective.Target == e.Text))
                            foreach (var s in inst.Sessions.ToList()) Complete(s, q);
                        StoryOnUniqueKilled(inst, e.Text);
                        if (_opt.ExitOnUnique == e.Text) { Log($"{e.Text} slain in {inst.Key}"); ShouldExit = true; }
                        break;
                    case SimEventType.DuelEnded:
                        DuelEnded(inst, e);
                        break;
                    case SimEventType.Yielded:
                        StartVote(inst, e.Text);
                        break;
                }
                EndgameEvent(inst, e);
            }
            TickVotes(inst);
            if (inst.World.Cleared && !inst.ClearAnnounced)
            {
                inst.ClearAnnounced = true;
                Log($"{inst.Key}: {inst.Zone.Name} cleared in {inst.World.ClearedAt:F0} s by " +
                    string.Join(", ", inst.Sessions.Select(x => x.Entity).Where(p => p is not null)
                        .Select(p => $"{p!.Name} ({p.Class} L{p.Level}, {p.Kills} kills, {p.Deaths} deaths)")));
                foreach (var q in _data.Quests.Where(q => q.Objective.Type == ObjectiveType.ClearZone && q.Objective.Target == inst.Zone.Id))
                    foreach (var s in inst.Sessions.ToList()) Complete(s, q);
                foreach (var s in inst.Sessions) s.Entity?.Extras.Count("clear:" + inst.Zone.Id);
                foreach (var s in inst.Sessions) if (s.Entity is { } p) _store.Save(s.Record = Snapshot(s, p));
                if (_opt.ExitOnClear && !inst.Zone.Prologue) ShouldExit = true;
            }
            SendSnapshots(inst);
        }
        foreach (var s in _sessions.Values.ToList()) CheckExits(s);
        TickEndgame();
        if (_ticks % 10 == 0)
        {
            foreach (var s in _sessions.Values) { SendStats(s); SendParty(s); SendExtras(s); }
        }
        if (_ticks % (20 * 30) == 0)
        {
            if (_auctions.Expire(DateTime.UtcNow)) _auctions.Save();
            foreach (var s in _sessions.Values) DeliverMail(s);
        }
        if (_ticks % (20 * 60) == 0) // autosave every minute
            foreach (var s in _sessions.Values) if (s.Entity is { } p) _store.Save(s.Record = Snapshot(s, p));
        if (_opt.StopAfterTicks is int n && _ticks >= n) ShouldExit = true;
    }

    private void Broadcast(Instance inst, SimEvent e)
    {
        Msg.Event(_w, e);
        foreach (var s in inst.Sessions) s.Peer.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    private void SendRoster(Instance inst)
    {
        var roster = inst.World.Players.Select(p => new RosterEntry(p.Id, p.Name, p.Class)).ToList();
        Msg.Roster(_w, roster);
        foreach (var s in inst.Sessions) s.Peer.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    private void SendParty(Session s)
    {
        var members = s.Party.Members.Where(m => m != s).Select(m =>
        {
            var e = m.Entity;
            return new PartyMember(m.Name, m.Class, e?.Level ?? m.Record.Level, e?.Hp ?? 0, e?.MaxHp ?? 1,
                m.Instance?.Zone.Name ?? "", m == s.Party.Leader, _sessions.ContainsValue(m));
        }).ToList();
        Msg.Party(_w, members);
        s.Peer.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    private readonly List<EntityState> _visible = [];

    private void SendSnapshots(Instance inst)
    {
        uint tick = (uint)inst.World.TickCount;
        foreach (var s in inst.Sessions)
        {
            if (s.Entity is not { } me) continue;
            _visible.Clear();
            foreach (var e in inst.World.Entities)
            {
                if (e.Kind == EntityKind.Npc && inst.Zone.Npcs[e.TypeIndex].HiddenUntil is string q && !s.QuestsDone.Contains(q)) continue;
                if (e.Kind is EntityKind.Player or EntityKind.Npc || Vec2.Distance(e.Pos, me.Pos) <= Protocol.InterestRadius)
                    _visible.Add(EntityState.From(e));
            }
            int chunks = System.Math.Max(1, (_visible.Count + Protocol.EntitiesPerChunk - 1) / Protocol.EntitiesPerChunk);
            for (int c = 0; c < chunks; c++)
            {
                int start = c * Protocol.EntitiesPerChunk, count = System.Math.Min(Protocol.EntitiesPerChunk, _visible.Count - start);
                Msg.SnapshotHeader(_w, s.Visit, tick, (ushort)c, (ushort)chunks, (ushort)System.Math.Max(0, count));
                for (int i = 0; i < count; i++) _visible[start + i].Write(_w);
                s.Peer.Send(_w, DeliveryMethod.Unreliable);
            }
        }
    }

    private void SendStats(Session s)
    {
        if (s.Entity is not { } p) return;
        var world = s.Instance!.World;
        var skills = _data.SkillList.Select(sk =>
        {
            var m = p.MasteryFor(sk.Id);
            return new SkillInfo(sk.Id, p.SkillLevel(sk.Id), m.Level, m.Hits, m.ScrollLevels);
        }).ToList();
        Msg.Stats(_w, new PlayerStats(p.Level, p.Xp, Progression.XpToNext(p.Level), p.Gold, (int)p.Mana, p.MaxMana,
            p.Kills, p.Deaths, p.GearDamage, world.Defense(p), skills,
            p.Equipped.Values.OrderBy(i => i.Slot).Select(Msg.Info).ToList(),
            p.Inventory.OrderByDescending(i => i.Rarity).ThenByDescending(Loot.Score).Select(Msg.Info).ToList(),
            p.BuildPoints, p.SpellBar.ToList(), world.WeaponOf(p), (float)world.BlockChance(p),
            _data.Quests.Select(q => new QuestInfo(q.Id, s.QuestsDone.Contains(q.Id))).ToList(),
            s.Waypoints.ToList(), s.Scrolls, s.Unlocks.ToList(), p.HealthPotions, p.ManaPotions, s.Guild ?? ""));
        s.Peer.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    private void Tell(Session s, string text)
    {
        Msg.Chat(_w, new ChatLine("system", "", text));
        s.Peer.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    private static void Log(string text) => Console.WriteLine($"[server {DateTime.Now:HH:mm:ss}] {text}");

    public void Dispose()
    {
        foreach (var s in _sessions.Values)
            if (s.Entity is { } p) _store.Save(Snapshot(s, p));
        _net.Stop();
    }
}
