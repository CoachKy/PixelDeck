namespace PixelDeck.Emulation.N64;

[Flags]
public enum N64Button : ushort
{
    None = 0,
    DPadRight = 0x0001,
    DPadLeft = 0x0002,
    DPadDown = 0x0004,
    DPadUp = 0x0008,
    Start = 0x0010,
    Z = 0x0020,
    B = 0x0040,
    A = 0x0080,
    CRight = 0x0100,
    CLeft = 0x0200,
    CDown = 0x0400,
    CUp = 0x0800,
    R = 0x1000,
    L = 0x2000
}

public readonly record struct N64ControllerState(N64Button Buttons, sbyte StickX, sbyte StickY)
{
    public static N64ControllerState Neutral => default;

    public uint ToPifWord() =>
        ((uint)(ushort)Buttons << 16) |
        ((uint)(byte)StickX << 8) |
        (byte)StickY;
}
