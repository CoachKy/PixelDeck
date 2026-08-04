namespace PixelDeck.Emulation.GameCube;

[Flags]
public enum GameCubeButtons : ushort
{
    None = 0,
    Left = 1 << 0,
    Right = 1 << 1,
    Down = 1 << 2,
    Up = 1 << 3,
    Z = 1 << 4,
    R = 1 << 5,
    L = 1 << 6,
    A = 1 << 8,
    B = 1 << 9,
    X = 1 << 10,
    Y = 1 << 11,
    Start = 1 << 12
}

/// <summary>
/// Represents state and SI report generation for a GameCube controller.
/// </summary>
public sealed class GameCubeController
{
    public GameCubeButtons Buttons { get; set; }
    
    public byte MainStickX { get; set; } = 128;
    public byte MainStickY { get; set; } = 128;
    
    public byte CStickX { get; set; } = 128;
    public byte CStickY { get; set; } = 128;
    
    public byte TriggerL { get; set; }
    public byte TriggerR { get; set; }

    public bool IsConnected { get; set; } = true;

    /// <summary>
    /// Encodes current input state into an 8-byte GameCube Serial Interface (SI) status buffer.
    /// </summary>
    public ulong GetSiReport()
    {
        if (!IsConnected)
        {
            return 0x0000000000000000UL; // No device connected
        }

        var btn = (ushort)Buttons;
        ulong report = 0;

        report |= (ulong)(btn & 0xFFFF) << 48;
        report |= (ulong)MainStickX << 40;
        report |= (ulong)MainStickY << 32;
        report |= (ulong)CStickX << 24;
        report |= (ulong)CStickY << 16;
        report |= (ulong)TriggerL << 8;
        report |= (ulong)TriggerR;

        return report;
    }

    public void Reset()
    {
        Buttons = GameCubeButtons.None;
        MainStickX = 128;
        MainStickY = 128;
        CStickX = 128;
        CStickY = 128;
        TriggerL = 0;
        TriggerR = 0;
    }
}
