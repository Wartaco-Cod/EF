using LiteNetLib.Utils;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;

namespace TacoFactory.Net;

/// <summary>Taco Factory wire protocol. Every message starts with a <see cref="MsgType"/> byte.</summary>
public static class Protocol
{
    public const int Version = 9; // 9: endgame (14 gear slots, rune sockets, traits, sets, pets in snapshots). 8: player snapshots carry the weapon family. 7: Acts II-IV, story choices, cutscenes, wider entity flags. 6: accounts, encrypted login, character select. 5: snapshots carry the zone visit.  4: economy (items with properties, shop, stash, forge, trade, auction house, guilds)
    public const string ConnectKey = "taco-factory-templar-knights-v1";
    public const int DefaultPort = 7777;
    public const int EntitiesPerChunk = 36; // 36 × 26 bytes + header stays under the 1,023-byte unreliable limit
    public const float InterestRadius = 3000;
}

public enum MsgType : byte
{
    // client -> server
    Hello = 1,
    Input = 2,
    ChatSend = 3,
    LearnSkill = 4,
    Travel = 5,
    Interact = 6,
    UseScroll = 7,
    Command = 8,
    Auth = 9,
    CreateCharacter = 20,
    DeleteCharacter = 21,
    EnterWorld = 22,
    // server -> client
    Welcome = 10,
    Snapshot = 11,
    Event = 12,
    Stats = 13,
    Roster = 14,
    Reject = 15,
    Chat = 16,
    Party = 17,
    Ui = 18,
    ServerHello = 19,
    Account = 23,
}

/// <summary>One entity in a snapshot, 25 bytes on the wire.</summary>
public readonly record struct EntityState(int Id, EntityKind Kind, ushort TypeIndex, Vec2 Pos, int Hp, int MaxHp, byte Level, EntityFlags Flags)
{
    public bool Dead => (Flags & EntityFlags.Dead) != 0;
    /// <summary>For players the type index carries the class in its low byte and the weapon family in its high byte.</summary>
    public ClassId PlayerClass => (ClassId)(TypeIndex & 0xFF);
    public WeaponType PlayerWeapon => (WeaponType)((TypeIndex >> 8) & 0x7);
    /// <summary>The companion following a player (Endgame.Pets index + 1), 0 for none.</summary>
    public int PlayerPet => (TypeIndex >> 11) & 0x1F;

    public void Write(NetDataWriter w)
    {
        w.Put(Id); w.Put((byte)Kind); w.Put(TypeIndex); w.Put(Pos.X); w.Put(Pos.Y);
        w.Put(Hp); w.Put(MaxHp); w.Put(Level); w.Put((ushort)Flags);
    }

    public static EntityState Read(NetDataReader r) => new(
        r.GetInt(), (EntityKind)r.GetByte(), r.GetUShort(), new Vec2(r.GetFloat(), r.GetFloat()),
        r.GetInt(), r.GetInt(), r.GetByte(), (EntityFlags)r.GetUShort());

    public static EntityState From(Entity e) => new(
        e.Id, e.Kind, (ushort)(e.Kind == EntityKind.Player
            ? (int)e.Class | (int)(e.Equipped.TryGetValue(ItemSlot.Weapon, out var w) ? w.Weapon : WeaponType.None) << 8 | (System.Math.Clamp(e.PetIndex, 0, 31) << 11)
            : System.Math.Clamp(e.TypeIndex, 0, ushort.MaxValue)),
        e.Pos, e.Hp, e.MaxHp, (byte)System.Math.Clamp(e.Level, 0, 255), e.Flags);
}

public sealed record RosterEntry(int Id, string Name, ClassId Class);

/// <summary>One skill as the owner sees it: learned level (0-10) and mastery (0-100).</summary>
public sealed record SkillInfo(string SkillId, int Level, int MasteryLevel, int MasteryHits, int ScrollLevels);

/// <summary>An item as clients see it (bags, stash, shop, trade, auction house).</summary>
public sealed record ItemInfo(long Id, string Name, ItemSlot Slot, Rarity Rarity, int ItemLevel, int Damage, int Defense, WeaponType Weapon,
    Dictionary<string, int> Props, bool Forged, int Value, int Upgrade = 0, string? Trait = null, string? Set = null, bool Exalted = false,
    List<RuneInfo?>? Sockets = null)
{
    public List<RuneInfo?> SocketList => Sockets ?? [];
    /// <summary>A property's total on this item: rolled value, socketed runes and trait (as <see cref="Item.Prop"/>).</summary>
    public int Total(string key) =>
        Props.GetValueOrDefault(key) + SocketList.Where(r => r is not null && Runes.Kind(r.Kind)?.Property == key).Sum(r => r!.Value)
        + (Traits.Get(Trait) is { } t && t.Property == key ? Traits.Value(t, ItemLevel) : 0);
}

