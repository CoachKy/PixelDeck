using PixelDeck.Emulation.N64;

namespace CruisnDiag;

// Measures the RDP bridge: does native paraLLEl-RDP come up, and how complete
// is the display list Fast3D lowers into native RDP packets? Mirror mode keeps
// the software raster on screen, so this is safe to run against any title.
internal static class Bridge
{
    public static void Run(string rom, int fields, N64RdpBridgeMode mode, string output)
    {
        var machine = N64Machine.Load(rom);
        var probeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var enabled = machine.TryEnableNativeRdp(mode, 1, TimeSpan.FromSeconds(20));
        Console.WriteLine(
            $"init probe returned in " +
            $"{System.Diagnostics.Stopwatch.GetElapsedTime(probeStarted).TotalSeconds:F1}s");
        if (!enabled)
        {
            Console.WriteLine($"  reason: {machine.NativeRdpUnavailableReason}");
        }

        Console.WriteLine($"native rdp: {(enabled ? "ENABLED" : "unavailable")}");
        Console.WriteLine($"  IsNativeRdpActive = {machine.LleRdpEngine.IsNativeRdpActive}");
        Console.WriteLine($"  backend           = {machine.LleRdpEngine.ActiveBackendName}");
        Console.WriteLine($"  bridge mode       = {machine.RdpBridgeMode}");
        Console.WriteLine();

        for (var field = 0; field < fields; field++)
        {
            machine.RunFrame();
        }

        var renderer = machine.Renderer;
        var engine = machine.LleRdpEngine;
        var lowered = engine.RdpCommandsProcessed;
        var omitted = renderer.OmittedForNoPerspective + renderer.OmittedUnsupportedPrimitive;
        Console.WriteLine($"triangles drawn by Fast3D      {renderer.TrianglesDrawn,12:N0}");
        Console.WriteLine($"RDP commands delivered         {lowered,12:N0}");
        Console.WriteLine($"triangles engine could not use {engine.RdpTrianglesUnhandled,12:N0}");
        Console.WriteLine($"omitted: no perspective        {renderer.OmittedForNoPerspective,12:N0}");
        Console.WriteLine($"omitted: unsupported primitive {renderer.OmittedUnsupportedPrimitive,12:N0}");
        var total = renderer.TrianglesDrawn + omitted;
        Console.WriteLine();
        Console.WriteLine(
            $"lowering coverage: {(total == 0 ? 0 : 100.0 * renderer.TrianglesDrawn / total):F2}% " +
            $"of primitives reached the encoder");

        if (output.Length > 0)
        {
            Png.WriteArgb(
                Path.Combine(output, "bridge.png"),
                machine.CurrentFrame,
                machine.Width,
                machine.Height);
            Console.WriteLine($"frame written to {output}");
        }
    }
}
