namespace PixelDeck.Emulation.N64;

/// <summary>
/// One command submitted to the Nintendo 64 Reality Display Processor.
/// Commands are stored as their native 32-bit words rather than as
/// Fast3D/F3DEX display-list instructions.
/// </summary>
public sealed class N64RdpCommand
{
    public const int MaximumWordCount = 64;
    private readonly uint[] _words;

    public N64RdpCommand(params uint[] words)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (words.Length is < 2 or > MaximumWordCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(words),
                $"An RDP command must contain between 2 and {MaximumWordCount} words.");
        }

        _words = words.ToArray();
    }

    public byte Opcode => (byte)(_words[0] >> 24);

    public ReadOnlyMemory<uint> Words => _words;

    internal uint[] CopyWords() => _words.ToArray();
}
