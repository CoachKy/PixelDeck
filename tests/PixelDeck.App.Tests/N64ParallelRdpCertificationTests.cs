using PixelDeck.Emulation.N64;
using Xunit.Abstractions;

namespace PixelDeck.App.Tests;

/// <summary>
/// Opt-in certification against the user's owned local Super Mario 64 image.
/// No cartridge bytes, captures, or native output are written into the
/// repository; only compact hashes are emitted to the test log.
/// </summary>
public sealed class N64ParallelRdpCertificationTests
{
    private const string CertificationEnvironmentVariable =
        "PIXELDECK_CERTIFY_PARALLEL_RDP_MARIO";
    private readonly ITestOutputHelper _output;

    public N64ParallelRdpCertificationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void LocalSuperMario64ProducesAnUnambiguousNativeSequenceWhenPresent()
    {
        var path = N64TestSupport.FindSuperMario64();
        if (path is null)
        {
            _output.WriteLine(
                "Local Super Mario 64 target is not installed; optional " +
                "sequence-capture gate skipped.");
            return;
        }

        var traces = CaptureConsecutiveTriangleTasks(path);

        Assert.True(
            traces.Count >= 2,
            "Pixel64 could not capture two consecutive complete Mario triangle tasks.");
        Assert.All(traces, trace => Assert.True(trace.IsComplete));
        Assert.Contains(
            traces.First().Commands,
            command => command.Opcode == 0x0F);
        Assert.Contains(
            traces.Last().Commands,
            command => command.Opcode == 0x0F);
        _output.WriteLine(
            $"Captured {traces.Count} ordered task(s) from " +
            $"{traces.First().TraceSha256} through {traces.Last().TraceSha256}.");
    }

    [Fact]
    public void LocalSuperMario64PreservesNativeCoverageAcrossGraphicsTasks()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    CertificationEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            _output.WriteLine(
                $"Set {CertificationEnvironmentVariable}=1 to run the owned-local " +
                "Mario native sequence gate.");
            return;
        }

        var path = N64TestSupport.FindSuperMario64();
        Assert.NotNull(path);
        var traces = CaptureConsecutiveTriangleTasks(path);
        Assert.True(
            traces.Count >= 2,
            "Pixel64 could not capture two consecutive complete Mario triangle tasks.");

        var initialHidden =
            new byte[ParallelRdpContext.HiddenRdramSize];
        Assert.True(
            ParallelRdpTraceReplay.TryReplaySequence(
                traces,
                initialHidden,
                out var first,
                out var firstSummary),
            firstSummary);
        Assert.Equal(traces.Count, first.Count);
        Assert.Contains(
            first,
            result => result.Framebuffer?.Changed is true);
        Assert.Contains(
            first,
            result => result.DepthBuffer?.Changed is true);
        Assert.Contains(
            first,
            result => result.HiddenCoverage.Changed);

        for (var index = 1; index < first.Count; index++)
        {
            Assert.Equal(
                first[index - 1].HiddenCoverage.OutputSha256,
                first[index].HiddenCoverage.InputSha256);
        }

        Assert.True(
            ParallelRdpTraceReplay.TryReplaySequence(
                traces,
                initialHidden,
                out var second,
                out var secondSummary),
            secondSummary);
        Assert.Equal(first.Count, second.Count);
        for (var index = 0; index < first.Count; index++)
        {
            Assert.Equal(
                first[index].RdramSha256,
                second[index].RdramSha256);
            Assert.Equal(
                first[index].HiddenRdramSha256,
                second[index].HiddenRdramSha256);
            _output.WriteLine(
                $"Task {index + 1}: trace={traces[index].TraceSha256}, " +
                $"RDRAM={first[index].RdramSha256}, " +
                $"frame={Format(first[index].Framebuffer)}, " +
                $"depth={Format(first[index].DepthBuffer)}, " +
                $"coverage={first[index].HiddenRdramSha256} " +
                $"({first[index].HiddenCoverage.ChangedBytes:N0} changed bytes)");
        }
    }

    private static List<N64RdpTrace> CaptureConsecutiveTriangleTasks(
        string path)
    {
        var machine = N64Machine.Load(path);
        var sequence = new List<N64RdpTrace>();
        for (var field = 0; field < 720; field++)
        {
            machine.SetControllerState(
                1,
                N64TestSupport.WalkTitleScreens(field));
            var tasksBefore = machine.GraphicsTasksSubmitted;
            machine.RequestGraphicsTaskCapture();
            machine.RunFrame();
            var tasksSubmitted =
                machine.GraphicsTasksSubmitted - tasksBefore;
            var capture = machine.LastGraphicsCapture;
            if (capture is null || tasksSubmitted != 1)
            {
                sequence.Clear();
                continue;
            }

            var trace = N64RdpTrace.Capture(capture);
            var hasTriangle =
                trace.Commands.Any(command => command.Opcode == 0x0F);
            var layout = N64RdpOutputLayoutParser.Analyze(trace.Commands);
            var hasOutputImages =
                layout.Framebuffer is not null &&
                layout.DepthBuffer is not null;
            if (!trace.IsComplete || !hasOutputImages)
            {
                sequence.Clear();
                continue;
            }

            if (sequence.Count == 0)
            {
                if (hasTriangle)
                {
                    sequence.Add(trace);
                }

                continue;
            }

            sequence.Add(trace);
            if (hasTriangle)
            {
                return sequence;
            }

            // Keep the gate bounded and avoid carrying a long span of tasks
            // when a later microcode phase stops drawing ordinary triangles.
            if (sequence.Count >= 4)
            {
                sequence.Clear();
            }
        }

        return sequence;
    }

    private static string Format(ParallelRdpBufferDelta? delta) =>
        delta is null
            ? "not selected"
            : $"{delta.OutputSha256} ({delta.ChangedBytes:N0} changed bytes)";
}
