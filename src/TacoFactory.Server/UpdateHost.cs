using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TacoFactory.Server;

/// <summary>One file of the game as the launcher sees it.</summary>
public sealed record UpdateFile(string Path, long Size, string Sha256);

public sealed record UpdateManifest(string Version, DateTime BuiltUtc, List<UpdateFile> Files);

/// <summary>
/// Serves game updates over plain HTTP so the launcher on each player's PC can stay in step with the server:
/// GET /manifest.json lists every file with its SHA-256, GET /files/&lt;path&gt; downloads one. Read-only, GET only,
/// nothing outside the update folder. Start the server with --updates &lt;folder of the released game&gt;.
/// </summary>
public sealed class UpdateHost : IDisposable
{
    private readonly string _root;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private byte[] _manifest = [];
    public UpdateManifest Manifest { get; private set; } = new("", DateTime.MinValue, []);

    public UpdateHost(string folder, int port)
    {
        _root = Path.GetFullPath(folder);
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException($"Update folder not found: {_root}");
        Refresh();
        _listener = new TcpListener(IPAddress.Any, port);
    }

    /// <summary>Re-reads the folder (after you copy a new build into it).</summary>
    public void Refresh()
    {
        var files = new List<UpdateFile>();
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(_root, f).Replace('\\', '/');
            if (rel == "manifest.json" || rel.StartsWith("saves/") || rel.EndsWith(".pdb")) continue;
            using var s = File.OpenRead(f);
            files.Add(new UpdateFile(rel, s.Length, Convert.ToHexString(SHA256.HashData(s))));
        }
        string version = File.Exists(Path.Combine(_root, "version.txt")) ? File.ReadAllText(Path.Combine(_root, "version.txt")).Trim() : "dev";
        Manifest = new UpdateManifest(version, DateTime.UtcNow, files);
        _manifest = JsonSerializer.SerializeToUtf8Bytes(Manifest, new JsonSerializerOptions { WriteIndented = true });
    }

    private DateTime _checked = DateTime.MinValue, _newest = DateTime.MinValue;
    private readonly object _gate = new();

    /// <summary>Picks up a new build copied into the folder (checked at most every 10 seconds).</summary>
    private void RefreshIfChanged()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _checked < TimeSpan.FromSeconds(10)) return;
            _checked = DateTime.UtcNow;
            var newest = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Max(f => File.GetLastWriteTimeUtc(f));
            if (newest > _newest) { _newest = newest; Refresh(); }
        }
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => Handle(client));
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            client.ReceiveTimeout = client.SendTimeout = 15000;
            var stream = client.GetStream();
            try
            {
                string request = await ReadHead(stream);
                var first = request.Split("\r\n")[0].Split(' ');
                if (first.Length < 2 || first[0] != "GET") { await Respond(stream, 405, "text/plain", "GET only"u8.ToArray()); return; }
                string path = Uri.UnescapeDataString(first[1].Split('?')[0]);
                if (path == "/manifest.json") { RefreshIfChanged(); await Respond(stream, 200, "application/json", _manifest); return; }
                if (path.StartsWith("/files/"))
                {
                    string rel = path["/files/".Length..];
                    string full = Path.GetFullPath(Path.Combine(_root, rel));
                    bool listed = Manifest.Files.Any(f => f.Path == rel);
                    if (!listed || !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
                    { await Respond(stream, 404, "text/plain", "not found"u8.ToArray()); return; }
                    await using var fs = File.OpenRead(full);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {fs.Length}\r\nConnection: close\r\n\r\n"));
                    await fs.CopyToAsync(stream);
                    return;
                }
                await Respond(stream, 404, "text/plain", "not found"u8.ToArray());
            }
            catch (Exception) { /* client went away */ }
        }
    }

    private static async Task<string> ReadHead(NetworkStream s)
    {
        var buf = new byte[8192];
        int n = 0;
        while (n < buf.Length)
        {
            int r = await s.ReadAsync(buf.AsMemory(n));
            if (r <= 0) break;
            n += r;
            if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n")) break;
        }
        return Encoding.ASCII.GetString(buf, 0, n);
    }

    private static async Task Respond(NetworkStream s, int code, string type, byte[] body)
    {
        string status = code switch { 200 => "OK", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
        await s.WriteAsync(body);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
