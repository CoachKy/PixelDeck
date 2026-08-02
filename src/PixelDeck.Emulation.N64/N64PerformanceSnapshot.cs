namespace PixelDeck.Emulation.N64;

/// <summary>
/// Cumulative wall-clock cost of Pixel64's major execution phases. It is
/// deliberately diagnostic-only: N64 timing continues to come from emulated
/// clocks rather than host performance.
/// </summary>
public readonly record struct N64PerformanceSnapshot(
    long Fields,
    long GraphicsTasks,
    long AudioTasks,
    TimeSpan Total,
    TimeSpan Graphics,
    TimeSpan Audio,
    TimeSpan VideoInterface)
{
    public TimeSpan CpuAndScheduling
    {
        get
        {
            var measured = Graphics + Audio + VideoInterface;
            return measured < Total ? Total - measured : TimeSpan.Zero;
        }
    }

    public double AverageMillisecondsPerField => Fields > 0
        ? Total.TotalMilliseconds / Fields
        : 0;

    public double GraphicsPercentage => Percentage(Graphics);

    public double AudioPercentage => Percentage(Audio);

    public double VideoInterfacePercentage => Percentage(VideoInterface);

    public double CpuAndSchedulingPercentage => Percentage(CpuAndScheduling);

    private double Percentage(TimeSpan duration) => Total > TimeSpan.Zero
        ? duration.TotalMilliseconds * 100 / Total.TotalMilliseconds
        : 0;
}
