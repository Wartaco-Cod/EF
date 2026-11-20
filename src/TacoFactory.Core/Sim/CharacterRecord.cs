using System.Text.Json;
using System.Text.RegularExpressions;

namespace TacoFactory.Core.Sim;

/// <summary>A saved character. The server owns these; clients never write them.</summary>
public sealed class CharacterRecord
{
    public string Name { get; set; } = "";
    public ClassId Class { get; set; }
    /// <summary>The account that owns this character (empty for characters made before accounts existed).</summary>
    public string? Account { get; set; }
    /// <summary>Where the character was last seen, for the character select screen.</summary>
    public string? LastZone { get; set; }
    public int Level { get; set; } = 1;
    public long Xp { get; set; }
    public int Gold { get; set; }
    public List<Item> Equipped { get; set; } = [];
    public List<Item> Inventory { get; set; } = [];
    public Dictionary<string, int[]> Mastery { get; set; } = new(); // skill -> [level, hits, scrollLevels]
    public Dictionary<string, int> Skills { get; set; } = new();
    public int BuildPoints { get; set; }
    public List<string> QuestsDone { get; set; } = [];
    public List<string> Waypoints { get; set; } = [];
    public List<string> Unlocks { get; set; } = [];
    public int Scrolls { get; set; }
    public List<Item> Stash { get; set; } = [];
    public int HealthPotions { get; set; } = 3;
    public int ManaPotions { get; set; } = 2;
    public string? Guild { get; set; }
    /// <summary>Gold from auction sales made while offline, paid on next login.</summary>
    public int PendingGold { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public DateTime SavedUtc { get; set; }
    /// <summary>Runes, wallet, research, set collection, loadouts and armoury, pets, dailies (0.8).</summary>
    public PlayerExtras? Extras { get; set; }

    public static CharacterRecord From(Entity p) => new()
    {
        Name = p.Name, Class = p.Class, Level = p.Level, Xp = p.Xp, Gold = p.Gold,
        Equipped = p.Equipped.Values.ToList(), Inventory = p.Inventory.ToList(),
        Mastery = p.Mastery.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.Level, kv.Value.Hits, kv.Value.ScrollLevels }),
        Skills = new Dictionary<string, int>(p.Skills), BuildPoints = p.BuildPoints,
        HealthPotions = p.HealthPotions, ManaPotions = p.ManaPotions,
        Kills = p.Kills, Deaths = p.Deaths, SavedUtc = DateTime.UtcNow, Extras = p.Extras,
    };

    /// <summary>Copies saved progress onto a freshly added player (who has class starting skills and gear).</summary>
    public void ApplyTo(Entity p)
    {
        p.Xp = Xp;
        p.Gold = Gold;
        p.Kills = Kills;
        p.Deaths = Deaths;
        p.HealthPotions = HealthPotions;
        p.ManaPotions = ManaPotions;
        if (Equipped.Count > 0) p.Equipped.Clear();
        foreach (var i in Equipped) p.Equipped[i.Slot] = i;
        p.Inventory.AddRange(Inventory);
        p.Extras = Extras ?? new PlayerExtras();
        // Items saved before rune sockets get their empty sockets now.
        foreach (var i in p.Equipped.Values.Concat(p.Inventory).Concat(p.Extras.Armoury)) Runes.SocketsOf(i);
        foreach (var i in p.Equipped.Values.Concat(p.Inventory)) p.Extras.Note(i);
        if (Skills.Count > 0)
        {
            // Saves from before the skill system have no skills: those keep the class starting skills
            // and are given the build points their level would have earned.
            p.Skills.Clear();
            foreach (var (k, v) in Skills) p.Skills[k] = v;
            p.BuildPoints = BuildPoints;
        }
        else p.BuildPoints = System.Math.Max(0, Level - 1);
        foreach (var (skill, v) in Mastery)
            if (v.Length == 3) p.Mastery[skill] = new SkillMastery(v[0], v[1], v[2]);
    }
}

/// <summary>Stores one JSON file per character on the server machine, written atomically.</summary>
public sealed partial class CharacterStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _dir;

    public CharacterStore(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9 _-]{2,15}$")]
    private static partial Regex NameRule();

    public static bool IsValidName(string name) => NameRule().IsMatch(name);

    private string PathFor(string name) => Path.Combine(_dir, name.ToLowerInvariant().Replace(' ', '_') + ".json");

    public CharacterRecord? Load(string name)
    {
        string path = PathFor(name);
        return File.Exists(path) ? JsonSerializer.Deserialize<CharacterRecord>(File.ReadAllText(path), Json) : null;
    }

    /// <summary>Deleted characters are moved aside to saves/deleted, never destroyed outright.</summary>
    public void Delete(string name)
    {
        string path = PathFor(name);
        if (!File.Exists(path)) return;
        string dir = Path.Combine(_dir, "deleted");
        Directory.CreateDirectory(dir);
        File.Move(path, Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)}-{DateTime.UtcNow:yyyyMMddHHmmss}.json"), overwrite: true);
    }

    public void Save(CharacterRecord record)
    {
        string path = PathFor(record.Name), tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(record, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
