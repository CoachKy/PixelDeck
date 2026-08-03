namespace PixelDeck.Emulation.N64;

/// <summary>
/// Scheduler-owned information about the graphics microcode described by an
/// <see cref="N64RspTask"/>. Keeping this metadata separate from the renderer
/// allows the machine to own the RSP task classification step rather than
/// deferring it to backend-specific code.
/// </summary>
public sealed record N64GraphicsTaskProfile(
    string? MicrocodeBanner,
    uint MicrocodeCrc32,
    string DetectedMicrocodeName)
{
    public static N64GraphicsTaskProfile FromTask(
        N64Memory memory,
        N64RspTask task,
        string current = "Fast3d")
    {
        var banner = ReadMicrocodeBanner(memory, task);
        var crc32 = CalculateMicrocodeCrc32(memory, task);
        var detectedMicrocode = ClassifyMicrocodeName(banner, crc32, current);
        return new N64GraphicsTaskProfile(banner, crc32, detectedMicrocode);
    }

    public static string ClassifyMicrocodeName(
        string? banner,
        uint crc32,
        string current = "Fast3d")
    {
        if (crc32 == 0xDA51CCDB)
        {
            return Fast3dRenderer.N64Microcode.F5Rogue.ToString();
        }

        if (crc32 is 0x94C4C833 or 0xD17906E2)
        {
            return Fast3dRenderer.N64Microcode.F3dBeta.ToString();
        }

        if (banner is null)
        {
            return current;
        }

        if (banner.Contains("F3DZEX", StringComparison.Ordinal) ||
            (banner.Contains("F3DEX", StringComparison.Ordinal) &&
             banner.Contains(" 2.", StringComparison.Ordinal)))
        {
            return Fast3dRenderer.N64Microcode.F3dex2.ToString();
        }

        return banner.Contains("F3DEX", StringComparison.Ordinal)
            ? Fast3dRenderer.N64Microcode.F3dex.ToString()
            : Fast3dRenderer.N64Microcode.Fast3d.ToString();
    }

    private static uint CalculateMicrocodeCrc32(N64Memory memory, N64RspTask task)
    {
        const int microcodeTextLength = 4096;
        var address = task.MicrocodePointer & 0x7FFFFF;
        if (address + microcodeTextLength > N64Memory.RdramSize)
        {
            return 0;
        }

        return Fast3dRenderer.ComputeStrictWordSwappedCrc32(
            memory.Rdram.AsSpan((int)address, microcodeTextLength));
    }

    private static string? ReadMicrocodeBanner(N64Memory memory, N64RspTask task)
    {
        var address = task.MicrocodeDataPointer & 0x7FFFFF;
        var length = (int)Math.Min(task.MicrocodeDataSize, 2048);
        if (length <= 0 || address + length > N64Memory.RdramSize)
        {
            return null;
        }

        var data = memory.Rdram.AsSpan((int)address, length);
        ReadOnlySpan<byte> marker = "RSP Gfx ucode"u8;
        var start = data.IndexOf(marker);
        if (start < 0)
        {
            return null;
        }

        var text = data[start..];
        var end = 0;
        while (end < text.Length && end < 96 && text[end] is >= 0x20 and <= 0x7E)
        {
            end++;
        }

        return System.Text.Encoding.ASCII.GetString(text[..end]);
    }
}
