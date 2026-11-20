using System.Text.Json;

namespace TacoFactory.Core.Data;

public sealed class SkillRules
{
    public int MaxSkillLevel { get; set; } = 10;
    public int BuildPointsPerLevelUp { get; set; } = 1;
    public int CostPerSkillLevel { get; set; } = 4;
    public double UntrainedDamagePenalty { get; set; } = 0.30;
    public int[] SpellUnlockLevels { get; set; } = [1, 3, 5, 7, 9];
    public double MaxBlockChance { get; set; } = 0.40;
    public double BlockReduction { get; set; } = 0.75;
    public int StartingSkillLevel { get; set; } = 1;
}

public enum SkillKind { Weapon, Shield, SpellField }

/// <summary>One of the 11 learnable skills: 5 weapon skills, Shield, and 5 spell fields.</summary>
public sealed class SkillDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public SkillKind Kind { get; set; }
    public string? WeaponType { get; set; }
    public string Description { get; set; } = "";
    public Dictionary<string, double> PerLevel { get; set; } = new();

    public double Per(string key) => PerLevel.GetValueOrDefault(key);
}

public sealed class ClassSkillInfo
{
    public List<string> Starting { get; set; } = [];
    public Dictionary<string, double> CostMultiplier { get; set; } = new();
}

public enum SpellKind { Bolt, Burst, Nova, Heal, HealArea, BuffSelf, BuffArea, Drain, Blink, Revive }

public sealed class SpellDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Field { get; set; } = "";
    public int RequiredLevel { get; set; }
    public SpellKind Kind { get; set; }
    public int ManaCost { get; set; }
    public float Cooldown { get; set; }
    public float Range { get; set; }
    public float Radius { get; set; }
    public float Power { get; set; }
    public float Duration { get; set; }
    public float Slow { get; set; }
    public float Knockback { get; set; }
    public float Defense { get; set; }
    public float Damage { get; set; }
    public float BonusVsUndead { get; set; }
    public float HealReturn { get; set; }
    public float ManaReturn { get; set; }
    public string Description { get; set; } = "";

    public bool IsDamage => Kind is SpellKind.Bolt or SpellKind.Burst or SpellKind.Nova or SpellKind.Drain;
    public bool NeedsMonsterTarget => Kind is SpellKind.Bolt or SpellKind.Burst or SpellKind.Drain;
    public bool TargetsAlly => Kind is SpellKind.Heal or SpellKind.Revive;
}

internal sealed class SkillFile
{
    public SkillRules Rules { get; set; } = new();
    public List<SkillDef> Skills { get; set; } = [];
    public Dictionary<string, ClassSkillInfo> Classes { get; set; } = new();
}
