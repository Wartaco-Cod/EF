using System.Text.Json;
using System.Text.RegularExpressions;
using TacoFactory.Core.Sim;

namespace TacoFactory.Server;

/// <summary>An item for sale on the auction house. Buyout only; listings last 48 hours.</summary>
public sealed class Listing
{
    public long Id { get; set; }
    public string Seller { get; set; } = "";
    public Item Item { get; set; } = null!;
    public int Price { get; set; }
    public DateTime PostedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
}

/// <summary>Gold and items waiting for a character: auction sales and expired listings.</summary>
public sealed class Mail
{
    public int Gold { get; set; }
    public List<Item> Items { get; set; } = [];
}

/// <summary>
/// The server-wide auction house, saved to saves/auctions.json after every change.
/// Posting costs 5% of the price up front; the house keeps 10% of each sale.
/// </summary>
public sealed class AuctionHouse
{
    public const int PostingFeePercent = 5, SaleCutPercent = 10, MaxListingsPerSeller = 20, MaxPrice = 10_000_000;
    public static readonly TimeSpan Duration = TimeSpan.FromHours(48);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _file;
    public List<Listing> Listings { get; private set; } = [];
    public Dictionary<string, Mail> Mailbox { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Snapshot
    {
        public List<Listing> Listings { get; set; } = [];
        public Dictionary<string, Mail> Mailbox { get; set; } = new();
    }

    public AuctionHouse(string directory)
    {
        _file = Path.Combine(directory, "auctions.json");
        if (!File.Exists(_file)) return;
        var snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_file)) ?? new Snapshot();
        Listings = snap.Listings;
        Mailbox = new Dictionary<string, Mail>(snap.Mailbox, StringComparer.OrdinalIgnoreCase);
    }

    public void Save()
    {
        string tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Snapshot { Listings = Listings, Mailbox = new(Mailbox) }, Json));
        File.Move(tmp, _file, overwrite: true);
    }

    public static int PostingFee(int price) => System.Math.Max(1, price * PostingFeePercent / 100);

    public Mail MailFor(string name)
    {
        if (!Mailbox.TryGetValue(name, out var m)) Mailbox[name] = m = new Mail();
        return m;
    }

    /// <summary>Moves expired listings back to their sellers' mail. Returns true if anything changed.</summary>
    public bool Expire(DateTime nowUtc)
    {
        var expired = Listings.Where(l => l.ExpiresUtc <= nowUtc).ToList();
        foreach (var l in expired)
        {
            Listings.Remove(l);
            MailFor(l.Seller).Items.Add(l.Item);
        }
        return expired.Count > 0;
    }

    public IEnumerable<Listing> Search(string text, ItemSlot? slot, Rarity? minRarity, int minLevel, int maxLevel, string sort, bool desc)
    {
        var q = Listings.Where(l =>
            (text.Length == 0 || l.Item.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase) || l.Seller.Contains(text, StringComparison.OrdinalIgnoreCase))
            && (slot is null || l.Item.Slot == slot)
            && (minRarity is null || l.Item.Rarity >= minRarity)
            && l.Item.ItemLevel >= minLevel && l.Item.ItemLevel <= maxLevel);
        IOrderedEnumerable<Listing> sorted = sort switch
        {
            "name" => q.OrderBy(l => l.Item.DisplayName),
            "level" => q.OrderBy(l => l.Item.ItemLevel),
            "rarity" => q.OrderBy(l => l.Item.Rarity),
            "slot" => q.OrderBy(l => l.Item.Slot),
            "seller" => q.OrderBy(l => l.Seller),
            "ends" => q.OrderBy(l => l.ExpiresUtc),
            _ => q.OrderBy(l => l.Price),
        };
        var list = sorted.ThenBy(l => l.Price).ThenBy(l => l.Id).ToList();
        if (desc) list.Reverse();
        return list;
    }
}

public sealed class Guild
{
    public string Name { get; set; } = "";
    public string Leader { get; set; } = "";
    public string Motto { get; set; } = "";
    public List<string> Members { get; set; } = [];
    public List<string> Officers { get; set; } = [];
    public List<Item> Stash { get; set; } = [];
    public DateTime CreatedUtc { get; set; }
    public string RankOf(string name) =>
        string.Equals(name, Leader, StringComparison.OrdinalIgnoreCase) ? "Grand Master"
        : Officers.Contains(name, StringComparer.OrdinalIgnoreCase) ? "Preceptor" : "Brother";
}

/// <summary>Guilds ("Commanderies"), saved to saves/guilds.json after every change.</summary>
public sealed partial class GuildRegistry
{
    public const int CreateCost = 2000, MaxMembers = 100;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    public Dictionary<string, Guild> Guilds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public GuildRegistry(string directory)
    {
        _file = Path.Combine(directory, "guilds.json");
        if (!File.Exists(_file)) return;
        foreach (var g in JsonSerializer.Deserialize<List<Guild>>(File.ReadAllText(_file)) ?? []) Guilds[g.Name] = g;
    }

    public void Save()
    {
        string tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Guilds.Values.ToList(), Json));
        File.Move(tmp, _file, overwrite: true);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z' -]{2,23}$")]
    private static partial Regex NameRule();

    public static bool IsValidName(string name) => NameRule().IsMatch(name);

    public Guild? Of(string member) =>
        Guilds.Values.FirstOrDefault(g => g.Members.Contains(member, StringComparer.OrdinalIgnoreCase));
}

/// <summary>A trade between two players. Any change clears both acceptances; both must accept to swap.</summary>
public sealed class Trade
{
    public required Session A { get; init; }
    public required Session B { get; init; }
    public List<long> ItemsA { get; } = [];
    public List<long> ItemsB { get; } = [];
    public int GoldA { get; set; }
    public int GoldB { get; set; }
    public bool AcceptA { get; set; }
    public bool AcceptB { get; set; }

    public bool Involves(Session s) => s == A || s == B;
    public Session Other(Session s) => s == A ? B : A;
    public List<long> ItemsOf(Session s) => s == A ? ItemsA : ItemsB;
    public void ResetAccept() { AcceptA = false; AcceptB = false; }
}