/// <summary>A rune as clients see it: in the pouch or in a socket.</summary>
public sealed record RuneInfo(long Id, string Kind, Rarity Tier, int Level, long Xp, long XpToNext, int Value)
{
    public static RuneInfo From(Rune r) => new(r.Id, r.Kind, r.Tier, r.Level, r.Xp, r.Level >= Runes.MaxLevel ? 0 : Runes.XpToNext(r), Runes.Value(r));
    public string Name => $"{Tier} {Runes.Kind(Kind)?.Name ?? Kind}";
}

public sealed record PlayerStats(int Level, long Xp, long XpToNext, int Gold, int Mana, int MaxMana, int Kills, int Deaths,
    int GearDamage, int GearDefense, List<SkillInfo> Skills, List<ItemInfo> Equipped, List<ItemInfo> Inventory,
    int BuildPoints, List<string> Bar, WeaponType Weapon, float BlockChance,
    List<QuestInfo>? Quests = null, List<string>? Waypoints = null, int Scrolls = 0, List<string>? Unlocks = null,
    int HealthPotions = 0, int ManaPotions = 0, string Guild = "")
{
    public List<QuestInfo> QuestList => Quests ?? [];
    public List<string> WaypointList => Waypoints ?? [];
    public List<string> UnlockList => Unlocks ?? [];
}

public sealed record ChatLine(string Channel, string From, string Text);

public sealed record ExitInfo(string To, string Name, float X, float Y);

public sealed record WelcomeInfo(int YourId, string ZoneId, string ZoneName, float ZoneWidth, float ZoneHeight, int TickRate, string GroundTile,
    bool IsTown = false, List<ExitInfo>? Exits = null, float[]? Waypoint = null, float[]? Arena = null, string ArenaName = "",
    List<string>? Npcs = null, int Act = 1, int LevelMin = 1, int LevelMax = 1, byte Visit = 0)
{
    public List<ExitInfo> ExitList => Exits ?? [];
    public List<string> NpcList => Npcs ?? [];
}

public enum TravelKind : byte { Waypoint = 0, Town = 1 }

public sealed record QuestInfo(string Id, bool Done);

public sealed record PartyMember(string Name, ClassId Class, int Level, int Hp, int MaxHp, string ZoneName, bool Leader, bool Online);

public static partial class Msg
{

    public static void Input(NetDataWriter w, uint seq, PlayerInput input)
    {
        w.Reset(); w.Put((byte)MsgType.Input); w.Put(seq);
        var mv = input.Move.LengthSquared > 1 ? input.Move.Normalized() : input.Move;
        w.Put((sbyte)System.Math.Round(mv.X * 127)); w.Put((sbyte)System.Math.Round(mv.Y * 127));
        w.Put((byte)input.Action); w.Put(input.SkillSlot); w.Put(input.TargetId); w.Put(input.TargetPos.X); w.Put(input.TargetPos.Y);
    }

    public static PlayerInput ReadInput(NetDataReader r, out uint seq)
    {
        seq = r.GetUInt();
        var move = new Vec2(r.GetSByte() / 127f, r.GetSByte() / 127f);
        var action = (PlayerAction)r.GetByte();
        if (action > PlayerAction.UseSkill) action = PlayerAction.None;
        byte slot = (byte)System.Math.Min(r.GetByte(), (byte)(Entity.BarSlots - 1));
        return new PlayerInput(move, action, slot, r.GetInt(), new Vec2(r.GetFloat(), r.GetFloat()));
    }

