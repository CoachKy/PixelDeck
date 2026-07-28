namespace PixelDeck.Emulation.N64;

/// <summary>
/// Executes Nintendo 64 graphics tasks for <see cref="N64Machine"/>.
/// Keeping this boundary independent from the current Fast3D software
/// renderer allows a conformant RDP backend to be introduced without
/// coupling the machine scheduler to one renderer implementation.
/// </summary>
public interface IN64GraphicsBackend
{
    string Name { get; }

    bool RasterizationEnabled { get; set; }

    void Execute(N64RspTask task);
}
