using TacoFactory.Core.Time;

namespace TacoFactory.Core.Tests;

public class FixedStepTests
{
    [Fact]
    public void One_second_at_20hz_is_20_steps()
    {
        var step = new FixedStep(20, maxStepsPerFrame: 100);
        int total = 0;
        for (int i = 0; i < 60; i++) total += step.Advance(1.0 / 60);
        Assert.InRange(total, 19, 20);
        Assert.Equal(total, step.TotalSteps);
    }

    [Fact]
    public void Remainder_carries_into_alpha()
    {
        var step = new FixedStep(20);
        Assert.Equal(0, step.Advance(0.025)); // half a 50 ms step
        Assert.Equal(0.5, step.Alpha, 3);
        Assert.Equal(1, step.Advance(0.025));
    }

    [Fact]
    public void Long_stall_is_capped()
    {
        var step = new FixedStep(20, maxStepsPerFrame: 5);
        Assert.Equal(5, step.Advance(10.0));
        Assert.Equal(0, step.Advance(0.0));
    }

    [Fact]
    public void Negative_time_is_ignored() => Assert.Equal(0, new FixedStep(20).Advance(-1));
}