    public static void Welcome(NetDataWriter w, WelcomeInfo info)
    {
        w.Reset(); w.Put((byte)MsgType.Welcome); w.Put(info.YourId); w.Put(info.ZoneId); w.Put(info.ZoneName);
        w.Put(info.ZoneWidth); w.Put(info.ZoneHeight); w.Put(info.TickRate); w.Put(info.GroundTile);
        w.Put(info.IsTown); w.Put((byte)info.Act); w.Put((byte)info.LevelMin); w.Put((byte)info.LevelMax);
        w.Put((byte)info.ExitList.Count);
        foreach (var e in info.ExitList) { w.Put(e.To); w.Put(e.Name); w.Put(e.X); w.Put(e.Y); }
        w.Put(info.Waypoint is not null);
        if (info.Waypoint is { } wp) { w.Put(wp[0]); w.Put(wp[1]); }
        w.Put(info.Arena is not null);
        if (info.Arena is { } a) { w.Put(a[0]); w.Put(a[1]); w.Put(a[2]); w.Put(a[3]); w.Put(info.ArenaName); }
        w.Put((byte)info.NpcList.Count);
        foreach (var n in info.NpcList) w.Put(n);
        w.Put(info.Visit);
    }

    public static WelcomeInfo ReadWelcome(NetDataReader r)
    {
        int id = r.GetInt(); string zid = r.GetString(), zname = r.GetString();
        float zw = r.GetFloat(), zh = r.GetFloat(); int rate = r.GetInt(); string tile = r.GetString();
        bool town = r.GetBool(); int act = r.GetByte(), lo = r.GetByte(), hi = r.GetByte();
        var exits = new List<ExitInfo>();
        for (int i = 0, n = r.GetByte(); i < n; i++) exits.Add(new ExitInfo(r.GetString(), r.GetString(), r.GetFloat(), r.GetFloat()));
        float[]? wp = r.GetBool() ? [r.GetFloat(), r.GetFloat()] : null;
        float[]? arena = null;
        string arenaName = "";
        if (r.GetBool()) { arena = [r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat()]; arenaName = r.GetString(); }
        var npcs = new List<string>();
        for (int i = 0, n = r.GetByte(); i < n; i++) npcs.Add(r.GetString());
        byte visit = r.GetByte();
        return new WelcomeInfo(id, zid, zname, zw, zh, rate, tile, town, exits, wp, arena, arenaName, npcs, act, lo, hi, visit);
    }

    public static void Travel(NetDataWriter w, TravelKind kind, string zoneId)
    {
        w.Reset(); w.Put((byte)MsgType.Travel); w.Put((byte)kind); w.Put(zoneId);
    }

    public static void Interact(NetDataWriter w, int entityId)
    {
        w.Reset(); w.Put((byte)MsgType.Interact); w.Put(entityId);
    }

    public static void UseScroll(NetDataWriter w, string skillId)
    {
        w.Reset(); w.Put((byte)MsgType.UseScroll); w.Put(skillId);
    }

    public static void Party(NetDataWriter w, IReadOnlyList<PartyMember> members)
    {
        w.Reset(); w.Put((byte)MsgType.Party); w.Put((byte)members.Count);
        foreach (var m in members)
        {
            w.Put(m.Name); w.Put((byte)m.Class); w.Put((byte)m.Level); w.Put(m.Hp); w.Put(m.MaxHp); w.Put(m.ZoneName); w.Put(m.Leader); w.Put(m.Online);
        }
    }

    public static List<PartyMember> ReadParty(NetDataReader r)
    {
        var list = new List<PartyMember>();
        for (int i = 0, n = r.GetByte(); i < n; i++)
            list.Add(new PartyMember(r.GetString(), (ClassId)r.GetByte(), r.GetByte(), r.GetInt(), r.GetInt(), r.GetString(), r.GetBool(), r.GetBool()));
        return list;
    }

    public static void SnapshotHeader(NetDataWriter w, byte visit, uint tick, ushort chunk, ushort chunkCount, ushort count)
    {
        w.Reset(); w.Put((byte)MsgType.Snapshot); w.Put(visit); w.Put(tick); w.Put(chunk); w.Put(chunkCount); w.Put(count);
    }

    public static void Event(NetDataWriter w, SimEvent e)
    {
        w.Reset(); w.Put((byte)MsgType.Event); w.Put((byte)e.Type); w.Put(e.Source); w.Put(e.Target); w.Put(e.Value); w.Put(e.Text ?? "");
    }

    public static SimEvent ReadEvent(NetDataReader r) =>
        new((SimEventType)r.GetByte(), r.GetInt(), r.GetInt(), r.GetInt(), r.GetString());

