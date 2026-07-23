using System.Runtime.InteropServices;

namespace PixelDeck.App.Input;

[Flags]
public enum GamepadButton
{
    None = 0,
    DPadLeft = 1 << 0,
    DPadRight = 1 << 1,
    X = 1 << 2,
    Y = 1 << 3,
    Start = 1 << 4,
    DPadUp = 1 << 5,
    DPadDown = 1 << 6,
    A = 1 << 7,
    B = 1 << 8,
    Back = 1 << 9,
    LeftShoulder = 1 << 10,
    RightShoulder = 1 << 11,
    LeftThumb = 1 << 12,
    RightThumb = 1 << 13,
    Guide = 1 << 14,
    LeftTrigger = 1 << 15,
    RightTrigger = 1 << 16
}

internal sealed class WindowsGamepad
{
    private const ushort XInputDPadLeft = 0x0004;
    private const ushort XInputDPadRight = 0x0008;
    private const ushort XInputStart = 0x0010;
    private const ushort XInputBack = 0x0020;
    private const ushort XInputLeftThumb = 0x0040;
    private const ushort XInputRightThumb = 0x0080;
    private const ushort XInputLeftShoulder = 0x0100;
    private const ushort XInputRightShoulder = 0x0200;
    private const ushort XInputGuide = 0x0400;
    private const ushort XInputA = 0x1000;
    private const ushort XInputB = 0x2000;
    private const ushort XInputX = 0x4000;
    private const ushort XInputY = 0x8000;
    private const ushort XInputDPadUp = 0x0001;
    private const ushort XInputDPadDown = 0x0002;
    private const short StickThreshold = 18000;
    private const byte TriggerThreshold = 30;

    private GamepadButton _previousButtons;
    private static readonly XInputGetStateDelegate? ExtendedGetState = LoadExtendedGetState();
    private static nint _xInputModule;

    public int UserIndex { get; set; }

    public bool IsConnected => TryReadState(out _);

    public GamepadButton ReadNewPresses()
    {
        var buttons = ReadButtons();
        var newPresses = buttons & ~_previousButtons;
        _previousButtons = buttons;
        return newPresses;
    }

    public GamepadButton ReadButtons()
    {
        if (!TryReadState(out var state))
        {
            return GamepadButton.None;
        }

        return Translate(state.Gamepad);
    }

    private static GamepadButton Translate(XInputGamepad gamepad)
    {
        var result = GamepadButton.None;

        if ((gamepad.Buttons & XInputDPadLeft) != 0 || gamepad.ThumbLX < -StickThreshold)
        {
            result |= GamepadButton.DPadLeft;
        }

        if ((gamepad.Buttons & XInputDPadRight) != 0 || gamepad.ThumbLX > StickThreshold)
        {
            result |= GamepadButton.DPadRight;
        }

        if ((gamepad.Buttons & XInputDPadUp) != 0 || gamepad.ThumbLY > StickThreshold)
        {
            result |= GamepadButton.DPadUp;
        }

        if ((gamepad.Buttons & XInputDPadDown) != 0 || gamepad.ThumbLY < -StickThreshold)
        {
            result |= GamepadButton.DPadDown;
        }

        if ((gamepad.Buttons & XInputA) != 0)
        {
            result |= GamepadButton.A;
        }

        if ((gamepad.Buttons & XInputB) != 0)
        {
            result |= GamepadButton.B;
        }

        if ((gamepad.Buttons & XInputBack) != 0)
        {
            result |= GamepadButton.Back;
        }

        if ((gamepad.Buttons & XInputX) != 0)
        {
            result |= GamepadButton.X;
        }

        if ((gamepad.Buttons & XInputY) != 0)
        {
            result |= GamepadButton.Y;
        }

        if ((gamepad.Buttons & XInputStart) != 0)
        {
            result |= GamepadButton.Start;
        }

        if ((gamepad.Buttons & XInputLeftShoulder) != 0)
        {
            result |= GamepadButton.LeftShoulder;
        }

        if ((gamepad.Buttons & XInputRightShoulder) != 0)
        {
            result |= GamepadButton.RightShoulder;
        }

        if ((gamepad.Buttons & XInputLeftThumb) != 0)
        {
            result |= GamepadButton.LeftThumb;
        }

        if ((gamepad.Buttons & XInputRightThumb) != 0)
        {
            result |= GamepadButton.RightThumb;
        }

        if ((gamepad.Buttons & XInputGuide) != 0)
        {
            result |= GamepadButton.Guide;
        }

        if (gamepad.LeftTrigger > TriggerThreshold)
        {
            result |= GamepadButton.LeftTrigger;
        }

        if (gamepad.RightTrigger > TriggerThreshold)
        {
            result |= GamepadButton.RightTrigger;
        }

        return result;
    }

    private bool TryReadState(out XInputState state)
    {
        state = default;
        if (!OperatingSystem.IsWindows() || UserIndex is < 0 or > 3)
        {
            return false;
        }

        return (ExtendedGetState?.Invoke((uint)UserIndex, out state) ?? XInputGetState((uint)UserIndex, out state)) == 0;
    }

    private static XInputGetStateDelegate? LoadExtendedGetState()
    {
        if (!OperatingSystem.IsWindows() || !NativeLibrary.TryLoad("xinput1_4.dll", out _xInputModule))
        {
            return null;
        }

        var address = GetProcAddress(_xInputModule, (nint)100);
        return address == 0
            ? null
            : Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(address);
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    [DllImport("kernel32.dll")]
    private static extern nint GetProcAddress(nint module, nint procedureName);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint XInputGetStateDelegate(uint userIndex, out XInputState state);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }
}
