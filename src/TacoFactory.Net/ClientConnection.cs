using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;

namespace TacoFactory.Net;

/// <summary>An entity as the client knows it: the last two snapshots, for smooth interpolation.</summary>
public sealed class RemoteEntity
{
    public EntityState Current;
    public EntityState Previous;
    public double ReceivedAt;
    public uint Tick;

    public Vec2 PositionAt(double now, double tickSeconds)
    {
        float t = (float)System.Math.Clamp((now - ReceivedAt) / tickSeconds, 0, 1);
        return Vec2.Lerp(Previous.Pos, Current.Pos, t);
    }
}

/// <summary>
/// Client side of the protocol, used by the game client and by headless bots. Call <see cref="Poll"/> every frame.
/// </summary>
public sealed class ClientConnection : IDisposable
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly NetDataWriter _w = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private NetPeer? _server;
    private uint _inputSeq;
    private uint _frameTick;
    private int _frameChunks, _frameChunkCount;
    private readonly HashSet<int> _frameSeen = [];

    /// <summary>The character in play (set when entering the world).</summary>
    public string Name { get; private set; } = "";
    public ClassId Class { get; private set; }
    /// <summary>Quick-play: log in (or register on open servers) and play this character, creating it if needed.</summary>
    private readonly (string Name, ClassId Class)? _auto;
    private string _address = "";
    private bool _entering;
    public KnownServers? Known { get; set; }
    public ServerHello? Hello { get; private set; }
    public AccountView? Account { get; private set; }
    /// <summary>The latest message for the login and character screens.</summary>
    public string? AccountMessage { get; private set; }
    public bool InWorld => Welcome is not null;
    public WelcomeInfo? Welcome { get; private set; }
    public int YourId => Welcome?.YourId ?? 0;
    public Dictionary<int, RemoteEntity> Entities { get; } = new();
    public Dictionary<int, RosterEntry> Roster { get; } = new();
    public PlayerStats? Stats { get; private set; }
    public Queue<SimEvent> Events { get; } = new();
    public Queue<ChatLine> Chat { get; } = new();
    public List<PartyMember> Party { get; private set; } = [];
    /// <summary>Window updates from the server (kind, JSON), handled by the game each frame.</summary>
    public Queue<(string Kind, string Json)> Ui { get; } = new();
    /// <summary>Counts zone changes; the game uses it to reset effects when the world changes.</summary>
    public int ZoneGeneration { get; private set; }
    public string? RejectReason { get; private set; }
    public bool Connected => _server?.ConnectionState == ConnectionState.Connected;
    public bool Failed { get; private set; }
    public double Now => _clock.Elapsed.TotalSeconds;
    public int Ping => _server?.Ping ?? 0;

    /// <summary>Quick-play connection for bots and tests: account and character are both <paramref name="name"/>.</summary>
    public ClientConnection(string name, ClassId cls) : this()
    {
        Name = name;
        Class = cls;
        _auto = (name, cls);
    }

    /// <summary>A connection that waits at the login screen.</summary>
    public ClientConnection()
    {
        _net = new NetManager(_listener) { DisconnectTimeout = 10000, AutoRecycle = true };
        _listener.PeerConnectedEvent += peer =>
        {
            Msg.Hello(_w);
            peer.Send(_w, DeliveryMethod.ReliableOrdered);
        };
        _listener.PeerDisconnectedEvent += (_, info) =>
        {
            Failed = true;
            if (RejectReason is null && info.AdditionalData.AvailableBytes > 0)
            {
                try { RejectReason = info.AdditionalData.GetString(); } catch (Exception) { }
            }
            RejectReason ??= info.Reason switch
            {
                DisconnectReason.ConnectionFailed => "Could not reach the server. Check the address and that it is running.",
                DisconnectReason.Timeout => "Lost contact with the server.",
                DisconnectReason.ConnectionRejected => "The server is full or turned us away.",
                _ => info.Reason.ToString(),
            };
        };
        _listener.NetworkReceiveEvent += (_, reader, _, _) => Receive(reader);
        _listener.NetworkErrorEvent += (_, err) => { Failed = true; RejectReason ??= err.ToString(); };
    }

    public void Connect(string host, int port)
    {
        _address = $"{host}:{port}";
        _net.Start();
        _server = _net.Connect(host, port, Protocol.ConnectKey);
    }

    public void Poll() => _net.PollEvents();

    public void SendInput(PlayerInput input)
    {
        if (!Connected || Welcome is null) return;
        Msg.Input(_w, ++_inputSeq, input);
        _server!.Send(_w, DeliveryMethod.Sequenced);
    }

    private void Receive(NetDataReader r)
    {
        var type = (MsgType)r.GetByte();
        switch (type)
        {
            case MsgType.ServerHello:
                Hello = Msg.ReadServerHello(r);
                if (Known is not null && !Known.Check(_address, Hello.PublicKey))
                {
                    RejectReason = $"This server's identity changed (now {AuthCrypto.Fingerprint(Hello.PublicKey)}). " +
                                   "If its owner moved or rebuilt it, remove it from known_servers.json and connect again.";
                    Failed = true;
                    _net.DisconnectAll();
                    break;
                }
                if (_auto is { } auto) SendAuth(new AuthRequest(AuthMode.Auto, QuickAccount(auto.Name), "quickplay:" + auto.Name));
                break;
            case MsgType.Account:
                if (Welcome is not null)
                {
                    // Logged out to character select: forget the world.
                    Welcome = null;
                    Entities.Clear();
                    Roster.Clear();
                    Stats = null;
                    Party = [];
                    ZoneGeneration++;
                }
                Account = Msg.ReadAccount(r);
                AccountMessage = Account?.Message;
                if (_auto is not null && Account is { Ok: false } refused) { RejectReason = refused.Message; Failed = true; break; }
                if (_auto is { } a && Account is { Ok: true } acc && Welcome is null && !_entering)
                {
                    if (acc.Characters.All(c => !string.Equals(c.Name, a.Name, StringComparison.OrdinalIgnoreCase))) CreateCharacter(a.Name, a.Class);
                    else EnterWorld(acc.Characters.First(c => string.Equals(c.Name, a.Name, StringComparison.OrdinalIgnoreCase)).Name);
                }
                break;
            case MsgType.Welcome:
                _entering = false;
                Welcome = Msg.ReadWelcome(r);
                // A new zone: its tick counter starts again and nothing from the old zone applies.
                Entities.Clear();
                _frameTick = 0;
                _frameSeen.Clear();
                ZoneGeneration++;
                break;
            case MsgType.Party: Party = Msg.ReadParty(r); break;
            case MsgType.Ui: Ui.Enqueue((r.GetString(), r.GetString())); break;
            case MsgType.Snapshot: ReadSnapshot(r); break;
            case MsgType.Event: Events.Enqueue(Msg.ReadEvent(r)); break;
            case MsgType.Stats: Stats = Msg.ReadStats(r); break;
            case MsgType.Roster:
                Roster.Clear();
                foreach (var e in Msg.ReadRoster(r)) Roster[e.Id] = e;
                break;
            case MsgType.Reject: RejectReason = r.GetString(); Failed = true; break;
            case MsgType.Chat: Chat.Enqueue(Msg.ReadChat(r)); break;
        }
    }

    private void ReadSnapshot(NetDataReader r)
    {
        byte visit = r.GetByte();
        if (Welcome is null || visit != Welcome.Visit) return; // still in flight from the zone we just left
        uint tick = r.GetUInt();
        ushort chunk = r.GetUShort(), chunkCount = r.GetUShort(), count = r.GetUShort();
        if (tick < _frameTick) return; // late packet from an older tick
        if (tick > _frameTick)
        {
            _frameTick = tick;
            _frameChunks = 0;
            _frameChunkCount = chunkCount;
            _frameSeen.Clear();
        }
        double now = Now;
        for (int i = 0; i < count; i++)
        {
            var s = EntityState.Read(r);
            _frameSeen.Add(s.Id);
            if (Entities.TryGetValue(s.Id, out var e))
            {
                e.Previous = e.Current;
                e.Current = s;
                e.ReceivedAt = now;
                e.Tick = tick;
            }
            else Entities[s.Id] = new RemoteEntity { Current = s, Previous = s, ReceivedAt = now, Tick = tick };
        }
        _ = chunk;
        // Once every chunk of a tick has arrived, anything not in it has left our view.
        if (++_frameChunks == _frameChunkCount)
        {
            List<int>? gone = null;
            foreach (var id in Entities.Keys) if (!_frameSeen.Contains(id)) (gone ??= []).Add(id);
            if (gone is not null) foreach (var id in gone) Entities.Remove(id);
        }
    }

    public void SendChat(string text, string channel = "group")
    {
        if (!Connected || Welcome is null || string.IsNullOrWhiteSpace(text)) return;
        Msg.ChatSend(_w, channel, text.Trim());
        _server!.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Generic request: equip, buy, sell, stash, forge, trade, auction and guild actions.</summary>
    public void Command(string verb, params string[] args)
    {
        if (!Connected || Welcome is null) return;
        Msg.Command(_w, verb, args);
        _server!.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    public void Travel(TravelKind kind, string zoneId = "")
    {
        if (!Connected || Welcome is null) return;
        Msg.Travel(_w, kind, zoneId);
        _server!.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    public void Interact(int entityId)
    {
        if (!Connected || Welcome is null) return;
        Msg.Interact(_w, entityId);
        _server!.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    public void UseScroll(string skillId)
    {
        if (!Connected || Welcome is null) return;
        Msg.UseScroll(_w, skillId);
        _server!.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    public void Learn(string skillId)
    {
        if (!Connected || Welcome is null) return;
        Msg.LearnSkill(_w, skillId);
        _server!.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    // ------------------------------------------------------------------ login and characters

    private void SendAuth(AuthRequest request)
    {
        if (!Connected || Hello is null) return;
        var (pub, nonce, sealedBytes) = AuthCrypto.Seal(Hello, request);
        Msg.Auth(_w, pub, nonce, sealedBytes);
        _server!.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>The account quick-play uses for a character name (account names have no spaces).</summary>
    public static string QuickAccount(string characterName) => characterName.Trim().Replace(' ', '_');

    public void Login(string account, string password) => SendAuth(new AuthRequest(AuthMode.Login, account.Trim(), password));

    public void Register(string account, string password, string key) => SendAuth(new AuthRequest(AuthMode.Register, account.Trim(), password, key.Trim()));

    public void CreateCharacter(string name, ClassId cls)
    {
        if (!Connected || Account is not { Ok: true }) return;
        Msg.CharacterAction(_w, MsgType.CreateCharacter, name.Trim(), cls);
        _server!.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    public void DeleteCharacter(string name)
    {
        if (!Connected || Account is not { Ok: true }) return;
        Msg.CharacterAction(_w, MsgType.DeleteCharacter, name);
        _server!.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    public void EnterWorld(string name)
    {
        if (!Connected || Account is not { Ok: true } acc) return;
        var c = acc.Characters.FirstOrDefault(x => x.Name == name);
        if (c is null) return;
        Name = c.Name;
        Class = c.Class;
        _entering = true;
        Msg.CharacterAction(_w, MsgType.EnterWorld, name);
        _server!.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    public void Disconnect() => _net.DisconnectAll();

    public void Dispose()
    {
        _net.DisconnectAll();
        _net.Stop();
    }
}