    public static void Stats(NetDataWriter w, PlayerStats s)
    {
        w.Reset(); w.Put((byte)MsgType.Stats); w.Put(s.Level); w.Put(s.Xp); w.Put(s.XpToNext); w.Put(s.Gold);
        w.Put(s.Mana); w.Put(s.MaxMana); w.Put(s.Kills); w.Put(s.Deaths); w.Put(s.GearDamage); w.Put(s.GearDefense);
        w.Put((ushort)s.Skills.Count);
        foreach (var m in s.Skills) { w.Put(m.SkillId); w.Put((byte)m.Level); w.Put((byte)m.MasteryLevel); w.Put(m.MasteryHits); w.Put((byte)m.ScrollLevels); }
        PutItems(w, s.Equipped);
        PutItems(w, s.Inventory);
        w.Put(s.BuildPoints);
        w.Put((byte)s.Bar.Count);
        foreach (var b in s.Bar) w.Put(b);
        w.Put((byte)s.Weapon);
        w.Put(s.BlockChance);
        w.Put((byte)s.QuestList.Count);
        foreach (var q in s.QuestList) { w.Put(q.Id); w.Put(q.Done); }
        w.Put((byte)s.WaypointList.Count);
        foreach (var z in s.WaypointList) w.Put(z);
        w.Put(s.Scrolls);
        w.Put((byte)s.UnlockList.Count);
        foreach (var u in s.UnlockList) w.Put(u);
        w.Put((byte)s.HealthPotions); w.Put((byte)s.ManaPotions); w.Put(s.Guild);
    }

    private static void PutItems(NetDataWriter w, List<ItemInfo> items)
    {
        w.Put((byte)System.Math.Min(items.Count, 60));
        foreach (var i in items.Take(60))
        {
            w.Put(i.Id); w.Put(i.Name); w.Put((byte)i.Slot); w.Put((byte)i.Rarity); w.Put((byte)i.ItemLevel); w.Put((short)i.Damage); w.Put((short)i.Defense);
            w.Put((byte)i.Weapon); w.Put(i.Forged); w.Put(i.Value);
            w.Put((byte)i.Props.Count);
            foreach (var (k, v) in i.Props) { w.Put(k); w.Put((short)v); }
            w.Put((byte)i.Upgrade); w.Put(i.Trait ?? ""); w.Put(i.Set ?? ""); w.Put(i.Exalted);
            w.Put((byte)i.SocketList.Count);
            foreach (var r in i.SocketList)
            {
                w.Put(r is not null);
                if (r is null) continue;
                w.Put(r.Id); w.Put(r.Kind); w.Put((byte)r.Tier); w.Put((byte)r.Level); w.Put(r.Xp); w.Put(r.XpToNext); w.Put((short)r.Value);
            }
        }
    }

    private static List<ItemInfo> GetItems(NetDataReader r)
    {
        int n = r.GetByte();
        var list = new List<ItemInfo>(n);
        for (int i = 0; i < n; i++)
        {
            long id = r.GetLong(); string name = r.GetString(); var slot = (ItemSlot)r.GetByte(); var rar = (Rarity)r.GetByte();
            int lvl = r.GetByte(), dmg = r.GetShort(), def = r.GetShort(); var wt = (WeaponType)r.GetByte(); bool forged = r.GetBool(); int value = r.GetInt();
            var props = new Dictionary<string, int>();
            for (int k = 0, pc = r.GetByte(); k < pc; k++) props[r.GetString()] = r.GetShort();
            int up = r.GetByte(); string trait = r.GetString(), set = r.GetString(); bool exalted = r.GetBool();
            var sockets = new List<RuneInfo?>();
            for (int k = 0, sc = r.GetByte(); k < sc; k++)
                sockets.Add(r.GetBool() ? new RuneInfo(r.GetLong(), r.GetString(), (Rarity)r.GetByte(), r.GetByte(), r.GetLong(), r.GetLong(), r.GetShort()) : null);
            list.Add(new ItemInfo(id, name, slot, rar, lvl, dmg, def, wt, props, forged, value, up, trait.Length == 0 ? null : trait, set.Length == 0 ? null : set, exalted, sockets));
        }
        return list;
    }

    public static ItemInfo Info(Item i) =>
        new(i.Id, i.DisplayName, i.Slot, i.Rarity, i.ItemLevel, i.Damage, i.Defense, i.Weapon, new Dictionary<string, int>(i.Properties), i.Forged, Loot.Value(i),
            i.Upgrade, i.Trait, i.Set, i.Exalted, Runes.SocketsOf(i).Select(r => r is null ? null : RuneInfo.From(r)).ToList());

