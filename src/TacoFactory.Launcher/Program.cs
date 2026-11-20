using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

// Templar Knights launcher: brings the game up to date from your server, then starts it.
//
//   TemplarKnightsLauncher                 uses launcher.json next to it (made on first run)
//   TemplarKnightsLauncher --server host   remembers this server
//   TemplarKnightsLauncher --offline       skips the update check
//
// The server publishes the current build with `--updates <folder>` (see docs/RELEASE.md). Only files whose
// SHA-256 differs are downloaded; each lands in a temporary file first and is moved into place when complete.

string home = AppContext.BaseDirectory;
string configPath = Path.Combine(home, "launcher.json");
var config = File.Exists(configPath)
    ? JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(configPath)) ?? new LauncherConfig()
    : new LauncherConfig();
bool offline = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--server" when i + 1 < args.Length: config.Server = args[++i]; break;
        case "--update-port" when i + 1 < args.Length: config.UpdatePort = int.Parse(args[++i]); break;
        case "--offline": offline = true; break;
    }
}
File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine("Templar Knights");
Console.WriteLine($"Server: {config.Server}");
// A launcher update downloaded last time is swapped in now that the old one is not running.
foreach (var pending in Directory.GetFiles(home, "*.launcher-new", SearchOption.TopDirectoryOnly))
{
    try { File.Move(pending, pending[..^".launcher-new".Length], overwrite: true); } catch (IOException) { }
}

if (!offline)
{
    try { await Update(config, home); }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or JsonException)
    {
        Console.WriteLine($"Could not check for updates ({e.Message}). Starting the installed version.");
    }
}

string exe = Path.Combine(home, OperatingSystem.IsWindows() ? "TemplarKnights.exe" : "TemplarKnights");
if (!File.Exists(exe)) exe = Path.Combine(home, "TemplarKnights.dll");
if (!File.Exists(exe)) { Console.WriteLine("The game is not installed here. Run the launcher with --server <address> once your server is up."); Pause(); return 1; }
var (host, port) = Split(config.Server);
var start = exe.EndsWith(".dll")
    ? new ProcessStartInfo("dotnet", $"\"{exe}\" --host {host} --port {port}")
    : new ProcessStartInfo(exe, $"--host {host} --port {port}");
start.WorkingDirectory = home;
start.UseShellExecute = false;
Process.Start(start);
return 0;

static async Task Update(LauncherConfig config, string home)
{
    var (host, _) = Split(config.Server);
    var baseUrl = $"http://{host}:{config.UpdatePort}";
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var manifest = JsonSerializer.Deserialize<UpdateManifest>(await http.GetStringAsync($"{baseUrl}/manifest.json"))
                   ?? throw new JsonException("empty manifest");
    var todo = new List<UpdateFile>();
    foreach (var f in manifest.Files)
    {
        if (f.Path.Contains("..") || Path.IsPathRooted(f.Path)) continue; // never write outside the game folder
        string local = Path.Combine(home, f.Path);
        if (File.Exists(local) && new FileInfo(local).Length == f.Size && Hash(local) == f.Sha256) continue;
        todo.Add(f);
    }
    if (todo.Count == 0) { Console.WriteLine($"Up to date (version {manifest.Version})."); return; }
    long total = todo.Sum(f => f.Size), done = 0;
    Console.WriteLine($"Updating to version {manifest.Version}: {todo.Count} file(s), {total / 1048576.0:F1} MB");
    string self = Path.GetFileName(Environment.ProcessPath ?? "");
    foreach (var f in todo)
    {
        string target = Path.Combine(home, f.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string tmp = target + ".download";
        await using (var src = await http.GetStreamAsync($"{baseUrl}/files/{Uri.EscapeDataString(f.Path).Replace("%2F", "/")}"))
        await using (var dst = File.Create(tmp))
            await src.CopyToAsync(dst);
        if (Hash(tmp) != f.Sha256) { File.Delete(tmp); throw new IOException($"{f.Path} arrived damaged; try again."); }
        // The running launcher cannot replace itself: it is swapped in on the next start.
        bool isSelf = string.Equals(Path.GetFileName(target), self, StringComparison.OrdinalIgnoreCase)
                      || f.Path.StartsWith("TemplarKnightsLauncher", StringComparison.OrdinalIgnoreCase);
        File.Move(tmp, isSelf ? target + ".launcher-new" : target, overwrite: true);
        done += f.Size;
        Console.Write($"\r  {done * 100 / Math.Max(1, total),3}%  {f.Path,-60}");
    }
    Console.WriteLine("\r  100%  done." + new string(' ', 60));
}

static string Hash(string path)
{
    using var s = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(s));
}

static (string Host, int Port) Split(string server)
{
    int colon = server.LastIndexOf(':');
    return colon > 0 && int.TryParse(server[(colon + 1)..], out var p) ? (server[..colon], p) : (server, 7777);
}

static void Pause()
{
    if (!Console.IsInputRedirected) { Console.WriteLine("Press Enter to close."); Console.ReadLine(); }
}

sealed class LauncherConfig
{
    public string Server { get; set; } = "127.0.0.1";
    public int UpdatePort { get; set; } = 7778;
}

sealed record UpdateFile(string Path, long Size, string Sha256);
sealed record UpdateManifest(string Version, DateTime BuiltUtc, List<UpdateFile> Files);
