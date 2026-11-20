using TacoFactory.Core.Sim;
using Xunit;

namespace TacoFactory.Core.Tests;

public class DifficultyTests
{
    private static readonly Data.GameData Data = Core.Data.GameData.Load(Content.ContentRoot.Locate(AppContext.BaseDirectory)!);

    [Fact]
    public void Bigger_groups_face_tougher_and_harder_hitting_monsters()
    {
        var solo = new World(Data, "bloodied_fields", 7);
        solo.Populate(1);
        var six = new World(Data, "bloodied_fields", 7);
        six.Populate(6);
        var a = solo.Entities.Where(e => e.Kind == EntityKind.Monster).ToList();
        var b = six.Entities.Where(e => e.Kind == EntityKind.Monster).ToList();
        Assert.Equal(a.Count, b.Count);
        // Same seed, same packs: compare like for like.
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Def!.Id, b[i].Def!.Id);
            Assert.True(b[i].MaxHp > a[i].MaxHp * 3);
            Assert.True(b[i].Damage >= a[i].Damage * 2);
        }
    }

    [Fact]
    public void Monster_level_growth_is_gentle_because_tiers_carry_the_threat()
    {
        float at20 = 1 + Difficulty.HealthPerLevel * 19;
        Assert.InRange(at20, 1.5f, 2.5f);
        Assert.InRange(1 + Difficulty.DamagePerLevel * 19, 1.3f, 2.0f);
    }
}
