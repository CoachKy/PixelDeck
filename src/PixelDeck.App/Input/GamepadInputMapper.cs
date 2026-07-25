using PixelDeck.App.Settings;
using PixelDeck.Emulation.Nes;
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

    private static bool IsPressed(GamepadButton gamepad, GamepadButton mappedButton) =>
        mappedButton != GamepadButton.None && (gamepad & mappedButton) == mappedButton;
}
