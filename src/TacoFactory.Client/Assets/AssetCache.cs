using System.Collections.Concurrent;
using Silk.NET.OpenGL;
using TacoFactory.Client.Rendering;
using TacoFactory.Core.Content;

namespace TacoFactory.Client.Assets;

/// <summary>
/// Loads textures by asset key (e.g. "sprites/knight.png") and reloads them when the file changes on disk.
/// File events arrive on a worker thread; reloads happen on the render thread in <see cref="ProcessReloads"/>.
/// </summary>
public sealed class AssetCache : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _changed = new();
    private readonly Dictionary<string, int> _retries = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher? _watcher;

    public string Root { get; }
    public int ReloadCount { get; private set; }

    public AssetCache(GL gl, string root, bool watch = true)
    {
        _gl = gl;
        Root = root;
        if (!watch) return;
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, e) => _changed.Enqueue(e.FullPath);
        _watcher.Created += (_, e) => _changed.Enqueue(e.FullPath);
        _watcher.Renamed += (_, e) => _changed.Enqueue(e.FullPath);
    }

    /// <summary>Returns the texture for a key, loading it on first use. A missing file gets a magenta placeholder.</summary>
    public Texture2D Texture(string key)
    {
        if (_textures.TryGetValue(key, out var tex)) return tex;
        string path = Path.Combine(Root, key);
        tex = File.Exists(path)
            ? Texture2D.FromFile(_gl, key, path)
            : Texture2D.FromPixels(_gl, key, 2, 2, [255, 0, 255, 255, 40, 0, 40, 255, 40, 0, 40, 255, 255, 0, 255, 255]);
        if (!File.Exists(path)) Console.WriteLine($"[assets] missing {key}, using placeholder");
        _textures[key] = tex;
        return tex;
    }

    private readonly Dictionary<string, bool> _exists = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the asset file is on disk (cached, so it is cheap to ask every frame).</summary>
    public bool Exists(string key)
    {
        if (_textures.ContainsKey(key)) return true;
        if (!_exists.TryGetValue(key, out bool e)) _exists[key] = e = File.Exists(Path.Combine(Root, key));
        return e;
    }

    private readonly Dictionary<string, SpriteSheet?> _sheets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the animated sheet for a sprite key ("sprites/class_knight.png") if a matching .json exists, else null.
    /// Callers fall back to the single image, so art can move from stills to sheets one asset at a time.
    /// </summary>
    public SpriteSheet? Sheet(string pngKey)
    {
        if (_sheets.TryGetValue(pngKey, out var sheet)) return sheet;
        string json = Path.Combine(Root, Path.ChangeExtension(pngKey, ".json"));
        sheet = null;
        if (File.Exists(json))
        {
            try
            {
                var meta = SpriteSheet.LoadMeta(json);
                // The atlas may live beside the still ("class_knight_sheet.png"), leaving the still for portraits.
                string atlas = string.IsNullOrEmpty(meta.Image) ? pngKey
                    : (Path.GetDirectoryName(pngKey) is { Length: > 0 } dir ? dir.Replace('\\', '/') + "/" + meta.Image : meta.Image);
                sheet = new SpriteSheet(meta, Texture(atlas));
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
            { Console.WriteLine($"[assets] bad sheet {json}: {ex.Message}"); }
        }
        _sheets[pngKey] = sheet;
        return sheet;
    }

    /// <summary>Call once per frame on the render thread.</summary>
    public void ProcessReloads()
    {
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (_changed.TryDequeue(out var full)) pending.Add(full);
        foreach (string full in pending)
        {
            string key = ContentRoot.ToKey(Root, full);
            if (key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                // A sheet description changed or appeared: drop the cached sheet so the next draw re-reads it.
                _sheets.Remove(Path.ChangeExtension(key, ".png"));
                continue;
            }
            if (!_textures.TryGetValue(key, out var tex) || !File.Exists(full)) continue;
            try
            {
                tex.Reload(full);
                ReloadCount++;
                _retries.Remove(key);
                Console.WriteLine($"[assets] reloaded {key} ({tex.Width}x{tex.Height})");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
            {
                // The art tool is probably still writing the file. Try again next frame, give up after ~2 s.
                int n = _retries.GetValueOrDefault(key) + 1;
                _retries[key] = n;
                if (n < 120) _changed.Enqueue(full);
                else Console.WriteLine($"[assets] could not reload {key}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        foreach (var t in _textures.Values) t.Dispose();
        _textures.Clear();
    }
}