    public static void Command(NetDataWriter w, string verb, params string[] args)
    {
        w.Reset(); w.Put((byte)MsgType.Command); w.Put(verb); w.Put((byte)args.Length);
        foreach (var a in args) w.Put(a);
    }

    public static (string Verb, string[] Args) ReadCommand(NetDataReader r)
    {
        string verb = r.GetString(32);
        var args = new string[System.Math.Min((int)r.GetByte(), 8)];
        for (int i = 0; i < args.Length; i++) args[i] = r.GetString(128);
        return (verb, args);
    }

    public static void Ui(NetDataWriter w, string kind, string json)
    {
        w.Reset(); w.Put((byte)MsgType.Ui); w.Put(kind); w.Put(json);
    }

    public static void LearnSkill(NetDataWriter w, string skillId)
    {
        w.Reset(); w.Put((byte)MsgType.LearnSkill); w.Put(skillId);
    }

    public const int MaxChatLength = 200;

    public static void ChatSend(NetDataWriter w, string channel, string text)
    {
        w.Reset(); w.Put((byte)MsgType.ChatSend); w.Put(channel); w.Put(text.Length > MaxChatLength ? text[..MaxChatLength] : text);
    }

    public static void Chat(NetDataWriter w, ChatLine line)
    {
        w.Reset(); w.Put((byte)MsgType.Chat); w.Put(line.Channel); w.Put(line.From); w.Put(line.Text);
    }

    public static ChatLine ReadChat(NetDataReader r) => new(r.GetString(16), r.GetString(32), r.GetString(MaxChatLength));

    public static PlayerStats ReadStats(NetDataReader r)
    {
        int level = r.GetInt(); long xp = r.GetLong(), next = r.GetLong();
        int gold = r.GetInt(), mana = r.GetInt(), maxMana = r.GetInt(), kills = r.GetInt(), deaths = r.GetInt(), gd = r.GetInt(), gdef = r.GetInt();
        int n = r.GetUShort();
        var list = new List<SkillInfo>(n);
        for (int i = 0; i < n; i++) list.Add(new SkillInfo(r.GetString(), r.GetByte(), r.GetByte(), r.GetInt(), r.GetByte()));
        var equipped = GetItems(r);
        var inventory = GetItems(r);
        int bp = r.GetInt();
        int barCount = r.GetByte();
        var bar = new List<string>(barCount);
        for (int i = 0; i < barCount; i++) bar.Add(r.GetString());
        var weapon = (WeaponType)r.GetByte();
        float block = r.GetFloat();
        var quests = new List<QuestInfo>();
        for (int i = 0, qn = r.GetByte(); i < qn; i++) quests.Add(new QuestInfo(r.GetString(), r.GetBool()));
        var wps = new List<string>();
        for (int i = 0, wn = r.GetByte(); i < wn; i++) wps.Add(r.GetString());
        int scrolls = r.GetInt();
        var unlocks = new List<string>();
        for (int i = 0, un = r.GetByte(); i < un; i++) unlocks.Add(r.GetString());
        int hp = r.GetByte(), mp = r.GetByte();
        string guild = r.GetString();
        return new PlayerStats(level, xp, next, gold, mana, maxMana, kills, deaths, gd, gdef, list, equipped, inventory, bp, bar, weapon, block,
            quests, wps, scrolls, unlocks, hp, mp, guild);
    }

    public static void Roster(NetDataWriter w, IReadOnlyCollection<RosterEntry> roster)
    {
        w.Reset(); w.Put((byte)MsgType.Roster); w.Put((byte)roster.Count);
        foreach (var e in roster) { w.Put(e.Id); w.Put(e.Name); w.Put((byte)e.Class); }
    }

    public static List<RosterEntry> ReadRoster(NetDataReader r)
    {
        int n = r.GetByte();
        var list = new List<RosterEntry>(n);
        for (int i = 0; i < n; i++) list.Add(new RosterEntry(r.GetInt(), r.GetString(), (ClassId)r.GetByte()));
        return list;
    }

    public static void Reject(NetDataWriter w, string reason)
    {
        w.Reset(); w.Put((byte)MsgType.Reject); w.Put(reason);
    }
}
