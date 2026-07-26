using PixelDeck.App.Settings;
using PixelDeck.Emulation.Nes;
using PixelDeck.Emulation.N64;
using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.Input;

internal static class GamepadInputMapper
{
    public static NesButton ToNesButtons(
        GamepadButton gamepad,
        PixelDeckSettings settings,
        bool playerTwo = false)
    {
        var aButton = playerTwo ? settings.PlayerTwoAButton : settings.AButton;
        var bButton = playerTwo ? settings.PlayerTwoBButton : settings.BButton;
        var startButton = playerTwo ? settings.PlayerTwoStartButton : settings.StartButton;
        var selectButton = playerTwo ? settings.PlayerTwoSelectButton : settings.SelectButton;
        var buttons = NesButton.None;
        if (IsPressed(gamepad, aButton)) buttons |= NesButton.A;
        if (IsPressed(gamepad, bButton)) buttons |= NesButton.B;
        if (IsPressed(gamepad, startButton)) buttons |= NesButton.Start;
        if (IsPressed(gamepad, selectButton)) buttons |= NesButton.Select;
        if (gamepad.HasFlag(GamepadButton.DPadUp)) buttons |= NesButton.Up;
        if (gamepad.HasFlag(GamepadButton.DPadDown)) buttons |= NesButton.Down;
        if (gamepad.HasFlag(GamepadButton.DPadLeft)) buttons |= NesButton.Left;
        if (gamepad.HasFlag(GamepadButton.DPadRight)) buttons |= NesButton.Right;
        return buttons;
    }

    public static SnesButton ToSnesButtons(
        GamepadButton gamepad,
        PixelDeckSettings settings,
        bool playerTwo = false)
    {
        var aButton = playerTwo ? settings.PlayerTwoSnesAButton : settings.SnesAButton;
        var bButton = playerTwo ? settings.PlayerTwoSnesBButton : settings.SnesBButton;
        var xButton = playerTwo ? settings.PlayerTwoSnesXButton : settings.SnesXButton;
        var yButton = playerTwo ? settings.PlayerTwoSnesYButton : settings.SnesYButton;
        var lButton = playerTwo ? settings.PlayerTwoSnesLButton : settings.SnesLButton;
        var rButton = playerTwo ? settings.PlayerTwoSnesRButton : settings.SnesRButton;
        var startButton = playerTwo ? settings.PlayerTwoSnesStartButton : settings.SnesStartButton;
        var selectButton = playerTwo ? settings.PlayerTwoSnesSelectButton : settings.SnesSelectButton;
        var buttons = SnesButton.None;
        if (IsPressed(gamepad, aButton)) buttons |= SnesButton.A;
        if (IsPressed(gamepad, bButton)) buttons |= SnesButton.B;
        if (IsPressed(gamepad, xButton)) buttons |= SnesButton.X;
        if (IsPressed(gamepad, yButton)) buttons |= SnesButton.Y;
        if (IsPressed(gamepad, lButton)) buttons |= SnesButton.L;
        if (IsPressed(gamepad, rButton)) buttons |= SnesButton.R;
        if (IsPressed(gamepad, startButton)) buttons |= SnesButton.Start;
        if (IsPressed(gamepad, selectButton)) buttons |= SnesButton.Select;
        if (gamepad.HasFlag(GamepadButton.DPadUp)) buttons |= SnesButton.Up;
        if (gamepad.HasFlag(GamepadButton.DPadDown)) buttons |= SnesButton.Down;
        if (gamepad.HasFlag(GamepadButton.DPadLeft)) buttons |= SnesButton.Left;
        if (gamepad.HasFlag(GamepadButton.DPadRight)) buttons |= SnesButton.Right;
        return buttons;
    }

    public static N64ControllerState ToN64Controller(
        GamepadButton gamepad,
        PixelDeckSettings settings,
        bool playerTwo = false)
        => ToN64Controller(
            new GamepadState(gamepad, 0, 0, 0, 0),
            settings,
            playerTwo);

    public static N64ControllerState ToN64Controller(
        GamepadState gamepad,
        PixelDeckSettings settings,
        bool playerTwo = false)
    {
        var gamepadButtons = gamepad.Buttons;
        var aButton = playerTwo ? settings.PlayerTwoAButton : settings.AButton;
        var bButton = playerTwo ? settings.PlayerTwoBButton : settings.BButton;
        var startButton = playerTwo ? settings.PlayerTwoStartButton : settings.StartButton;
        var buttons = N64Button.None;
        if (IsPressed(gamepadButtons, aButton)) buttons |= N64Button.A;
        if (IsPressed(gamepadButtons, bButton)) buttons |= N64Button.B;
        if (IsPressed(gamepadButtons, startButton)) buttons |= N64Button.Start;
        if (gamepadButtons.HasFlag(GamepadButton.LeftTrigger)) buttons |= N64Button.Z;
        if (gamepadButtons.HasFlag(GamepadButton.LeftShoulder)) buttons |= N64Button.L;
        if (gamepadButtons.HasFlag(GamepadButton.RightShoulder)) buttons |= N64Button.R;
        if (gamepadButtons.HasFlag(GamepadButton.DPadUp)) buttons |= N64Button.DPadUp;
        if (gamepadButtons.HasFlag(GamepadButton.DPadDown)) buttons |= N64Button.DPadDown;
        if (gamepadButtons.HasFlag(GamepadButton.DPadLeft)) buttons |= N64Button.DPadLeft;
        if (gamepadButtons.HasFlag(GamepadButton.DPadRight)) buttons |= N64Button.DPadRight;

        var stickX = ScaleN64Axis(gamepad.LeftX);
        var stickY = ScaleN64Axis(gamepad.LeftY);
        if (stickX == 0)
        {
            stickX = gamepadButtons.HasFlag(GamepadButton.DPadLeft)
                ? (sbyte)-80
                : gamepadButtons.HasFlag(GamepadButton.DPadRight) ? (sbyte)80 : (sbyte)0;
        }

        if (stickY == 0)
        {
            stickY = gamepadButtons.HasFlag(GamepadButton.DPadDown)
                ? (sbyte)-80
                : gamepadButtons.HasFlag(GamepadButton.DPadUp) ? (sbyte)80 : (sbyte)0;
        }

        const short cameraThreshold = 12_000;
        if (gamepad.RightY > cameraThreshold || gamepadButtons.HasFlag(GamepadButton.Y))
            buttons |= N64Button.CUp;
        if (gamepad.RightY < -cameraThreshold || gamepadButtons.HasFlag(GamepadButton.B))
            buttons |= N64Button.CDown;
        if (gamepad.RightX < -cameraThreshold || gamepadButtons.HasFlag(GamepadButton.LeftThumb))
            buttons |= N64Button.CLeft;
        if (gamepad.RightX > cameraThreshold || gamepadButtons.HasFlag(GamepadButton.RightThumb))
            buttons |= N64Button.CRight;
        return new N64ControllerState(buttons, stickX, stickY);
    }

    private static sbyte ScaleN64Axis(short value)
    {
        const int deadZone = 4_096;
        var magnitude = Math.Abs((int)value);
        if (magnitude <= deadZone)
        {
            return 0;
        }

        var scaled = Math.Clamp(
            (magnitude - deadZone) * 80 / (short.MaxValue - deadZone),
            1,
            80);
        return (sbyte)(value < 0 ? -scaled : scaled);
    }

    private static bool IsPressed(GamepadButton gamepad, GamepadButton mappedButton) =>
        mappedButton != GamepadButton.None && (gamepad & mappedButton) == mappedButton;
}
