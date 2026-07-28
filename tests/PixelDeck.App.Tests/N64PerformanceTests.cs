using System.Diagnostics;
using PixelDeck.Emulation.N64;
using Xunit.Abstractions;

namespace PixelDeck.App.Tests;

public sealed class N64PerformanceTests(ITestOutputHelper output)
{
    private const int MeasuredInstructions = 5_000_000;

    [Fact]
    public void InterpreterSustainsABaselineInstructionRateAllocationFree()
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(CreateBusyLoopCartridgeImage()));
        machine.RunInstructions(200_000);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        machine.RunInstructions(MeasuredInstructions);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        var instructionsPerSecond = MeasuredInstructions / elapsed.TotalSeconds;
        output.WriteLine(
            $"{MeasuredInstructions:N0} instructions in {elapsed.TotalSeconds:0.000}s = " +
            $"{instructionsPerSecond / 1_000_000:0.00} MIPS " +
            $"(realtime requires {N64Memory.CpuTicksPerSecond / 1_000_000.0:0.000} MIPS, " +
            $"{instructionsPerSecond / N64Memory.CpuTicksPerSecond:P1} of realtime).");

#if DEBUG
        // Debug builds carry bounds checks and no inlining; this floor only
        // guards against catastrophic regressions while Release below keeps
        // the meaningful budget.
        var minimumInstructionsPerSecond = 1_000_000;
#else
        var minimumInstructionsPerSecond = 4_000_000;
