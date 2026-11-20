using System.Text.Json;
using System.Text.Json.Serialization;
using TacoFactory.Core.Sim;

namespace TacoFactory.Core.Data;

/// <summary>A monster type, loaded from assets/data/monsters.json.</summary>
public sealed class MonsterDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Health { get; set; }
    public int Damage { get; set; }
    public float Speed { get; set; }
    public float AttackRange { get; set; } = 40;
    public float DetectRange { get; set; } = 320;
    public float AttackCooldown { get; set; } = 1.0f;
    public float Radius { get; set; } = 20;
    public bool Undead { get; set; }
    public string Sprite { get; set; } = "";
    /// <summary>Guards see through disguises only up close (infiltration zones).</summary>
    public bool Guard { get; set; }
}

/// <summary>A named boss-like monster built on a base monster, with an escort and a guaranteed drop.</summary>
public sealed class SuperuniqueDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Base { get; set; } = "";
    /// <summary>Optional sprite of its own (e.g. "sprites/boss_brother_anselm.png"); otherwise the base monster's is used.</summary>
    public string? Sprite { get; set; }
    public int Level { get; set; }
    public float HealthMul { get; set; } = 4;
    public float DamageMul { get; set; } = 1.8f;
    public float SpeedMul { get; set; } = 1.0f;
    public string Escort { get; set; } = "";
    public int EscortCount { get; set; }
    public string GuaranteedDrop { get; set; } = "";
    public string MinRarity { get; set; } = "Common";
    /// <summary>Cutscene shown to everyone present when it dies.</summary>
    public string? DeathCutscene { get; set; }
}

