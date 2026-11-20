using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TacoFactory.Server;

/// <summary>A player account: login, the characters it owns, and moderation state.</summary>
public sealed class Account
{
    public string Name { get; set; } = "";
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
    public int Iterations { get; set; } = AccountStore.Iterations;
    public List<string> Characters { get; set; } = [];
    public bool Admin { get; set; }
    public bool Banned { get; set; }
    public string? BanReason { get; set; }
    public DateTime? MutedUntilUtc { get; set; }
    public string? PurchaseKey { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }
}

/// <summary>
/// Accounts in saves/accounts.json. Passwords are stored as salted PBKDF2-SHA256 hashes, never as text.
/// The first account made on a server becomes its administrator.
/// </summary>
public sealed partial class AccountStore
{
    public const int Iterations = 120_000, MaxCharacters = 8, MinPassword = 8;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    private readonly Dictionary<string, Account> _accounts;

    public AccountStore(string directory)
    {
        _file = Path.Combine(directory, "accounts.json");
        _accounts = File.Exists(_file)
            ? (JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(_file)) ?? []).ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
    }

    public int Count => _accounts.Count;
    public IEnumerable<Account> All => _accounts.Values;

    [GeneratedRegex("^[A-Za-z0-9_.-]{3,24}$")]
    private static partial Regex NameRule();

    public static bool IsValidName(string name) => NameRule().IsMatch(name);

    public Account? Get(string name) => _accounts.GetValueOrDefault(name);

    public Account? OwnerOf(string character) =>
        _accounts.Values.FirstOrDefault(a => a.Characters.Any(c => string.Equals(c, character, StringComparison.OrdinalIgnoreCase)));

    public Account Create(string name, string password, string? key)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var a = new Account
        {
            Name = name, Salt = Convert.ToBase64String(salt), Hash = Convert.ToBase64String(HashOf(password, salt, Iterations)),
            Admin = _accounts.Count == 0, PurchaseKey = key, CreatedUtc = DateTime.UtcNow,
        };
        _accounts[name] = a;
        Save();
        return a;
    }

    public static bool Verify(Account a, string password) =>
        CryptographicOperations.FixedTimeEquals(HashOf(password, Convert.FromBase64String(a.Salt), a.Iterations), Convert.FromBase64String(a.Hash));

    private static byte[] HashOf(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    public void Save()
    {
        string tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_accounts.Values.ToList(), Json));
        File.Move(tmp, _file, overwrite: true);
    }
}

/// <summary>Purchase keys in saves/keys.json. Each key creates one account.</summary>
public sealed class KeyStore
{
    public sealed class Entry
    {
        public string Key { get; set; } = "";
        public string? UsedBy { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? UsedUtc { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    private readonly List<Entry> _keys;

    public KeyStore(string directory)
    {
        _file = Path.Combine(directory, "keys.json");
        _keys = File.Exists(_file) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_file)) ?? [] : [];
    }

    public int Unused => _keys.Count(k => k.UsedBy is null);

    public static string Normalize(string key) => key.Trim().ToUpperInvariant();

    /// <summary>Makes new keys like TK-7F3K-Q2MD-9XWB-HC4P (no easily confused letters).</summary>
    public List<string> Make(int count)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var made = new List<string>();
        for (int i = 0; i < count; i++)
        {
            string key = "TK-" + string.Join("-", Enumerable.Range(0, 4).Select(_ =>
                new string(Enumerable.Range(0, 4).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray())));
            _keys.Add(new Entry { Key = key, CreatedUtc = DateTime.UtcNow });
            made.Add(key);
        }
        Save();
        return made;
    }

    public bool IsUnused(string key) => _keys.Any(k => k.Key == Normalize(key) && k.UsedBy is null);

    public void Use(string key, string account)
    {
        var k = _keys.First(x => x.Key == Normalize(key));
        k.UsedBy = account;
        k.UsedUtc = DateTime.UtcNow;
        Save();
    }

    private void Save()
    {
        string tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_keys, Json));
        File.Move(tmp, _file, overwrite: true);
    }
}

/// <summary>The server's long-lived identity key (saves/server_key.pem). Clients remember it to detect impostors.</summary>
public static class ServerIdentity
{
    public static ECDiffieHellman LoadOrCreate(string directory)
    {
        string path = Path.Combine(directory, "server_key.pem");
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path)) key.ImportFromPem(File.ReadAllText(path));
        else
        {
            File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return key;
    }
}
