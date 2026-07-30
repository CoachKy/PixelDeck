using System.Numerics;
using PixelDeck.Emulation.N64;

namespace PixelDeck.App.Tests;

public sealed class ParallelRdpSupportTests
{
    [Fact]
    public void NativeBridgeContractIncludesHiddenCoverageMemory()
    {
        Assert.Equal(2u, ParallelRdpNative.RequiredAbiVersion);
        Assert.Equal(
            N64Memory.RdramSize / 2,
            ParallelRdpContext.HiddenRdramSize);
    }

    [Fact]
    public void RuntimeProbeAlwaysReturnsAUsableFallbackDecision()
    {
        var result = ParallelRdpSupport.Probe();

        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
        Assert.NotNull(result.Devices);
    }

    [Fact]
    public void MissingVulkanLoaderSelectsSoftwareFallback()
    {
        var result = ParallelRdpSupport.Evaluate(false, []);

        Assert.False(result.LoaderAvailable);
        Assert.False(result.HasCompatibleDevice);
        Assert.Contains("software renderer", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Vulkan11DeviceRequiresBothStorageExtensions()
    {
        var device = new ParallelRdpVulkanDevice(
            "Test GPU",
            ParallelRdpSupport.MakeVersion(1, 1, 0),
            [ParallelRdpSupport.Storage8BitExtension]);

        var result = ParallelRdpSupport.Evaluate(true, [device]);

        Assert.True(result.LoaderAvailable);
        Assert.False(result.HasCompatibleDevice);
    }

    [Fact]
    public void Vulkan12PromotesStorageExtensionsToCore()
    {
        var device = new ParallelRdpVulkanDevice(
            "Test GPU",
            ParallelRdpSupport.MakeVersion(1, 2, 0),
            []);

        var result = ParallelRdpSupport.Evaluate(true, [device]);

        Assert.True(result.HasCompatibleDevice);
        Assert.Contains("slower RDRAM upload", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalHostMemorySelectsZeroCopyCandidate()
    {
        var device = new ParallelRdpVulkanDevice(
            "Test GPU",
            ParallelRdpSupport.MakeVersion(1, 2, 0),
            [ParallelRdpSupport.ExternalMemoryHostExtension]);

        var result = ParallelRdpSupport.Evaluate(true, [device]);

        Assert.True(result.HasCompatibleDevice);
        Assert.Contains("zero-copy", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionFormattingUsesVulkanBitLayout()
    {
        Assert.Equal(
            "1.3.275",
            ParallelRdpSupport.FormatVersion(
                ParallelRdpSupport.MakeVersion(1, 3, 275)));
    }

    [Fact]
    public void OptionalNativeBridgeAlwaysReturnsASafeDecision()
    {
        var bridgeLoaded = ParallelRdpNative.TryLoadBridge(
            out var bridgeSummary);
        Assert.False(string.IsNullOrWhiteSpace(bridgeSummary));
        if (!string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    ParallelRdpNative.LibraryPathEnvironmentVariable)))
        {
            Assert.True(bridgeLoaded, bridgeSummary);
        }

        var available = ParallelRdpNative.TryCreate(
            out var context,
            out var summary);
        using (context)
        {
            Assert.False(string.IsNullOrWhiteSpace(summary));
            Assert.Equal(available, context is not null);
        }
    }

    [Fact]
    public void ViSnapshotCarriesEveryProgrammedScanoutRegister()
    {
        var machine = N64Machine.Create(
            N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        var memory = machine.Memory;
        memory.WriteUInt32(0xA4400000, 0x00000003);
        memory.WriteUInt32(0xA4400004, 0x00123450);
        memory.WriteUInt32(0xA4400008, 320);
        memory.WriteUInt32(0xA440000C, 0x200);
        memory.WriteUInt32(0xA4400014, 0x03E52239);
        memory.WriteUInt32(0xA4400018, 525);
        memory.WriteUInt32(0xA440001C, 0x0C15);
        memory.WriteUInt32(0xA4400020, 0x0C150C15);
        memory.WriteUInt32(0xA4400024, 0x006C02EC);
        memory.WriteUInt32(0xA4400028, 0x002501FF);
        memory.WriteUInt32(0xA440002C, 0x000E0204);
        memory.WriteUInt32(0xA4400030, 0x00000200);
        memory.WriteUInt32(0xA4400034, 0x00000400);

        var state = ParallelRdpViState.FromMemory(memory);
        var registers = state.EnumerateRegisters().ToArray();

        Assert.Equal((uint)3, state.Control);
        Assert.Equal(0x00123450u, state.Origin);
        Assert.Equal((uint)320, state.Width);
        Assert.Equal(0x03E52239u, state.Timing);
        Assert.Equal((uint)525, state.VerticalSync);
        Assert.Equal(0x0C150C15u, state.Leap);
        Assert.Equal(0x006C02ECu, state.HorizontalStart);
        Assert.Equal(0x002501FFu, state.VerticalStart);
        Assert.Equal(14, registers.Length);
        Assert.Equal(
            Enumerable.Range(0, 14).Select(value => (uint)value),
            registers.Select(entry => (uint)entry.Register));
    }

    [Fact]
    public void NativeReplayRejectsIncompleteHleTraceBeforeLoadingBridge()
    {
        var trace = new N64RdpTrace(
            new byte[N64Memory.RdramSize],
            [],
            "F3DEX2",
            omittedHlePrimitiveCommands: 1,
            unsupportedSourceCommands: 0);

        var replayed = ParallelRdpTraceReplay.TryReplay(
            trace,
            viState: null,
            out var result,
            out var summary);

        Assert.False(replayed);
        Assert.Null(result);
        Assert.Contains("not all been lowered", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeOutputLayoutFindsFramebufferAndDepthRegions()
    {
        var trace = CreateNativeTriangleTrace();

        var layout = N64RdpOutputLayoutParser.Analyze(trace.Commands);

        Assert.Equal(
            new N64RdpMemoryRegion(0x1000, 16 * 16 * 2),
            layout.Framebuffer);
        Assert.Equal(
            new N64RdpMemoryRegion(0x2000, 16 * 16 * 2),
            layout.DepthBuffer);
    }

    [Fact]
    public void NativeBufferDeltaCountsAndHashesExactChanges()
    {
        byte[] input = [1, 2, 3, 4];
        byte[] output = [1, 9, 3, 8];

        var delta = ParallelRdpBufferDelta.Create(
            "test",
            0x120,
            input,
            output);

        Assert.Equal("test", delta.Name);
        Assert.Equal(0x120u, delta.Address);
        Assert.Equal(4, delta.Length);
        Assert.Equal(2, delta.ChangedBytes);
        Assert.True(delta.Changed);
        Assert.NotEqual(delta.InputSha256, delta.OutputSha256);
    }

    [Fact]
    public void NativeTriangleReplayChangesColorDepthAndCoverageWhenRequired()
    {
        var required = string.Equals(
            Environment.GetEnvironmentVariable(
                "PIXELDECK_REQUIRE_PARALLEL_RDP_REPLAY"),
            "1",
            StringComparison.Ordinal);
        var trace = CreateNativeTriangleTrace();
        var initialHidden =
            new byte[ParallelRdpContext.HiddenRdramSize];

        var replayed = ParallelRdpTraceReplay.TryReplay(
            trace,
            viState: null,
            initialHidden,
            out var first,
            out var firstSummary);
        if (!replayed)
        {
            Assert.False(required, firstSummary);
            return;
        }

        Assert.NotNull(first);
        Assert.NotNull(first.Framebuffer);
        Assert.NotNull(first.DepthBuffer);
        Assert.True(first.RdramDelta.Changed);
        Assert.True(first.Framebuffer.Changed);
        Assert.True(first.DepthBuffer.Changed);
        Assert.True(first.HiddenCoverage.Changed);

        Assert.True(
            ParallelRdpTraceReplay.TryReplay(
                trace,
                viState: null,
                initialHidden,
                out var second,
                out var secondSummary),
            secondSummary);
        Assert.NotNull(second);
        Assert.Equal(first.RdramSha256, second.RdramSha256);
        Assert.Equal(first.HiddenRdramSha256, second.HiddenRdramSha256);
        Assert.Equal(
            first.Framebuffer.OutputSha256,
            second.Framebuffer?.OutputSha256);
        Assert.Equal(
            first.DepthBuffer.OutputSha256,
            second.DepthBuffer?.OutputSha256);
    }

    [Fact]
    public void NativeSequenceCarriesHiddenCoverageAcrossTasksWhenRequired()
    {
        var required = string.Equals(
            Environment.GetEnvironmentVariable(
                "PIXELDECK_REQUIRE_PARALLEL_RDP_REPLAY"),
            "1",
            StringComparison.Ordinal);
        N64RdpTrace[] traces =
        [
            CreateNativeTriangleTrace(1.25f),
            CreateNativeTriangleTrace(9.0f),
        ];
        var initialHidden =
            new byte[ParallelRdpContext.HiddenRdramSize];

        var replayed = ParallelRdpTraceReplay.TryReplaySequence(
            traces,
            initialHidden,
            out var first,
            out var firstSummary);
        if (!replayed)
        {
            Assert.False(required, firstSummary);
            return;
        }

        Assert.Equal(2, first.Count);
        Assert.Equal(
            first[0].HiddenCoverage.OutputSha256,
            first[1].HiddenCoverage.InputSha256);
        Assert.True(first[0].HiddenCoverage.Changed);
        Assert.True(first[1].HiddenCoverage.Changed);
        Assert.True(first[1].Framebuffer?.Changed is true);
        Assert.True(first[1].DepthBuffer?.Changed is true);

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
        }
    }

    private static N64RdpTrace CreateNativeTriangleTrace(
        float origin = 2.25f)
    {
        var color = new Vector4(1, 0.25f, 0.125f, 1);
        const float size = 5.5f;
        Assert.True(
            N64RdpTriangleEncoder.TryEncode(
                new N64RdpHleVertex(
                    origin, origin, 0.25f, 0, 0, 0.49f, color),
                new N64RdpHleVertex(
                    origin + size, origin, 0.25f, 1, 0, 0.49f, color),
                new N64RdpHleVertex(
                    origin, origin + size, 0.25f, 0, 1, 0.49f, color),
                tile: 0,
                maximumMipLevel: 0,
                out var triangle));
        Assert.NotNull(triangle);

        return new N64RdpTrace(
            new byte[N64Memory.RdramSize],
            [
                // RGBA16 framebuffer, 16 pixels wide, at 0x1000.
                new N64RdpCommand(0xFF10000F, 0x00001000),
                new N64RdpCommand(0xFE000000, 0x00002000),
                // 16x16, integer-aligned scissor in 10.2 coordinates.
                new N64RdpCommand(0xED000000, 0x00040040),
                // One-cycle SHADE RGB/alpha combiner.
                new N64RdpCommand(0xFC887F10, 0x88FE793C),
                // Sample quad, dither off, antialias and Z update enabled.
                new N64RdpCommand(0xEF002CF0, 0x00000028),
                triangle,
                new N64RdpCommand(0xE9000000, 0),
            ],
            microcode: "Pixel64 native validation",
            omittedHlePrimitiveCommands: 0,
            unsupportedSourceCommands: 0);
    }
}
