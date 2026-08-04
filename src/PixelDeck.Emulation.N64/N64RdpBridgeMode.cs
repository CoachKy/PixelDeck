namespace PixelDeck.Emulation.N64;

/// <summary>
/// Selects how decoded high-level graphics reach the low-level RDP engine.
/// </summary>
public enum N64RdpBridgeMode
{
    /// <summary>
    /// The low-level engine receives nothing from high-level microcode. It is
    /// still fed by DP DMA, which only titles that drive the RDP directly
    /// perform.
    /// </summary>
    Off = 0,

    /// <summary>
    /// Fast3D rasterizes as usual and the same primitives are additionally
    /// lowered into native RDP packets and delivered to the low-level engine.
    /// Output is unchanged, so this is the safe mode for measuring how much
    /// traffic the engine actually receives and what it can handle.
    /// </summary>
    Mirror = 1,

    /// <summary>
    /// Only the low-level engine draws. Fast3D still decodes the display list
    /// and lowers primitives, but does not rasterize. This is the end state,
    /// and is only useful once the engine can rasterize triangles.
    /// </summary>
    Exclusive = 2
}
