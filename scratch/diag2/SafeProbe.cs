using PixelDeck.Emulation.N64;

namespace CruisnDiag;

// Exercises the out-of-process probe. The in-process watchdog did not survive
// a stalled native initialiser; this is the check that the replacement does.
internal static class SafeProbe
{
    public static void Run(string rom, string probeHost, int timeoutSeconds)
    {
        var machine = N64Machine.Load(rom);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var enabled = machine.TryEnableNativeRdpSafely(
            probeHost,
            N64RdpBridgeMode.Mirror,
            1,
            TimeSpan.FromSeconds(timeoutSeconds));
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);

        Console.WriteLine($"probe returned after {elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  enabled     = {enabled}");
        Console.WriteLine($"  bridge mode = {machine.RdpBridgeMode}");
        Console.WriteLine($"  reason      = '{machine.NativeRdpUnavailableReason}'");

        // The emulator must still be fully usable after a failed probe.
        for (var field = 0; field < 300; field++)
        {
            machine.RunFrame();
        }

        var hash = 14695981039346656037UL;
        foreach (var pixel in machine.CurrentFrame)
        {
            hash = (hash ^ pixel) * 1099511628211UL;
        }

        Console.WriteLine(
            $"  ran 300 fields afterwards: {machine.Width}x{machine.Height} frame={hash:X16}");
    }
}
