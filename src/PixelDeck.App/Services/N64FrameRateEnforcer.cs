namespace PixelDeck.App.Services;

/// <summary>
/// Keeps an overdue Pixel64 session from trying to execute an unbounded burst
/// of catch-up fields. This is a limiter, not a frame skipper: dropping RSP/RDP
/// work can make a 30 fps game update only a few times per second.
/// </summary>
internal sealed class N64FrameRateEnforcer
{
    private const int MaximumCatchUpFields = 8;

    internal TimeSpan BoundCatchUp(
        TimeSpan elapsed,
        TimeSpan deadline,
        TimeSpan frameInterval)
    {
        if (frameInterval <= TimeSpan.Zero)
        {
            return elapsed;
        }

        var maximumLag = TimeSpan.FromTicks(
            checked(frameInterval.Ticks * MaximumCatchUpFields));
        return elapsed - deadline > maximumLag
            ? elapsed
            : deadline;
    }
}
