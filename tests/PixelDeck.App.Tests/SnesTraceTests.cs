using PixelDeck.Emulation.Snes;
using Xunit.Abstractions;

namespace PixelDeck.App.Tests;

/// <summary>
/// Headless boot diagnostics for a locally installed Super Nintendo
/// cartridge. Separates core behaviour from the presentation layer when a
/// game misbehaves in the app.
/// </summary>
public sealed class SnesTraceTests(ITestOutputHelper output)
{
    [Fact]
    public void TraceLocalCartridgeWhenRequested()
    {
        var requested = Environment.GetEnvironmentVariable("PIXELSNES_TRACE_CART");
        if (string.IsNullOrWhiteSpace(requested))
        {
            return;
        }

        var path = FindCartridges()
            .FirstOrDefault(candidate =>
                Path.GetFileName(candidate).Contains(requested, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(path);

        var machine = SnesMachine.Load(path);
        output.WriteLine($"{Path.GetFileNameWithoutExtension(path)} -> {machine.Width}x{machine.Height}");

        // Native-mode interrupt vectors, so a "stuck" program counter can be
        // attributed to a handler rather than the main thread.
        static ushort Vector(SnesMachine m, uint address) =>
            (ushort)(m.PeekMemory(address) | (m.PeekMemory(address + 1) << 8));
        output.WriteLine(
            $"vectors: nmi=0x{Vector(machine, 0x00FFEA):X4} irq=0x{Vector(machine, 0x00FFEE):X4} " +
            $"brk=0x{Vector(machine, 0x00FFE6):X4} reset=0x{Vector(machine, 0x00FFFC):X4}");

        var nmiVector = Vector(machine, 0x00FFEA);
        var handler = new List<string>();
        for (var offset = 0u; offset < 24; offset++)
        {
            handler.Add($"{machine.PeekMemory(nmiVector + offset):X2}");
        }

        output.WriteLine($"nmi handler @0x00{nmiVector:X4}: {string.Join(" ", handler)}");

        var driveInput = Environment.GetEnvironmentVariable("PIXELSNES_TRACE_INPUT") == "1";
        var maximumFrames = int.TryParse(
            Environment.GetEnvironmentVariable("PIXELSNES_TRACE_FRAMES"),
            out var configured)
            ? configured
            : 1_800;

        // Sample colour variety every half second. A run of near-black frames
        // is what "the screen went black" looks like from the core's side.
        for (var frame = 0; frame < maximumFrames; frame++)
        {
            if (driveInput)
            {
                machine.SetControllerState(
                    1,
                    (frame % 180) switch
                    {
                        >= 20 and < 40 => SnesButton.Start,
                        >= 100 and < 120 => SnesButton.A,
                        _ => SnesButton.None
                    });
            }

            machine.RunFrame();
            // Sample the first few frames too: the SPC700 IPL handshake
            // completes within the first frame, long before the periodic
            // samples begin.
            if (frame % 30 != 29 && frame > 4)
            {
                continue;
            }

            var pixels = machine.CurrentFrame;
            var nonBlack = 0;
            var distinct = new HashSet<uint>();
            foreach (var pixel in pixels)
            {
                if ((pixel & 0x00FFFFFF) != 0)
                {
                    nonBlack++;
                }

                if (distinct.Count < 4096)
                {
                    distinct.Add(pixel);
                }
            }

            var colors = distinct.Count;
            output.WriteLine(
                $"frame {frame + 1,5}: colors={colors,5} non-black={nonBlack,7} " +
                $"({nonBlack * 100.0 / pixels.Length:0.0}%) " +
                $"blank={(machine.IsDisplayBlanked ? "Y" : "n")} " +
                $"bright={machine.DisplayBrightness,2} " +
                $"mode={machine.BackgroundMode} layers=0x{machine.MainScreenLayers:X2} " +
                $"pc=0x{machine.ProgramAddress:X6} " +
                $"nmitimen=0x{machine.NmiTimerControl:X2} " +
                $"nmis={machine.NmiCount,8:N0} irqs={machine.IrqCount,8:N0} " +
                $"apu=0x{machine.ApuOutputWord:X4} " +
                $"inidisp={machine.DisplayControlWrites,6:N0}/lit={machine.NonZeroBrightnessWrites,6:N0} " +
                $"last=0x{machine.LastDisplayControlValue:X2} " +
                (machine.HasSa1
                    ? $"SA1 instr={machine.Sa1ExecutedInstructions,12:N0} " +
                      $"pc=0x{machine.Sa1ProgramAddress:X6} " +
                      $"ccnt=0x{machine.Sa1ControlRegister:X2} " +
                      $"dma={machine.Sa1DmaCount,6:N0} "
                    : string.Empty) +
                $"hvbjoy={machine.HvbJoyReads,8:N0}/busy={machine.HvbJoyAutoReadBusyReads,6:N0} " +
                $"latch={machine.CounterLatchCount,7:N0} " +
                $"dma={machine.DmaTransferCount,6:N0}(0x{machine.LastDmaChannelMask:X2}) " +
                $"hdmaEn={machine.HdmaEnableWrites,6:N0} " +
                $"vTarget={machine.VerticalIrqTarget,4} hTarget={machine.HorizontalIrqTarget,4} " +
                $"line={machine.CurrentScanline,4} " +
                $"apu={machine.ApuExecutedInstructions,12:N0} " +
                $"apuBad=0x{machine.ApuFirstUnsupportedOpcode:X2}@0x{machine.ApuFirstUnsupportedAddress:X4} " +
                $"ppuWrites={machine.PpuRegisterWriteCount,10:N0}");

            if (Environment.GetEnvironmentVariable("PIXELSNES_TRACE_HANDLER") == "1")
            {
                // The RAM handlers are rewritten per scene, so they must be
                // sampled at the point of interest rather than at boot.
                var ram = new List<string>();
                for (var offset = 0u; offset < 28; offset++)
                {
                    ram.Add($"{machine.PeekMemory(0x001500 + offset):X2}");
                }

                output.WriteLine($"      ram handler @0x001500: {string.Join(" ", ram)}");

                // Follow the JML at $1500 to the real handler and dump it.
                var target = (uint)(machine.PeekMemory(0x001501) |
                                    (machine.PeekMemory(0x001502) << 8) |
                                    (machine.PeekMemory(0x001503) << 16));
                var body = new List<string>();
                for (var offset = 0u; offset < 32; offset++)
                {
                    body.Add($"{machine.PeekMemory(target + offset):X2}");
                }

                output.WriteLine($"      nmi body @0x{target:X6}: {string.Join(" ", body)}");
            }

            if (Environment.GetEnvironmentVariable("PIXELSNES_TRACE_CODE") != "1")
            {
                continue;
            }

            // Dump the bytes around the stuck program counter so the polling
            // instruction can be decoded by hand.
            var bytes = new List<string>();
            for (var offset = -8; offset < 16; offset++)
            {
                var address = (uint)((long)machine.ProgramAddress + offset);
                bytes.Add($"{machine.PeekMemory(address):X2}");
            }

            output.WriteLine($"      code @-8: {string.Join(" ", bytes)}");
        }
    }

    private static IEnumerable<string> FindCartridges()
    {
        var configured = Environment.GetEnvironmentVariable("PIXELDECK_GAMES_FOLDER");
        var gamesFolder = string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Games"))
            : Path.GetFullPath(configured);
        var folder = Path.Combine(gamesFolder, "SuperNintendo");
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(path =>
                    Path.GetExtension(path).Equals(".sfc", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(path).Equals(".smc", StringComparison.OrdinalIgnoreCase))
            : [];
    }
}
