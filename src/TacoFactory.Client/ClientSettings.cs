using System.Text.Json;

namespace TacoFactory.Client;

/// <summary>Per-PC game settings, saved to the user's application data folder.</summary>
public sealed class ClientSettings
{
    public bool Fullscreen { get; set; }
    public bool VSync { get; set; } = true;
    /// <summary>0 normal, 1 large, 2 larger.</summary>
    public int TextSize { get; set; }
    public bool DamageNumbers { get; set; } = true;
    public bool Minimap { get; set; } = true;
    public bool ShowFps { get; set; }
    public string LastServer { get; set; } = "127.0.0.1";
    public string LastAccount { get; set; } = "";
    public float MusicVolume { get; set; } = 0.6f;
    public float EffectsVolume { get; set; } = 0.8f;
    public bool Hints { get; set; } = true;
    /// <summary>Dynamic lighting, bloom, colour grading, particles and screen shake.</summary>
    public bool Effects { get; set; } = true;
    public List<string> SeenHints { get; set; } = [];

    public static readonly string[] TextSizeNames = ["Normal", "Large", "Larger"];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TemplarKnights", "settings.json");

    public static ClientSettings Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(path)) is { } s)
            {
                s.TextSize = Math.Clamp(s.TextSize, 0, TextSizeNames.Length - 1);
                return s;
            }
        }
        catch (Exception e) { Console.Error.WriteLine($"[client] settings unreadable, using defaults: {e.Message}"); }
        return new ClientSettings();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) { Console.Error.WriteLine($"[client] could not save settings: {e.Message}"); }
    }
}
