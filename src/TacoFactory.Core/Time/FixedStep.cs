namespace TacoFactory.Core.Time;

/// <summary>
/// Turns variable frame time into a whole number of fixed simulation steps.
/// The client and the server both run the simulation through this, so they tick identically.
/// </summary>
public sealed class FixedStep
{
    private double _accumulator;

    public FixedStep(int ticksPerSecond, int maxStepsPerFrame = 5)
    {
        if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
        if (maxStepsPerFrame <= 0) throw new ArgumentOutOfRangeException(nameof(maxStepsPerFrame));
        StepSeconds = 1.0 / ticksPerSecond;
        MaxStepsPerFrame = maxStepsPerFrame;
    }

    public double StepSeconds { get; }
    public int MaxStepsPerFrame { get; }
    public long TotalSteps { get; private set; }

    /// <summary>Fraction of a step left over, for interpolating rendering between ticks (0..1).</summary>
    public double Alpha => _accumulator / StepSeconds;

    /// <summary>Adds elapsed time and returns how many steps to run now. Drops time past the cap so a stall can't snowball.</summary>
    public int Advance(double elapsedSeconds)
    {
        if (elapsedSeconds < 0) elapsedSeconds = 0;
        _accumulator += elapsedSeconds;
        int steps = (int)(_accumulator / StepSeconds);
        if (steps > MaxStepsPerFrame)
        {
            steps = MaxStepsPerFrame;
            _accumulator = 0;
        }
        else
        {
            _accumulator -= steps * StepSeconds;
        }
        TotalSteps += steps;
        return steps;
    }
}
