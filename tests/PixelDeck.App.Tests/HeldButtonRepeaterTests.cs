using PixelDeck.App.Input;

namespace PixelDeck.App.Tests;

public sealed class HeldButtonRepeaterTests
{
    [Fact]
    public void HeldButton_RepeatsAfterDelayAtConfiguredInterval()
    {
        var repeater = new HeldButtonRepeater(
            GamepadButton.DPadUp | GamepadButton.DPadDown,
            initialDelayMilliseconds: 350,
            repeatIntervalMilliseconds: 100);

        Assert.Equal(GamepadButton.None, repeater.ReadRepeat(GamepadButton.DPadDown, 1_000));
        Assert.Equal(GamepadButton.None, repeater.ReadRepeat(GamepadButton.DPadDown, 1_349));
        Assert.Equal(GamepadButton.DPadDown, repeater.ReadRepeat(GamepadButton.DPadDown, 1_350));
        Assert.Equal(GamepadButton.None, repeater.ReadRepeat(GamepadButton.DPadDown, 1_449));
        Assert.Equal(GamepadButton.DPadDown, repeater.ReadRepeat(GamepadButton.DPadDown, 1_450));
    }

    [Fact]
    public void DirectionChange_StartsANewInitialDelay()
    {
        var repeater = new HeldButtonRepeater(
            GamepadButton.DPadUp | GamepadButton.DPadDown,
            initialDelayMilliseconds: 350,
            repeatIntervalMilliseconds: 100);

        Assert.Equal(GamepadButton.None, repeater.ReadRepeat(GamepadButton.DPadUp, 100));
        Assert.Equal(GamepadButton.DPadUp, repeater.ReadRepeat(GamepadButton.DPadUp, 450));
        Assert.Equal(GamepadButton.None, repeater.ReadRepeat(GamepadButton.DPadDown, 451));
        Assert.Equal(GamepadButton.None, repeater.ReadRepeat(GamepadButton.DPadDown, 800));
        Assert.Equal(GamepadButton.DPadDown, repeater.ReadRepeat(GamepadButton.DPadDown, 801));
    }

    [Fact]
    public void ReleaseOrOpposingDirections_CancelThePendingRepeat()
    {
        var repeater = new HeldButtonRepeater(
            GamepadButton.DPadUp | GamepadButton.DPadDown,
            initialDelayMilliseconds: 350,
            repeatIntervalMilliseconds: 100);

        Assert.Equal(GamepadButton.None, repeater.ReadRepeat(GamepadButton.DPadUp, 100));
        Assert.Equal(GamepadButton.None, repeater.ReadRepeat(GamepadButton.None, 200));
        Assert.Equal(GamepadButton.None, repeater.ReadRepeat(GamepadButton.DPadUp, 300));
        Assert.Equal(
            GamepadButton.None,
            repeater.ReadRepeat(GamepadButton.DPadUp | GamepadButton.DPadDown, 700));
        Assert.Equal(GamepadButton.None, repeater.ReadRepeat(GamepadButton.DPadDown, 701));
        Assert.Equal(GamepadButton.DPadDown, repeater.ReadRepeat(GamepadButton.DPadDown, 1_051));
    }
}
