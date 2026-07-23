namespace PixelDeck.Emulation.Nes;

public enum Mmc3IrqRevision
{
    Auto,
    Sharp,
    Nec
}

public enum NesPpuRevision
{
    Rp2C02G,
    Rp2C02BOrEarlier
}

public enum NesOamCorruptionMode
{
    StableCpuPpuAlignment,
    WorstCase
}

public sealed record NesEmulationOptions
{
    public bool RemoveSpriteLimit { get; init; }

    public Mmc3IrqRevision Mmc3IrqRevision { get; init; } = Mmc3IrqRevision.Auto;

    public NesPpuRevision PpuRevision { get; init; } = NesPpuRevision.Rp2C02G;

    public bool EnableOamDecay { get; init; }

    public NesOamCorruptionMode OamCorruptionMode { get; init; } =
        NesOamCorruptionMode.StableCpuPpuAlignment;
}
