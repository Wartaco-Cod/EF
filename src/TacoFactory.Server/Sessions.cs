using LiteNetLib;
using TacoFactory.Core.Data;
using TacoFactory.Core.Sim;

namespace TacoFactory.Server;

/// <summary>One connected player. Owns the character's saved state between zones.</summary>
public sealed class Session
{
    public required NetPeer Peer { get; init; }
    public required string Name { get; init; }
    public required ClassId Class { get; init; }
    public required CharacterRecord Record { get; set; }
    public required Account Account { get; init; }
    /// <summary>Messages this second, by kind, for rate limits.</summary>
    public int InputsThisSecond, CommandsThisSecond, ChatsThisSecond, MessagesThisSecond;
    public double RateWindowStart;
    public double LastChatWarning;
    /// <summary>Counts zone entries so the client can drop snapshots from the zone it just left.</summary>
    public byte Visit;
    public Party Party { get; set; } = null!;
    public Instance? Instance { get; set; }
    public int EntityId { get; set; }
    public double ArrivedAt { get; set; }
    public double TownReadyAt { get; set; }
    /// <summary>Latest invitation waiting for /accept: kind is party, duel, trade or guild.</summary>
    public (string From, string Kind, double At)? Pending { get; set; }
    public List<Item> Stash { get; set; } = [];
    public List<Item> ShopStock { get; set; } = [];
    public double ShopStockAt { get; set; } = -1e9;
    public string? Guild { get; set; }
    public HashSet<string> Waypoints { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> QuestsDone { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Unlocks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Scrolls { get; set; }
    /// <summary>Which line each NPC said last, so they don't repeat themselves.</summary>
    public Dictionary<string, int> NpcLine { get; } = new();

    public Entity? Entity => Instance?.World.Get(EntityId);
}

/// <summary>A group of up to 6. Everyone starts in a party of one; zone instances belong to a party.</summary>
public sealed class Party
{
    private static int _next = 1;
    public int Id { get; } = _next++;
    public Session Leader { get; set; }
    public List<Session> Members { get; } = [];
    public Party(Session leader) { Leader = leader; Members.Add(leader); }
}

/// <summary>A running copy of a zone: the shared town, or one party's copy of a field or dungeon.</summary>
public sealed class Instance
{
    public required string Key { get; init; }
    public required World World { get; init; }
    public HashSet<Session> Sessions { get; } = [];
    public double EmptySince { get; set; }
    public bool ClearAnnounced { get; set; }
    public ZoneDef Zone => World.Zone;
    /// <summary>The party's verdict on a yielded boss, while it is being decided.</summary>
    public Vote? Vote { get; set; }
}

public sealed class Vote
{
    public required BossDef Boss { get; init; }
    public required ChoiceDef Choice { get; init; }
    public required double Deadline { get; init; }
    public Dictionary<Session, string> Votes { get; } = new();
}