#endif
        // See NesPerformanceTests: allocation is deterministic and always
        // gated, wall-clock throughput is not meaningful on shared CI runners.
        Assert.True(allocatedBytes <= 256, $"The instruction loop allocated {allocatedBytes} bytes.");
        if (NesPerformanceTests.IsContinuousIntegration)
        {
            return;
        }

        Assert.True(
            instructionsPerSecond > minimumInstructionsPerSecond,
            $"Interpreter ran at {instructionsPerSecond / 1_000_000:0.00} MIPS; " +
            $"the floor is {minimumInstructionsPerSecond / 1_000_000.0:0.00} MIPS.");
    }

    [Fact]
    public void LocalSuperMario64ReportsFramePerformanceWhenPresent()
    {
        var path = N64TestSupport.FindSuperMario64();
        if (path is null)
        {
            output.WriteLine("Local Super Mario 64 target is not installed; optional perf gate skipped.");
            return;
        }

        var machine = N64Machine.Load(path);
        for (var frame = 0; frame < 10; frame++)
        {
            machine.RunFrame();
        }

        const int measuredFrames = 30;
        var frameDurations = new long[measuredFrames];
        var started = Stopwatch.GetTimestamp();
        for (var frame = 0; frame < measuredFrames; frame++)
        {
            var frameStarted = Stopwatch.GetTimestamp();
            machine.RunFrame();
            frameDurations[frame] = Stopwatch.GetTimestamp() - frameStarted;
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        Array.Sort(frameDurations);
        var p99 = TimeSpan.FromSeconds(
            frameDurations[measuredFrames - 1] / (double)Stopwatch.Frequency);
        var framesPerSecond = measuredFrames / elapsed.TotalSeconds;
        output.WriteLine(
            $"{measuredFrames} frames in {elapsed.TotalSeconds:0.000}s = " +
            $"{framesPerSecond:0.00} fps (realtime is {N64Machine.NtscFramesPerSecond:0} fps, " +
            $"{framesPerSecond / N64Machine.NtscFramesPerSecond:P1}); " +
            $"worst frame {p99.TotalMilliseconds:0.0}ms, " +
            $"instructions={machine.Cpu.InstructionsExecuted:N0}, " +
            $"gfx tasks={machine.GraphicsTasksSubmitted}.");

        Assert.Equal(0, machine.Cpu.UnsupportedInstructionCount);
        Assert.True(framesPerSecond > 1, $"Frame loop ran at {framesPerSecond:0.00} fps.");
    }

    [Fact]
    public void LocalSuperMario64SustainsRealtimeWithRenderingAndAudioWhenPresent()
    {
        var path = N64TestSupport.FindSuperMario64();
        if (path is null)
        {
            output.WriteLine("Local Super Mario 64 target is not installed; optional gameplay perf gate skipped.");
            return;
        }

        // Warm past boot to the rendered title sequence so the measured
        // window includes Fast3D graphics tasks and audio HLE, not just CPU.
        var machine = N64Machine.Load(path);
        var drain = new float[8_192];
        for (var frame = 0; frame < 420; frame++)
        {
            machine.RunFrame();
            while (machine.ReadAudioSamples(drain) > 0)
            {
            }
        }

        var graphicsTasksBefore = machine.GraphicsTasksSubmitted;
        var audioTasksBefore = machine.AudioTasksSubmitted;
        const int measuredFrames = 60;
        var started = Stopwatch.GetTimestamp();
        for (var frame = 0; frame < measuredFrames; frame++)
        {
            machine.RunFrame();
            while (machine.ReadAudioSamples(drain) > 0)
            {
            }
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        var framesPerSecond = measuredFrames / elapsed.TotalSeconds;
        output.WriteLine(
            $"{measuredFrames} rendered frames in {elapsed.TotalSeconds:0.000}s = " +
            $"{framesPerSecond:0.00} fps ({framesPerSecond / N64Machine.NtscFramesPerSecond:P1} of realtime); " +
            $"gfx tasks in window={machine.GraphicsTasksSubmitted - graphicsTasksBefore}, " +
            $"audio tasks in window={machine.AudioTasksSubmitted - audioTasksBefore}, " +
            $"triangles={machine.Renderer.TrianglesDrawn:N0}.");

        Assert.True(
            machine.GraphicsTasksSubmitted > graphicsTasksBefore,
            "The measured window contained no graphics tasks; the warmup did not reach rendering.");
        // Clean-machine baseline is ~45 fps (2026-07-26); the target is 60.
        // The floor is a catastrophe guard only — thermal throttling swings
        // wall-clock results by ±20%, so a tight floor would flake on heat.
        if (NesPerformanceTests.IsContinuousIntegration)
        {
            return;
        }

        Assert.True(
            framesPerSecond > 25,
            $"Rendered gameplay ran at {framesPerSecond:0.00} fps; the regression floor is 25 fps.");
    }

    [Fact]
    public void LocalSuperMario64GraphicsSkippedFramesProvideCatchUpHeadroomWhenPresent()
    {
        var path = N64TestSupport.FindSuperMario64();
        if (path is null)
        {
            output.WriteLine("Local Super Mario 64 target is not installed; optional frameskip gate skipped.");
            return;
        }

        var machine = N64Machine.Load(path);
        for (var frame = 0; frame < 420; frame++)
        {
            machine.RunFrame();
        }

        const int measuredFrames = 60;
        var started = Stopwatch.GetTimestamp();
        for (var frame = 0; frame < measuredFrames; frame++)
        {
            machine.RunFrame(
                renderGraphics: false,
                executeGraphicsTasks: false);
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        var framesPerSecond = measuredFrames / elapsed.TotalSeconds;
        output.WriteLine(
            $"{measuredFrames} graphics-skipped frames in {elapsed.TotalSeconds:0.000}s = " +
            $"{framesPerSecond:0.00} fps ({framesPerSecond / N64Machine.NtscFramesPerSecond:P1} of realtime).");

        Assert.Equal(0, machine.Cpu.UnsupportedInstructionCount);
        if (NesPerformanceTests.IsContinuousIntegration)
        {
            return;
        }

        Assert.True(
            framesPerSecond > 45,
            $"Graphics-skipped gameplay ran at {framesPerSecond:0.00} fps; " +
            "the frame enforcer needs catch-up headroom.");
    }

    private static byte[] CreateBusyLoopCartridgeImage()
    {
        var image = new byte[0x2000];
        image[0] = 0x80;
        image[1] = 0x37;
        image[2] = 0x12;
        image[3] = 0x40;
        N64TestSupport.WriteUInt32(image, 0x08, 0x80000400);
        "PIXEL64 PERF        "u8.CopyTo(image.AsSpan(0x20, 20));
        image[0x3B] = (byte)'N';
        image[0x3C] = (byte)'P';
        image[0x3D] = (byte)'X';
        image[0x3E] = (byte)'E';

        // Boot block runs from SP DMEM at 0xA4000040: a load/add/store loop
        // against RDRAM that exercises fetch, memory reads, writes, and the
        // branch/delay-slot machinery forever.
        N64TestSupport.WriteUInt32(image, 0x40, 0x3C088000); // LUI   t0, 0x8000
        N64TestSupport.WriteUInt32(image, 0x44, 0x8D090100); // LW    t1, 0x0100(t0)
        N64TestSupport.WriteUInt32(image, 0x48, 0x25290001); // ADDIU t1, t1, 1
        N64TestSupport.WriteUInt32(image, 0x4C, 0xAD090100); // SW    t1, 0x0100(t0)
        N64TestSupport.WriteUInt32(image, 0x50, 0x1000FFFC); // BEQ   r0, r0, -4 (back to LW)
        N64TestSupport.WriteUInt32(image, 0x54, 0x00000000); // NOP (delay slot)
        return image;
    }



}
