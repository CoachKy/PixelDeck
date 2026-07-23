using PixelDeck.App.Input;
using PixelDeck.App.Settings;
using PixelDeck.Emulation.Nes;

namespace PixelDeck.App.Tests;

public sealed class ControllerSettingsTests
{
    [Fact]
    public void NintendoAndSuperNintendoHaveIndependentDefaults()
    {
        var settings = new PixelDeckSettings();

        Assert.Equal(GamepadButton.A, settings.AButton);
        Assert.Equal(GamepadButton.X, settings.BButton);
        Assert.Equal(GamepadButton.Start, settings.StartButton);
        Assert.Equal(GamepadButton.Back, settings.SelectButton);
        Assert.False(settings.RemoveNesSpriteLimit);
        Assert.Equal(Mmc3IrqRevision.Auto, settings.Mmc3IrqRevision);
        Assert.Equal(NesPpuRevision.Rp2C02G, settings.NesPpuRevision);
        Assert.False(settings.EnableNesOamDecay);
        Assert.Equal(
            NesOamCorruptionMode.StableCpuPpuAlignment,
            settings.NesOamCorruptionMode);

        Assert.Equal(GamepadButton.B, settings.SnesAButton);
        Assert.Equal(GamepadButton.A, settings.SnesBButton);
        Assert.Equal(GamepadButton.Y, settings.SnesXButton);
        Assert.Equal(GamepadButton.X, settings.SnesYButton);
        Assert.Equal(GamepadButton.LeftShoulder, settings.SnesLButton);
        Assert.Equal(GamepadButton.RightShoulder, settings.SnesRButton);
        Assert.Equal(GamepadButton.Start, settings.SnesStartButton);
        Assert.Equal(GamepadButton.Back, settings.SnesSelectButton);
    }

    [Fact]
    public void RightTriggerHasItsOwnInputFlagForFastForward()
    {
        Assert.NotEqual(GamepadButton.None, GamepadButton.RightTrigger);
        Assert.Equal(
            GamepadButton.None,
            GamepadButton.RightTrigger & (GamepadButton.A | GamepadButton.B | GamepadButton.RightShoulder));
    }
}
