using System.Security.Cryptography;

namespace PixelDeck.Emulation.N64;

/// <summary>
/// A deterministic before/after view of one native RDP memory target.
/// Hashes make repeat-run comparisons compact while ChangedBytes proves that
/// the native renderer actually touched the expected target.
/// </summary>
public sealed class ParallelRdpBufferDelta
{
    private ParallelRdpBufferDelta(
        string name,
        uint address,
        int length,
        string inputSha256,
        string outputSha256,
        int changedBytes)
    {
        Name = name;
        Address = address;
        Length = length;
        InputSha256 = inputSha256;
        OutputSha256 = outputSha256;
        ChangedBytes = changedBytes;
    }

    public string Name { get; }

    public uint Address { get; }

    public int Length { get; }

    public string InputSha256 { get; }

    public string OutputSha256 { get; }

    public int ChangedBytes { get; }

    public bool Changed => ChangedBytes > 0;

    internal static ParallelRdpBufferDelta Create(
        string name,
        uint address,
        ReadOnlySpan<byte> input,
        ReadOnlySpan<byte> output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (input.Length != output.Length)
        {
            throw new ArgumentException(
                "Native RDP input and output buffers must have equal lengths.");
        }

        var changedBytes = 0;
        for (var index = 0; index < input.Length; index++)
        {
            if (input[index] != output[index])
            {
                changedBytes++;
            }
        }

        return new ParallelRdpBufferDelta(
            name,
            address,
            input.Length,
            Convert.ToHexString(SHA256.HashData(input)),
            Convert.ToHexString(SHA256.HashData(output)),
            changedBytes);
    }
}

internal static class N64RdpOutputLayoutParser
{
    internal static N64RdpOutputLayout Analyze(
        IReadOnlyList<N64RdpCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        uint? colorAddress = null;
        uint? depthAddress = null;
        var colorWidth = 0;
        var colorSize = 0;
        var scissorTop = 0;
        var scissorBottom = 0;

        foreach (var command in commands)
        {
            var words = command.Words.Span;
            switch (command.Opcode)
            {
                case 0xFF:
                    colorAddress = words[1] & 0x00FF_FFFF;
                    colorSize = (int)((words[0] >> 19) & 3);
                    colorWidth = (int)(words[0] & 0xFFF) + 1;
                    break;
                case 0xFE:
                    depthAddress = words[1] & 0x00FF_FFFF;
                    break;
                case 0xED:
                    scissorTop = (int)(words[0] & 0xFFF) / 4;
                    scissorBottom = (int)(words[1] & 0xFFF) / 4;
                    break;
            }
        }

        if (colorAddress is null ||
            colorWidth <= 0 ||
            scissorBottom <= scissorTop)
        {
            return default;
        }

        var colorBitsPerPixel = 4 << colorSize;
        var colorRowBytes = checked(
            ((colorWidth * colorBitsPerPixel) + 7) / 8);
        var rows = scissorBottom - scissorTop;
        var framebuffer = CreateRegion(
            colorAddress.Value,
            scissorTop,
            colorRowBytes,
            rows);
        var depthBuffer = depthAddress is null
            ? null
            : CreateRegion(
                depthAddress.Value,
                scissorTop,
                checked(colorWidth * 2),
                rows);

        return new N64RdpOutputLayout(framebuffer, depthBuffer);
    }

    private static N64RdpMemoryRegion? CreateRegion(
        uint imageAddress,
        int firstRow,
        int rowBytes,
        int rows)
    {
        var start = checked((long)imageAddress + ((long)firstRow * rowBytes));
        var length = checked((long)rowBytes * rows);
        if (start < 0 ||
            length <= 0 ||
            start + length > N64Memory.RdramSize)
        {
            return null;
        }

        return new N64RdpMemoryRegion(
            checked((uint)start),
            checked((int)length));
    }
}

internal readonly record struct N64RdpOutputLayout(
    N64RdpMemoryRegion? Framebuffer,
    N64RdpMemoryRegion? DepthBuffer);

internal readonly record struct N64RdpMemoryRegion(uint Address, int Length);
