using System.Security.Cryptography;

namespace PixelDeck.Emulation.N64;

public static class N64RdpReplay
{
    public static N64RdpReplayResult Replay(N64RdpTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var memory = N64GraphicsReplay.CreateReplayMemory();
        trace.Rdram.Span.CopyTo(memory.Rdram);
        var renderer = new Fast3dRenderer(memory);
        renderer.ReplayRdpCommands(trace.Commands);
        return new N64RdpReplayResult(
            renderer.Name,
            memory.Rdram,
            renderer.RdpState,
            renderer.UnsupportedCommands,
            renderer.ColorImageAddress,
            renderer.ColorImageWidth,
            renderer.ColorImageSize,
            trace.IsComplete);
    }
}

public sealed class N64RdpReplayResult
{
    private readonly byte[] _rdram;

    internal N64RdpReplayResult(
        string backendName,
        byte[] rdram,
        N64RdpStateSnapshot rdpState,
        long unsupportedCommands,
        uint colorImageAddress,
        int colorImageWidth,
        int colorImageSize,
        bool sourceTraceComplete)
    {
        BackendName = backendName;
        _rdram = rdram;
        RdpState = rdpState;
        UnsupportedCommands = unsupportedCommands;
        ColorImageAddress = colorImageAddress;
        ColorImageWidth = colorImageWidth;
        ColorImageSize = colorImageSize;
        SourceTraceComplete = sourceTraceComplete;
        RdramSha256 = Convert.ToHexString(SHA256.HashData(rdram));
    }

    public string BackendName { get; }

    public ReadOnlyMemory<byte> Rdram => _rdram;

    public string RdramSha256 { get; }

    public N64RdpStateSnapshot RdpState { get; }

    public long UnsupportedCommands { get; }

    public uint ColorImageAddress { get; }

    public int ColorImageWidth { get; }

    public int ColorImageSize { get; }

    public bool SourceTraceComplete { get; }
}
