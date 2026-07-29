namespace PixelDeck.Emulation.N64;

/// <summary>
/// Executes Nintendo 64 audio RSP tasks for <see cref="N64Machine"/>.
/// The machine owns task scheduling while the backend owns audio-microcode
/// state, matching the same replaceable-component boundary used for graphics.
/// </summary>
public interface IN64AudioBackend
{
    string Name { get; }

    long CommandsProcessed { get; }

    long UnsupportedCommands { get; }

    IReadOnlyDictionary<uint, long> UnsupportedCommandCounts { get; }

    void Execute(N64RspTask task);

    void SaveState(BinaryWriter writer);

    void LoadState(BinaryReader reader);
}
