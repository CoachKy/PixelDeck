using PixelDeck.App.Settings;
using PixelDeck.Emulation.Nes;
using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.Input;

internal static class GamepadInputMapper
{
    public static NesButton ToNesButtons(GamepadButton gamepad, PixelDeckSettings settings)
    {
        var buttons = NesButton.None;
        if (IsPressed(gamepad, settings.AButton)) buttons |= NesButton.A;
        if (IsPressed(gamepad, settings.BButton)) buttons |= NesButton.B;
        if (IsPressed(gamepad, settings.StartButton)) buttons |= NesButton.Start;
        if (IsPressed(gamepad, settings.SelectButton)) buttons |= NesButton.Select;
        if (gamepad.HasFlag(GamepadButton.DPadUp)) buttons |= NesButton.Up;
        if (gamepad.HasFlag(GamepadButton.DPadDown)) buttons |= NesButton.Down;
        if (gamepad.HasFlag(GamepadButton.DPadLeft)) buttons |= NesButton.Left;
        if (gamepad.HasFlag(GamepadButton.DPadRight)) buttons |= NesButton.Right;
        return buttons;
    }

    public static SnesButton ToSnesButtons(GamepadButton gamepad, PixelDeckSettings settings)
    {
        var buttons = SnesButton.None;
        if (IsPressed(gamepad, settings.SnesAButton)) buttons |= SnesButton.A;
        if (IsPressed(gamepad, settings.SnesBButton)) buttons |= SnesButton.B;
        if (IsPressed(gamepad, settings.SnesXButton)) buttons |= SnesButton.X;
        if (IsPressed(gamepad, settings.SnesYButton)) buttons |= SnesButton.Y;
        if (IsPressed(gamepad, settings.SnesLButton)) buttons |= SnesButton.L;
        if (IsPressed(gamepad, settings.SnesRButton)) buttons |= SnesButton.R;
        if (IsPressed(gamepad, settings.SnesStartButton)) buttons |= SnesButton.Start;
        if (IsPressed(gamepad, settings.SnesSelectButton)) buttons |= SnesButton.Select;
        if (gamepad.HasFlag(GamepadButton.DPadUp)) buttons |= SnesButton.Up;
        if (gamepad.HasFlag(GamepadButton.DPadDown)) buttons |= SnesButton.Down;
        if (gamepad.HasFlag(GamepadButton.DPadLeft)) buttons |= SnesButton.Left;
        if (gamepad.HasFlag(GamepadButton.DPadRight)) buttons |= SnesButton.Right;
        return buttons;
    }

    private static bool IsPressed(GamepadButton gamepad, GamepadButton mappedButton) =>
        mappedButton != GamepadButton.None && (gamepad & mappedButton) == mappedButton;
}
