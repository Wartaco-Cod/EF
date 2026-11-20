using TacoFactory.Core.Data;
using TacoFactory.Core.Math;

namespace TacoFactory.Core.Sim;

public enum EntityKind : byte { Player = 0, Monster = 1, Loot = 2, Npc = 3, Chest = 4 }

[Flags]
public enum EntityFlags : ushort
{
    None = 0, Dead = 1, Superunique = 2, Undead = 4, Slowed = 8, Invulnerable = 16, Ally = 32, Boss = 64, Dueling = 128,
    /// <summary>A player in disguise (infiltration zones): guards do not notice them from afar.</summary>
    Disguised = 256,
    /// <summary>A monster that has raised the alarm.</summary>
    Alerted = 512,
    /// <summary>A boss that has yielded and waits for the party's verdict.</summary>
    Yielded = 1024,
}

public enum MonsterState : byte { Idle, Chase, Return }

/// <summary>Anything in a zone. One class keeps the snapshot code simple; kind-specific fields sit at the bottom.</summary>
public sealed class Entity
{
    public int Id { get; init; }
    public EntityKind Kind { get; init; }
    public Vec2 Pos { get; set; }
    public float Radius { get; set; } = 20;
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public float Mana { get; set; }
    public int MaxMana { get; set; }
    public int Level { get; set; } = 1;
    public EntityFlags Flags { get; set; }
    public bool Dead => (Flags & EntityFlags.Dead) != 0;

    // ---- Player ----
    public string Name { get; set; } = "";
    public ClassId Class { get; set; }
    public long Xp { get; set; }
    /// <summary>All experience ever earned (balance runs).</summary>
    public long LifetimeXp { get; set; }
    public int Gold { get; set; }
    public PlayerInput Input { get; set; }
    public const int BarSlots = 8; // slot 0 = weapon attack, slots 1-7 = spells
    public float[] SkillCooldowns { get; } = new float[BarSlots];
    /// <summary>Mastery per skill id (one_handed_sword, alchemy, ...), 0 to 100, earned by use.</summary>
    public Dictionary<string, SkillMastery> Mastery { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Learned skill levels, 0 to 10, bought with build points.</summary>
    public Dictionary<string, int> Skills { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int BuildPoints { get; set; }
    /// <summary>Spells on bar slots 1-7, rebuilt whenever a spell field level changes.</summary>
    public List<string> SpellBar { get; } = [];
    public List<Buff> Buffs { get; } = [];
    public int SkillLevel(string id) => Skills.GetValueOrDefault(id);
    public float BuffDamage => Buffs.Sum(b => b.Damage);
    public float BuffDefense => Buffs.Sum(b => b.Defense);
    public Dictionary<ItemSlot, Item> Equipped { get; } = new();
    public List<Item> Inventory { get; } = [];
    public float RespawnTimer { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }

    // ---- Monster ----
    public MonsterDef? Def { get; set; }
    public int TypeIndex { get; set; }
    public string DisplayName { get; set; } = "";
    public int Damage { get; set; }
    public float Speed { get; set; }
    public Vec2 Home { get; set; }
    public MonsterState State { get; set; }
    public int TargetId { get; set; }
    public float AttackTimer { get; set; }
    public float SlowTimer { get; set; }
    public Item? GuaranteedDrop { get; set; }
    public string? UniqueId { get; set; }
    public List<Item> ExtraDrops { get; } = [];
    /// <summary>Strengthened by the Mist.</summary>
    public bool Mist { get; set; }

    // ---- Loot ----
    public Item? Item { get; set; }
    public int GoldAmount { get; set; }

    public SkillMastery MasteryFor(string skillId)
    {
        if (!Mastery.TryGetValue(skillId, out var m)) Mastery[skillId] = m = new SkillMastery();
        return m;
    }

    /// <summary>Weapon and gear damage, including strength from runes, traits and set bonuses, scaled by level sync.</summary>
    public int GearDamage => (int)((Equipped.Values.Sum(i => i.Damage + 3 * i.Prop("strength")) + 3 * SetBonus.GetValueOrDefault("strength")) * GearScale);

    // ---- endgame
    /// <summary>Saved endgame progress: runes, wallet, research, collection, loadouts, pets, dailies.</summary>
    public PlayerExtras Extras { get; set; } = new();
    /// <summary>Level the character is synced down to in a lower zone (0 = not synced).</summary>
    public int SyncLevel { get; set; }
    /// <summary>Share of gear power kept while synced (1 = full).</summary>
    public float GearScale { get; set; } = 1;
    public int EffectiveLevel => SyncLevel > 0 ? System.Math.Min(SyncLevel, Level) : Level;
    /// <summary>Set bonus properties of what is worn, refreshed with the character's stats.</summary>
    public Dictionary<string, int> SetBonus { get; set; } = new();
    /// <summary>Index of the companion following this knight (0 = none), for snapshots.</summary>
    public int PetIndex { get; set; }
    public int HealthPotions { get; set; }
    public int ManaPotions { get; set; }
    public float PotionCooldown { get; set; }
    public int GearDefense => (int)(Equipped.Values.Sum(i => i.Defense) * GearScale);
}

/// <summary>A timed effect from a spell. Same spell recast refreshes instead of stacking.</summary>
public sealed class Buff
{
    public required string SpellId { get; init; }
    public float Damage { get; init; }
    public float Defense { get; init; }
    public float Remaining { get; set; }
}

public enum PlayerAction : byte { None = 0, UseSkill = 1 }

/// <summary>What a client asks its character to do this tick. The server decides what actually happens.</summary>
public readonly record struct PlayerInput(Vec2 Move, PlayerAction Action, byte SkillSlot, int TargetId, Vec2 TargetPos);