/// <summary>A monster placed by hand (the prologue courtyard), rather than rolled in packs.</summary>
public sealed class FixedSpawn
{
    public string Monster { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
}

public sealed class SpawnEntry
{
    public string Monster { get; set; } = "";
    public int Weight { get; set; }
    public int GroupMin { get; set; }
    public int GroupMax { get; set; }
}

/// <summary>A zone, loaded from assets/data/zones/&lt;id&gt;.json.</summary>
public sealed class ZoneDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Act { get; set; }
    public int LevelMin { get; set; }
    public int LevelMax { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float[] PlayerSpawn { get; set; } = [0, 0];
    public float SafeRadius { get; set; } = 600;
    public int PackCount { get; set; }
    public string RarityCap { get; set; } = "Legendary";
    public int GoldMin { get; set; }
    public int GoldMax { get; set; }
    public List<SpawnEntry> Spawns { get; set; } = [];
    public string? Superunique { get; set; }
    public float[]? SuperuniquePosition { get; set; }
    public string GroundTile { get; set; } = "tiles/ground_placeholder.png";
    public string Description { get; set; } = "";
    public bool IsTown { get; set; }
    /// <summary>A guild's private hall: safe like a town, one copy per guild, members only.</summary>
    public bool IsGuildHall { get; set; }
    public List<ExitDef> Exits { get; set; } = [];
    public float[]? Waypoint { get; set; }
    public ArenaDef? Arena { get; set; }
    public List<NpcDef> Npcs { get; set; } = [];
    /// <summary>Scenery placed on the map (props.json names). Props with a footprint block movement (World.Obstacles).</summary>
    public List<DecorDef> Decor { get; set; } = [];
    public string? Boss { get; set; }
    public float[]? BossPosition { get; set; }
    /// <summary>Cutscene shown the first time a character enters.</summary>
    public string? Cutscene { get; set; }
    /// <summary>Players arrive disguised (if they hold the unlock in <see cref="DisguiseUnlock"/>); guards only notice them up close.</summary>
    public bool Infiltration { get; set; }
    public string? DisguiseUnlock { get; set; }
    /// <summary>Allied Templars fighting beside the party: base count plus one per unlock in <see cref="AllyUnlocks"/> any member holds.</summary>
    public int Allies { get; set; }
    public List<string> AllyUnlocks { get; set; } = [];
    public string AllyMonster { get; set; } = "remnant_brother";
    public string Music { get; set; } = "";
    /// <summary>Floor label for multi-floor dungeons ("Floor 2 of 3").</summary>
    public string? Floor { get; set; }
    public List<FixedSpawn> FixedSpawns { get; set; } = [];
    /// <summary>The tutorial courtyard new characters start in.</summary>
    public bool Prologue { get; set; }
    /// <summary>Light level, colour grade and weather (client only). Null: plain daylight.</summary>
    public AmbienceDef? Ambience { get; set; }
    /// <summary>Lights placed on the map that belong to no prop (sconces, braziers, fires, shafts of daylight).</summary>
    public List<PlacedLight> Lights { get; set; } = [];

    // ---- endgame
    /// <summary>How many knights one copy holds. Default: 120 in towns, 6 elsewhere (20 at a world boss).</summary>
    public int? MaxPlayers { get; set; }
    public int PlayerLimit => MaxPlayers ?? (IsTown ? 120 : 6);
    /// <summary>An act's hub town (with a waypoint), as opposed to the rooms and camps off it.</summary>
    public bool IsHub => IsTown && !IsGuildHall && !IsPersonal && !Muster && Waypoint is not null;
    /// <summary>A copy for each knight (their cell).</summary>
    public bool IsPersonal { get; set; }
    /// <summary>The Proving Pit: ten floors of rising waves, scaled to the party.</summary>
    public bool ProvingPit { get; set; }
    /// <summary>A world boss arena: one shared copy, the boss's blows are split between everyone, allies can be revived.</summary>
    public bool WorldBoss { get; set; }
    /// <summary>Where knights gather for the world boss (the Herald ports everyone here into the fight).</summary>
    public bool Muster { get; set; }
    /// <summary>Knights above this zone's level are synced down to it (default for every fighting zone).</summary>
    public bool LevelSync { get; set; } = true;
    /// <summary>Open-world zones the Mist can take over: not towns, with a waypoint.</summary>
    public bool MistEligible => !IsTown && !Prologue && !ProvingPit && !WorldBoss && Waypoint is not null && Floor is null;
}

/// <summary>
/// How a zone looks and feels: the ambient light everything starts from (1 = full daylight), the colour grade, and
/// the air (embers, dust motes, snow, ash, fireflies, spores, rain). Read by the client only.
/// </summary>
public sealed class AmbienceDef
{
    public string Ambient { get; set; } = "#FFFFFF";
    public float Level { get; set; } = 1f;
    public string Tint { get; set; } = "#FFFFFF";
    public float Saturation { get; set; } = 1f;
    public float Contrast { get; set; } = 1.05f;
    public float Vignette { get; set; } = 0.35f;
    public float Bloom { get; set; } = 0.6f;
    /// <summary>embers, dust, snow, ash, fireflies, spores, rain, or empty.</summary>
    public string Particles { get; set; } = "";
    public float ParticleDensity { get; set; } = 1f;
    /// <summary>Radius (world units) of the warm light the heroes carry; 0 outdoors by day.</summary>
    public float HeroLight { get; set; }
    /// <summary>Drifting cloud shadows on the ground (outdoors), 0..1.</summary>
    public float Clouds { get; set; }
    /// <summary>Colour of footstep dust.</summary>
    public string Dust { get; set; } = "#8A7A64";
    /// <summary>Ambience loop (assets/sound/amb_&lt;name&gt;): wind, wind_cold, rain, fire, crypt, forest, camp, night, town, hall.</summary>
    public string Sound { get; set; } = "";
}

/// <summary>A point light: colour, reach in world units, strength (1 = lifts dark ground to full light) and flicker 0..1.</summary>
public sealed class LightDef
{
    public string Colour { get; set; } = "#FFB060";
    public float Radius { get; set; } = 220f;
    public float Intensity { get; set; } = 1f;
    public float Flicker { get; set; }
    /// <summary>Height of the flame above the base, in metres: where the glow and embers are drawn.</summary>
    public float Height { get; set; }
    /// <summary>Flame glow drawn at the light (candles, fires); false for plain light pools.</summary>
    public bool Flame { get; set; }
    /// <summary>Embers rising from it, per second.</summary>
    public float Embers { get; set; }
}

public sealed class PlacedLight
{
    public float X { get; set; }
    public float Y { get; set; }
    public LightDef Light { get; set; } = new();
}

/// <summary>A walk-through exit to another zone. Walking onto it travels.</summary>
public sealed class ExitDef
{
    public string To { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    /// <summary>The way is shut until the character holds this unlock (e.g. "act2").</summary>
    public string? RequiresUnlock { get; set; }
    public string? LockedText { get; set; }
}

/// <summary>One piece of scenery in a zone: a prop from props.json at a world position.</summary>
public sealed class DecorDef
{
    public string Prop { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    /// <summary>Mirror the sprite left-right for variety.</summary>
    public bool Flip { get; set; }
}

/// <summary>A scenery prop: a rendered sprite, the world height its image covers, and where its base sits in the image.</summary>
public sealed class PropDef
{
    public string Sprite { get; set; } = "";
    public float Metres { get; set; } = 4f;
    public float AnchorX { get; set; } = 0.5f;
    public float AnchorY { get; set; } = 0.75f;
    /// <summary>Solid ground footprint as ellipse half-widths [x, y] in world units (y already squashed to the view). Null = walk-through.</summary>
    public float[]? Footprint { get; set; }
    /// <summary>Light the prop gives off (candles, smouldering beams).</summary>
    public LightDef? Light { get; set; }
}

public sealed class ArenaDef
{
    public string Name { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public bool Contains(float x, float y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;
}

public sealed class NpcDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public List<string> Services { get; set; } = [];
    /// <summary>Quest that unlocks this NPC's services (they are present but refuse until then).</summary>
    public string? Unlock { get; set; }
    /// <summary>Quest (or story unlock) after which this NPC appears at all (e.g. Brother Thomas after his rescue).</summary>
    public string? HiddenUntil { get; set; }
    /// <summary>Things the NPC says when spoken to, one at a time in turn.</summary>
    public List<string> Lines { get; set; } = [];
    /// <summary>What the NPC says while their services are still locked.</summary>
    public string? LockedLine { get; set; }
    public string Sprite { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
}

public enum ObjectiveType { ClearZone, KillUnique, ReachPoint, EnterZone }

public sealed class QuestObjective
{
    public string Text { get; set; } = "";
    public ObjectiveType Type { get; set; }
    public string Target { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Radius { get; set; }
    public string? Hint { get; set; }
}

public sealed class QuestReward
{
    public int BuildPoints { get; set; }
    public string? Unlock { get; set; }
    public string? Item { get; set; }
    /// <summary>Further story unlocks (trust, allies, proof...).</summary>
    public List<string> Flags { get; set; } = [];
    public int Gold { get; set; }
}

public sealed class QuestDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Zone { get; set; } = "";
    public int Act { get; set; }
    public bool Optional { get; set; }
    public QuestObjective Objective { get; set; } = new();
    public QuestReward Reward { get; set; } = new();
    /// <summary>Who hands it out (main quests) and what they say when giving it.</summary>
    public string? Giver { get; set; }
    public string? Briefing { get; set; }
    /// <summary>Quest that must be done first (otherwise the previous main quest of the act).</summary>
    public string? After { get; set; }
}

public sealed class LevelFile
{
    public long[] XpToNext { get; set; } = [];
}

/// <summary>A short story scene: a title and a few lines, shown full screen. assets/data/cutscenes.json.</summary>
public sealed class CutsceneDef
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Image { get; set; } = "";
    public List<CutsceneLine> Lines { get; set; } = [];
}

public sealed class CutsceneLine
{
    public string Speaker { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>A choice put to the party (spare or kill a yielded boss). Each player keeps the outcome they voted for.</summary>
public sealed class ChoiceDef
{
    public string Prompt { get; set; } = "";
    public List<ChoiceOption> Options { get; set; } = [];
    public float Seconds { get; set; } = 30;
}

public sealed class ChoiceOption
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Story unlock given to each player who voted for this (or to everyone, for the winning option).</summary>
    public string Unlock { get; set; } = "";
    /// <summary>"kill": the boss dies (loot, quest credit). "spare": the boss leaves the field (quest credit, no loot).</summary>
    public string Outcome { get; set; } = "kill";
    public string Line { get; set; } = "";
}

/// <summary>One step of a boss fight, from bosses.json. Phases start when health falls below <see cref="Below"/>.</summary>
public sealed class BossPhaseDef
{
    public float Below { get; set; } = 1;
    public string Announce { get; set; } = "";
    public List<BossAbilityDef> Abilities { get; set; } = [];
    /// <summary>Damage and speed multipliers while this phase lasts.</summary>
    public float DamageMul { get; set; } = 1;
    public float SpeedMul { get; set; } = 1;
    /// <summary>Invulnerable while this phase's summoned adds live.</summary>
    public bool ShieldedByAdds { get; set; }
}

public sealed class BossAbilityDef
{
    /// <summary>summon, telegraph, nova, charge, heal, curse.</summary>
    public string Kind { get; set; } = "";
    public float Every { get; set; } = 8;
    public float First { get; set; } = 3;
    public string Monster { get; set; } = "";
    public string Name { get; set; } = "";
    public int Count { get; set; } = 1;
    public int Max { get; set; } = 6;
    public float Radius { get; set; } = 90;
    public float Delay { get; set; } = 1.2f;
    public float DamageMul { get; set; } = 3;
    public float Amount { get; set; }
    public string Text { get; set; } = "";
}

public sealed class BossDef
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<BossPhaseDef> Phases { get; set; } = [];
    /// <summary>At this health fraction the boss yields and the party chooses (see <see cref="Choice"/>).</summary>
    public float YieldAt { get; set; }
    public ChoiceDef? Choice { get; set; }
    public string? DeathCutscene { get; set; }
    /// <summary>Allies (by unlock) who arrive to help when the fight starts.</summary>
    public List<string> HelpedBy { get; set; } = [];
}

public sealed class GameData
{
    public Dictionary<string, MonsterDef> Monsters { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SuperuniqueDef> Superuniques { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ZoneDef> Zones { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PropDef> Props { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SkillRules SkillRules { get; private set; } = new();
    /// <summary>Skills in display order: weapons, shield, then the five spell fields.</summary>
    public List<SkillDef> SkillList { get; } = [];
    public Dictionary<string, SkillDef> Skills { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ClassSkillInfo> ClassSkills { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SpellDef> Spells { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<QuestDef> Quests { get; } = [];
    public Dictionary<string, CutsceneDef> Cutscenes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, BossDef> Bosses { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The Act I town, where new characters begin.</summary>
    public ZoneDef Town => TownOf(1);
    public ZoneDef TownOf(int act) =>
        Zones.Values.Where(z => z.IsHub && z.Act <= act).OrderByDescending(z => z.Act).ThenBy(z => z.Id).FirstOrDefault()
        ?? Zones.Values.First(z => z.IsTown && !z.IsGuildHall);
    public int Acts => Zones.Values.Max(z => z.Act);

    /// <summary>Stable index per monster id, used on the wire instead of strings.</summary>
    public List<string> MonsterIndex { get; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads everything under assets/data. Throws with the file name if any file is invalid.</summary>
    public static GameData Load(string assetRoot)
    {
        var data = new GameData();
        string dir = Path.Combine(assetRoot, "data");
        foreach (var m in Read<List<MonsterDef>>(Path.Combine(dir, "monsters.json")))
        {
            data.Monsters[m.Id] = m;
            data.MonsterIndex.Add(m.Id);
        }
        foreach (var s in Read<List<SuperuniqueDef>>(Path.Combine(dir, "superuniques.json")))
            data.Superuniques[s.Id] = s;
        var skillFile = Read<SkillFile>(Path.Combine(dir, "skills.json"));
        data.SkillRules = skillFile.Rules;
        foreach (var sk in skillFile.Skills) { data.SkillList.Add(sk); data.Skills[sk.Id] = sk; }
        foreach (var (cls, info) in skillFile.Classes) data.ClassSkills[cls] = info;
        foreach (var sp in Read<List<SpellDef>>(Path.Combine(dir, "spells.json"))) data.Spells[sp.Id] = sp;
        data.Quests.AddRange(Read<List<QuestDef>>(Path.Combine(dir, "quests.json")));
        if (File.Exists(Path.Combine(dir, "cutscenes.json")))
            foreach (var c in Read<List<CutsceneDef>>(Path.Combine(dir, "cutscenes.json"))) data.Cutscenes[c.Id] = c;
        if (File.Exists(Path.Combine(dir, "levels.json")))
            Sim.Progression.UseTable(Read<LevelFile>(Path.Combine(dir, "levels.json")).XpToNext);
        if (File.Exists(Path.Combine(dir, "bosses.json")))
            foreach (var b in Read<List<BossDef>>(Path.Combine(dir, "bosses.json"))) data.Bosses[b.Id] = b;
        if (File.Exists(Path.Combine(dir, "props.json")))
            foreach (var (id, prop) in Read<Dictionary<string, PropDef>>(Path.Combine(dir, "props.json"))) data.Props[id] = prop;
        foreach (string file in Directory.GetFiles(Path.Combine(dir, "zones"), "*.json").OrderBy(f => f))
        {
            var z = Read<ZoneDef>(file);
            data.Zones[z.Id] = z;
        }
        data.Validate();
        return data;
    }

    private static T Read<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
                   ?? throw new InvalidDataException("empty file");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)}: {ex.Message}", ex);
        }
    }

    private void Validate()
    {
        foreach (var sp in Spells.Values)
        {
            if (sp.Field != RelicField && (!Skills.TryGetValue(sp.Field, out var f) || f.Kind != SkillKind.SpellField))
                throw new InvalidDataException($"Spell {sp.Id}: unknown spell field {sp.Field}");
            if (sp.Field != RelicField && (sp.RequiredLevel < 1 || sp.RequiredLevel > SkillRules.MaxSkillLevel))
                throw new InvalidDataException($"Spell {sp.Id}: required level {sp.RequiredLevel} is outside 1..{SkillRules.MaxSkillLevel}");
        }
        foreach (var z in Zones.Values)
            foreach (var e in z.Exits)
                if (!Zones.ContainsKey(e.To)) throw new InvalidDataException($"Zone {z.Id}: exit to unknown zone {e.To}");
        if (!Zones.Values.Any(z => z.IsTown && !z.IsGuildHall)) throw new InvalidDataException("No town zone found.");
        foreach (var (cls, info) in ClassSkills)
            foreach (var s in info.Starting)
                if (!Skills.ContainsKey(s)) throw new InvalidDataException($"Class {cls}: unknown starting skill {s}");
        foreach (var s in Superuniques.Values)
        {
            if (!Monsters.ContainsKey(s.Base)) throw new InvalidDataException($"Superunique {s.Id}: unknown base {s.Base}");
            if (s.EscortCount > 0 && !Monsters.ContainsKey(s.Escort)) throw new InvalidDataException($"Superunique {s.Id}: unknown escort {s.Escort}");
        }
        foreach (var z in Zones.Values)
        {
            foreach (var sp in z.Spawns)
                if (!Monsters.ContainsKey(sp.Monster)) throw new InvalidDataException($"Zone {z.Id}: unknown monster {sp.Monster}");
            if (z.Superunique is not null && !Superuniques.ContainsKey(z.Superunique))
                throw new InvalidDataException($"Zone {z.Id}: unknown superunique {z.Superunique}");
            if (z.Boss is not null && !Superuniques.ContainsKey(z.Boss))
                throw new InvalidDataException($"Zone {z.Id}: unknown boss {z.Boss}");
            if (z.Cutscene is not null && !Cutscenes.ContainsKey(z.Cutscene))
                throw new InvalidDataException($"Zone {z.Id}: unknown cutscene {z.Cutscene}");
            foreach (var f in z.FixedSpawns)
                if (!Monsters.ContainsKey(f.Monster)) throw new InvalidDataException($"Zone {z.Id}: unknown fixed monster {f.Monster}");
            if (z.Allies > 0 && !Monsters.ContainsKey(z.AllyMonster))
                throw new InvalidDataException($"Zone {z.Id}: unknown ally monster {z.AllyMonster}");
        }
        foreach (var b in Bosses.Values)
        {
            if (!Superuniques.ContainsKey(b.Id)) throw new InvalidDataException($"Boss {b.Id}: no superunique with that id");
            foreach (var ph in b.Phases)
                foreach (var ab in ph.Abilities)
                    if (ab.Kind == "summon" && !Monsters.ContainsKey(ab.Monster)) throw new InvalidDataException($"Boss {b.Id}: unknown summon {ab.Monster}");
            if (b.DeathCutscene is not null && !Cutscenes.ContainsKey(b.DeathCutscene)) throw new InvalidDataException($"Boss {b.Id}: unknown cutscene {b.DeathCutscene}");
        }
        foreach (var q in Quests)
        {
            if (!Zones.ContainsKey(q.Zone)) throw new InvalidDataException($"Quest {q.Id}: unknown zone {q.Zone}");
            if (q.Objective.Type == ObjectiveType.KillUnique && !Superuniques.ContainsKey(q.Objective.Target))
                throw new InvalidDataException($"Quest {q.Id}: unknown unique {q.Objective.Target}");
        }
    }

    /// <summary>Build points to raise a skill by one level for a class (base cost × class multiplier, rounded up).</summary>
    public int LearnCost(string className, string skillId)
    {
        var skill = Skills[skillId];
        double mult = 1;
        if (ClassSkills.TryGetValue(className, out var info))
        {
            string group = skill.Kind switch { SkillKind.Weapon => "weapon", SkillKind.Shield => "shield", _ => skill.Id };
            mult = info.CostMultiplier.TryGetValue(skill.Id, out var m) ? m : info.CostMultiplier.GetValueOrDefault(group, 1);
        }
        return (int)System.Math.Ceiling(SkillRules.CostPerSkillLevel * mult);
    }

    /// <summary>Spells known at the given skill levels, in field order then unlock level.</summary>
    /// <summary>Relic arts (field "relic") are taught by trophy relics rather than build points; they come first on the bar.</summary>
    public const string RelicField = "relic";

    public List<SpellDef> KnownSpells(IReadOnlyDictionary<string, int> skillLevels, IEnumerable<string>? relics = null) =>
        Spells.Values.Where(sp => sp.Field == RelicField && relics?.Contains(sp.Id) == true).OrderBy(sp => sp.Id)
            .Concat(SkillList.Where(s => s.Kind == SkillKind.SpellField)
                .SelectMany(f => Spells.Values.Where(sp => sp.Field == f.Id && skillLevels.GetValueOrDefault(f.Id) >= sp.RequiredLevel).OrderBy(sp => sp.RequiredLevel)))
            .ToList();

    public SkillDef? SkillForWeapon(WeaponType type) =>
        type == Sim.WeaponType.None ? null : SkillList.FirstOrDefault(s => s.WeaponType == type.ToString());

    public int IndexOfMonster(string id) => MonsterIndex.FindIndex(m => string.Equals(m, id, StringComparison.OrdinalIgnoreCase));
}
