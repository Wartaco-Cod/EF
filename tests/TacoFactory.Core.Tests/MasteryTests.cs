using TacoFactory.Core.Sim;

namespace TacoFactory.Core.Tests;

public class MasteryTests
{
    [Theory]
    [InlineData(1, 10)]
    [InlineData(50, 500)]
    [InlineData(100, 1000)]
    public void Hits_per_level_follow_the_curve(int level, int hits) => Assert.Equal(hits, SkillMastery.HitsToReach(level));

    [Theory]
    [InlineData(1, 0.005)]
    [InlineData(100, 0.015)]
    public void Per_level_bonus_rises_from_half_to_one_and_a_half_percent(int level, double bonus) =>
        Assert.Equal(bonus, SkillMastery.LevelBonus(level), 6);

    [Theory]
    [InlineData(25, 0.155)]
    [InlineData(50, 0.374)]
    [InlineData(75, 0.655)]
    [InlineData(100, 1.000)]
    public void Total_bonus_matches_the_plan_table(int level, double total) =>
        Assert.Equal(total, SkillMastery.TotalBonus(level), 3);

    [Fact]
    public void Total_bonus_equals_sum_of_levels()
    {
        double sum = 0;
        for (int l = 1; l <= 100; l++) sum += SkillMastery.LevelBonus(l);
        Assert.Equal(sum, SkillMastery.TotalBonus(100), 9);
    }

    [Fact]
    public void Full_mastery_takes_50500_hits()
    {
        var m = new SkillMastery();
        int hits = 0;
        while (m.Level < 100) { m.AddHit(); hits++; }
        Assert.Equal(50_500, hits);
        Assert.False(m.AddHit());
    }

    [Fact]
    public void Scrolls_cap_at_five_levels_per_skill()
    {
        var m = new SkillMastery();
        for (int i = 0; i < 5; i++) Assert.True(m.ApplyScroll());
        Assert.False(m.ApplyScroll());
        Assert.Equal(5, m.Level);
        Assert.Equal(5, m.ScrollLevels);
    }

    [Fact]
    public void One_scroll_per_opponent_per_24_hours()
    {
        var t = new DuelRewardTracker();
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(t.TryAward("Gaspard", "Hugues", now));
        Assert.False(t.TryAward("Gaspard", "Hugues", now.AddHours(23)));
        Assert.True(t.TryAward("Gaspard", "Bertrand", now.AddHours(1)));
        Assert.True(t.TryAward("Hugues", "Gaspard", now.AddHours(1)));
        Assert.True(t.TryAward("Gaspard", "Hugues", now.AddHours(24)));
        Assert.False(t.TryAward("Gaspard", "gaspard", now));
    }
}
