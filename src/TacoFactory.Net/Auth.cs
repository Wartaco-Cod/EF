using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiteNetLib.Utils;
using TacoFactory.Core.Sim;

namespace TacoFactory.Net;

/// <summary>What the player asks for at the login screen. Sent encrypted, never in the clear.</summary>
public sealed record AuthRequest(string Mode, string Account, string Password, string Key = "");

public static class AuthMode
{
    public const string Login = "login", Register = "register";
    /// <summary>Bots and quick-play: log in, or create the account if it does not exist (open servers only).</summary>
    public const string Auto = "auto";
}

public sealed record CharacterSummary(string Name, ClassId Class, int Level, string Where);

/// <summary>Sent after login and after every change to the character list.</summary>
public sealed record AccountView(bool Ok, string Message, string Account, bool Admin, List<CharacterSummary> Characters, int MaxCharacters);

/// <summary>The server's first message: who it is (a long-lived public key the client remembers) and a fresh nonce.</summary>
public sealed record ServerHello(byte[] PublicKey, byte[] Nonce, bool OpenRegistration, string ServerName);

/// <summary>
/// Login encryption. Each login does an ephemeral P-256 key agreement with the server's long-lived key, derives an
/// AES-256-GCM key with HKDF over both nonces, and seals the credentials. The client remembers the server's key the
/// first time (trust on first use) and refuses a server whose key changes.
/// </summary>
public static class AuthCrypto
{
    private static readonly byte[] Info = Encoding.ASCII.GetBytes("taco-factory-auth-v1");

    public static ECDiffieHellman NewKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static byte[] Nonce() => RandomNumberGenerator.GetBytes(16);

    private static byte[] DeriveKey(ECDiffieHellman mine, byte[] theirPublic, byte[] serverNonce, byte[] clientNonce)
    {
        using var theirs = ECDiffieHellman.Create();
        theirs.ImportSubjectPublicKeyInfo(theirPublic, out _);
        byte[] shared = mine.DeriveRawSecretAgreement(theirs.PublicKey);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, [.. serverNonce, .. clientNonce], Info);
    }

    /// <summary>Client side: returns (client public key, client nonce, sealed request).</summary>
    public static (byte[] PublicKey, byte[] Nonce, byte[] Sealed) Seal(ServerHello hello, AuthRequest request)
    {
        using var mine = NewKey();
        byte[] clientNonce = Nonce();
        byte[] key = DeriveKey(mine, hello.PublicKey, hello.Nonce, clientNonce);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(request);
        byte[] iv = RandomNumberGenerator.GetBytes(12), tag = new byte[16], cipher = new byte[plain.Length];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(iv, plain, cipher, tag);
        CryptographicOperations.ZeroMemory(plain);
        return (mine.ExportSubjectPublicKeyInfo(), clientNonce, [.. iv, .. tag, .. cipher]);
    }

    /// <summary>Server side: opens a sealed request, or returns null if it was tampered with.</summary>
    public static AuthRequest? Open(ECDiffieHellman serverKey, byte[] serverNonce, byte[] clientPublic, byte[] clientNonce, byte[] sealedBytes)
    {
        if (sealedBytes.Length < 28 || sealedBytes.Length > 4096 || clientNonce.Length != 16) return null;
        try
        {
            byte[] key = DeriveKey(serverKey, clientPublic, serverNonce, clientNonce);
            var iv = sealedBytes.AsSpan(0, 12);
            var tag = sealedBytes.AsSpan(12, 16);
            var cipher = sealedBytes.AsSpan(28);
            byte[] plain = new byte[cipher.Length];
            using (var gcm = new AesGcm(key, 16)) gcm.Decrypt(iv, cipher, tag, plain);
            return JsonSerializer.Deserialize<AuthRequest>(plain);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or ArgumentException) { return null; }
    }

    /// <summary>Short, readable fingerprint of a server key, shown to players if it ever changes.</summary>
    public static string Fingerprint(byte[] publicKey) => Convert.ToHexString(SHA256.HashData(publicKey))[..16];
}

/// <summary>Remembers each server's key on this PC (trust on first use).</summary>
public sealed class KnownServers
{
    private readonly string _path;
    private readonly Dictionary<string, string> _keys;

    public KnownServers(string path)
    {
        _path = path;
        try { _keys = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [] : []; }
        catch (JsonException) { _keys = []; }
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TemplarKnights", "known_servers.json");

    /// <summary>True if this key is the one remembered for the address (or the first one seen, which is then remembered).</summary>
    public bool Check(string address, byte[] publicKey)
    {
        string fp = AuthCrypto.Fingerprint(publicKey);
        if (_keys.TryGetValue(address, out var known)) return known == fp;
        _keys[address] = fp;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_keys, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
        return true;
    }
}

public static partial class Msg
{
    public static void Hello(NetDataWriter w)
    {
        w.Reset(); w.Put((byte)MsgType.Hello); w.Put(Protocol.Version);
    }

    public static void ServerHello(NetDataWriter w, ServerHello h)
    {
        w.Reset(); w.Put((byte)MsgType.ServerHello);
        w.PutBytesWithLength(h.PublicKey); w.PutBytesWithLength(h.Nonce); w.Put(h.OpenRegistration); w.Put(h.ServerName);
    }

    public static ServerHello ReadServerHello(NetDataReader r) =>
        new(r.GetBytesWithLength(), r.GetBytesWithLength(), r.GetBool(), r.GetString(64));

    public static void Auth(NetDataWriter w, byte[] publicKey, byte[] nonce, byte[] sealedBytes)
    {
        w.Reset(); w.Put((byte)MsgType.Auth);
        w.PutBytesWithLength(publicKey); w.PutBytesWithLength(nonce); w.PutBytesWithLength(sealedBytes);
    }

    public static void Account(NetDataWriter w, AccountView v)
    {
        w.Reset(); w.Put((byte)MsgType.Account); w.Put(JsonSerializer.Serialize(v));
    }

    public static AccountView? ReadAccount(NetDataReader r) => JsonSerializer.Deserialize<AccountView>(r.GetString());

    public static void CharacterAction(NetDataWriter w, MsgType type, string name, ClassId cls = default)
    {
        w.Reset(); w.Put((byte)type); w.Put(name); w.Put((byte)cls);
    }
}
