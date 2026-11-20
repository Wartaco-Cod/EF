namespace TacoFactory.Core.Sim;

/// <summary>
/// Skill mastery, levels 0 to 100, one track per skill.
/// Hits to reach level L = 10 × L. Level L adds 0.5% + (L − 1) × 1%/99 damage, so mastery 100 = +100%.
/// Arena scrolls add one level each, at most 5 levels per skill.
/// </summary>
public sealed class SkillMastery
{
    public const int MaxLevel = 100;
    public const int MaxScrollLevels = 5;

    public int Level { get; private set; }
    public int Hits { get; private set; }
    public int ScrollLevels { get; private set; }

    public SkillMastery() { }

    public SkillMastery(int level, int hits, int scrollLevels)
    {
        Level = System.Math.Clamp(level, 0, MaxLevel);
        Hits = System.Math.Max(0, hits);
        ScrollLevels = System.Math.Clamp(scrollLevels, 0, MaxScrollLevels);
    }

    public static int HitsToReach(int level) => 10 * level;

    public static double LevelBonus(int level) => level <= 0 ? 0 : 0.005 + (level - 1) * (0.01 / 99.0);

    /// <summary>Total damage bonus at a mastery level (0.0 to 1.0).</summary>
    public static double TotalBonus(int level) => level <= 0 ? 0 : 0.005 * level + (0.01 / 99.0) * level * (level - 1) / 2.0;

    public double DamageMultiplier => 1.0 + TotalBonus(Level);

    /// <summary>Counts one successful damaging hit on one target. Returns true if mastery levelled up.</summary>
    public bool AddHit()
    {
        if (Level >= MaxLevel) return false;
        Hits++;
        if (Hits < HitsToReach(Level + 1)) return false;
        Level++;
        Hits = 0;
        return true;
    }

    /// <summary>Reads a Skill Mastery Experience Scroll: +1 level, at most 5 levels per skill from scrolls.</summary>
    public bool ApplyScroll()
    {
        if (ScrollLevels >= MaxScrollLevels || Level >= MaxLevel) return false;
        Level++;
        ScrollLevels++;
        Hits = System.Math.Min(Hits, HitsToReach(System.Math.Min(Level + 1, MaxLevel)) - 1);
        return true;
    }
}

/// <summary>Arena rule: a winner earns at most one scroll from the same opponent every 24 hours.</summary>
public sealed class DuelRewardTracker
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);
    private readonly Dictionary<(string Winner, string Loser), DateTime> _last = new();

    public bool TryAward(string winner, string loser, DateTime nowUtc)
    {
        if (string.Equals(winner, loser, StringComparison.OrdinalIgnoreCase)) return false;
        var key = (winner.ToLowerInvariant(), loser.ToLowerInvariant());
        if (_last.TryGetValue(key, out var at) && nowUtc - at < Window) return false;
        _last[key] = nowUtc;
        return true;
    }

    public void Restore(string winner, string loser, DateTime atUtc) => _last[(winner.ToLowerInvariant(), loser.ToLowerInvariant())] = atUtc;

    public IReadOnlyDictionary<(string Winner, string Loser), DateTime> Export() => _last;
}
