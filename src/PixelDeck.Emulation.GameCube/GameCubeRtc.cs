namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Nintendo GameCube Real-Time Clock (RTC) and System SRAM configuration.
/// </summary>
public static class GameCubeRtc
{
    private static readonly DateTime GameCubeEpoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Calculates the 32-bit GameCube RTC counter value for a given timestamp.
    /// </summary>
    public static uint GetRtcCounter(DateTime timeUtc)
    {
        var span = timeUtc - GameCubeEpoch;
        return (uint)Math.Max(0, (long)span.TotalSeconds);
    }

    /// <summary>
    /// Converts a 32-bit GameCube RTC counter back into a UTC DateTime.
    /// </summary>
    public static DateTime GetDateTime(uint rtcCounter)
    {
        return GameCubeEpoch.AddSeconds(rtcCounter);
    }
}
