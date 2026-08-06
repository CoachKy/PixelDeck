using PixelDeck.Emulation.N64;

namespace CruisnDiag;

// Measures how completely Fast3D lowers its primitives into native RDP command
// packets. Mirror mode is set directly rather than through TryEnableNativeRdp,
// so this needs no Vulkan device: the software raster still owns the screen and
// the lowered stream is delivered to the managed engine, which counts what it
// receives.
internal static class Bridge
{
    /// <summary>
    /// Exercises the watchdog overload. Without a usable Vulkan device the
    /// underlying native call never returns, so the only thing that matters
    /// here is that this method does.
    /// </summary>
    public static void ProbeNative(string rom, int timeoutSeconds)
    {
        var machine = N64Machine.Load(rom);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var enabled = machine.TryEnableNativeRdp(
            N64RdpBridgeMode.Mirror,
            1,
            TimeSpan.FromSeconds(timeoutSeconds));
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        Console.WriteLine($"probe returned after {elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  enabled      = {enabled}");
        Console.WriteLine($"  bridge mode  = {machine.RdpBridgeMode}");
        Console.WriteLine($"  reason       = '{machine.NativeRdpUnavailableReason}'");

        // The session has to stay usable after a failed probe.
        for (var field = 0; field < 60; field++)
        {
            machine.RunFrame();
        }

        Console.WriteLine($"  ran 60 fields after the probe: {machine.Width}x{machine.Height}");
    }

    /// <summary>
    /// Mirror must leave the presented frame byte-identical to Off: the whole
    /// point of that mode is to measure the lowered stream without altering
    /// what the player sees. Any difference is a bug that blocks the migration.
    /// </summary>
    public static void CompareModes(string rom, int fields)
    {
        var offHash = HashRun(rom, fields, N64RdpBridgeMode.Off);
        var mirrorHash = HashRun(rom, fields, N64RdpBridgeMode.Mirror);
        var name = Path.GetFileNameWithoutExtension(rom);
        Console.WriteLine(
            $"{(offHash == mirrorHash ? "IDENTICAL" : "DIFFERS  ")}  {name,-46} " +
            $"off={offHash:X16} mirror={mirrorHash:X16}");
    }

    private static ulong HashRun(string rom, int fields, N64RdpBridgeMode mode)
    {
        var machine = N64Machine.Load(rom);
        machine.RdpBridgeMode = mode;
        for (var field = 0; field < fields; field++)
        {
            machine.RunFrame();
        }

        var hash = 14695981039346656037UL;
        foreach (var pixel in machine.CurrentFrame)
        {
            hash = (hash ^ pixel) * 1099511628211UL;
        }

        return hash;
    }

    public static void Run(string rom, int fields, N64RdpBridgeMode mode, string output)
    {
        var machine = N64Machine.Load(rom);
        machine.RdpBridgeMode = mode;

        for (var field = 0; field < fields; field++)
        {
            machine.RunFrame();
        }

        var renderer = machine.Renderer;
        var engine = machine.LleRdpEngine;

        // Triangles the engine received but cannot rasterize are, for this
        // purpose, triangles that lowered successfully -- under native RDP they
        // would be drawn rather than counted here.
        var delivered = engine.RdpTrianglesUnhandled;
        var drawn = renderer.TrianglesDrawn;
        var noPerspective = renderer.OmittedForNoPerspective;
        var unsupported = renderer.OmittedUnsupportedPrimitive;
        var encoderDropped = drawn - delivered - noPerspective - unsupported;

        Console.WriteLine($"{Path.GetFileNameWithoutExtension(rom)}");
        Console.WriteLine($"  Fast3D triangles drawn      {drawn,12:N0}");
        Console.WriteLine($"  lowered to RDP packets      {delivered,12:N0}");
        Console.WriteLine($"  omitted: no perspective     {noPerspective,12:N0}");
        Console.WriteLine($"  omitted: unsupported prim   {unsupported,12:N0}");
        Console.WriteLine($"  dropped inside the encoder  {encoderDropped,12:N0}");
        Console.WriteLine($"  RDP commands delivered      {engine.RdpCommandsProcessed,12:N0}");
        Console.WriteLine(
            $"  => lowering coverage        {(drawn == 0 ? 0 : 100.0 * delivered / drawn),11:F2}%");

        if (output.Length > 0)
        {
            Directory.CreateDirectory(output);
            Png.WriteArgb(
                Path.Combine(output, "mirror.png"),
                machine.CurrentFrame,
                machine.Width,
                machine.Height);
        }
    }
}
