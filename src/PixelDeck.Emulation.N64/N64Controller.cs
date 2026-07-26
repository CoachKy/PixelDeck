namespace PixelDeck.Emulation.N64;

[Flags]
public enum N64Button : ushort
{
    None = 0,
    CRight = 0x0001,
    CLeft = 0x0002,
    CDown = 0x0004,
    CUp = 0x0008,
    R = 0x0010,
    L = 0x0020,
    DPadRight = 0x0100,
    DPadLeft = 0x0200,
    DPadDown = 0x0400,
    DPadUp = 0x0800,
    Start = 0x1000,
    Z = 0x2000,
    B = 0x4000,
    A = 0x8000
}

public readonly record struct N64ControllerState(N64Button Buttons, sbyte StickX, sbyte StickY)
{
    public static N64ControllerState Neutral => default;

    public uint ToPifWord() =>
        ((uint)(ushort)Buttons << 16) |
        ((uint)(byte)StickX << 8) |
        (byte)StickY;
}
