using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PixelDeck.Emulation.N64;

public static class N64GraphicsReplay
{
    public static N64GraphicsReplayResult Replay(
        N64GraphicsTaskCapture capture,
        Func<N64Memory, IN64GraphicsBackend>? backendFactory = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var memory = CreateReplayMemory();
        capture.Rdram.Span.CopyTo(memory.Rdram);
        var backend = backendFactory?.Invoke(memory) ?? new Fast3dRenderer(memory);
        if (backend is null)
        {
            throw new InvalidOperationException("The graphics backend factory returned null.");
        }

        backend.Execute(capture.Task);
        var fast3d = backend as Fast3dRenderer;
        return new N64GraphicsReplayResult(
            backend.Name,
            memory.Rdram,
            fast3d?.RdpState,
            fast3d?.CommandsProcessed ?? 0,
            fast3d?.UnsupportedCommands ?? 0,
            fast3d?.ColorImageAddress ?? 0,
            fast3d?.ColorImageWidth ?? 0,
            fast3d?.ColorImageSize ?? 0);
    }

    internal static N64Memory CreateReplayMemory()
    {
        // Graphics replay only needs captured RDRAM and the task descriptor.
        // A minimal synthetic header avoids requiring the original cartridge.
        var image = new byte[0x1000];
        BinaryPrimitives.WriteUInt32BigEndian(image, 0x80371240);
        image[0x3B] = (byte)'N';
        image[0x3C] = (byte)'R';
        image[0x3D] = (byte)'P';
        image[0x3E] = (byte)'E';
        return new N64Memory(N64Cartridge.FromBytes(image));
    }
}

public sealed class N64GraphicsReplayResult
{
    private readonly byte[] _rdram;

    internal N64GraphicsReplayResult(
        string backendName,
        byte[] rdram,
        N64RdpStateSnapshot? rdpState,
        long commandsProcessed,
        long unsupportedCommands,
        uint colorImageAddress,
        int colorImageWidth,
        int colorImageSize)
    {
        BackendName = backendName;
        _rdram = rdram;
        RdpState = rdpState;
        CommandsProcessed = commandsProcessed;
        UnsupportedCommands = unsupportedCommands;
        ColorImageAddress = colorImageAddress;
        ColorImageWidth = colorImageWidth;
        ColorImageSize = colorImageSize;
        RdramSha256 = Convert.ToHexString(SHA256.HashData(rdram));
    }

    public string BackendName { get; }

    public ReadOnlyMemory<byte> Rdram => _rdram;

    public string RdramSha256 { get; }

    public N64RdpStateSnapshot? RdpState { get; }

    public long CommandsProcessed { get; }

    public long UnsupportedCommands { get; }

    public uint ColorImageAddress { get; }

    public int ColorImageWidth { get; }

    public int ColorImageSize { get; }
}
