namespace TacoFactory.Core.Sim;

/// <summary>Character level curve and per-level stat growth (from the Templar Knights balance constants).</summary>
public static class Progression
{
    public const int MaxLevel = 100;
    public const int BaseHealth = 100, HealthPerLevel = 10;
    public const int BaseMana = 50, ManaPerLevel = 5;
    public const int BaseDamage = 15, DamagePerLevel = 2;
    public const double XpForLevel2 = 400, XpCurve = 1.22; // tuned with bot balance runs (Act I)
    public const int XpPerEnemyBase = 100;
    /// <summary>Global XP tuning, set from balance runs so a group leaves each Act I zone near its top level.</summary>
    public const double XpScale = 0.35;

    private static long[]? _table;

    /// <summary>The level curve from assets/data/levels.json (fitted to the whole campaign). Without it, the Act I formula.</summary>
    public static void UseTable(long[]? table) => _table = table is { Length: >= MaxLevel - 1 } ? table : null;

    /// <summary>Experience needed to go from <paramref name="level"/> to the next level.</summary>
    public static long XpToNext(int level) =>
        level >= MaxLevel ? long.MaxValue
        : _table is { } t ? t[level - 1]
        : (long)System.Math.Round(XpForLevel2 * System.Math.Pow(XpCurve, level - 1));

    public static int MaxHealth(ClassDef c, int level) => (int)((BaseHealth + HealthPerLevel * (level - 1)) * c.HealthMul);
    public static int MaxMana(ClassDef c, int level) => (int)((BaseMana + ManaPerLevel * (level - 1)) * c.ManaMul);
    public static float Damage(ClassDef c, int level) => (BaseDamage + DamagePerLevel * (level - 1)) * c.DamageMul;
}

/// <summary>
/// Monster threat, tuned with bot balance runs (solo, 3 and 6 players) so groups feel pressure and
/// solo players can still finish Act I. Monster base stats in monsters.json already rise from tier to tier,
/// so the per-level growth is kept small. Fields rather than constants so balance runs can sweep them
/// (TF_DIFFICULTY in the bots). Act I results (deaths per zone): solo 0-10, groups of 3 or 6 mostly 0-6, a few wipes late.
/// </summary>
public static class Difficulty
{
    /// <summary>Multiplier on every monster's base damage.</summary>
    public static float DamageBase = 2.0f;
    /// <summary>Extra monster damage per level above 1.</summary>
    public static float DamagePerLevel = 0.03f;
    /// <summary>Multiplier on every monster's base health.</summary>
    public static float HealthBase = 2.2f;
    /// <summary>Extra monster health per level above 1.</summary>
    public static float HealthPerLevel = 0.05f;
    /// <summary>Extra monster health and damage per player beyond the first, when a zone copy opens.</summary>
    public static float GroupHealthPerPlayer = 0.5f, GroupDamagePerPlayer = 0.4f;
    /// <summary>Each player near a kill gets its full experience, plus this much per companion.</summary>
    public static float GroupXpPerPlayer = 0.05f;
}
