using PixelDeck.App.Input;

namespace PixelDeck.App.Tests;

public sealed class DashboardGamepadReaderTests
{
    [Fact]
    public void AnyControllerCanNavigateTheDashboard()
    {
        var reader = new DashboardGamepadReader();
        var states = new[]
        {
            GamepadButton.None,
            GamepadButton.None,
            GamepadButton.A | GamepadButton.DPadRight,
            GamepadButton.None
        };

        var presses = reader.Track(states, out var buttons);

        Assert.Equal(GamepadButton.A | GamepadButton.DPadRight, presses);
        Assert.Equal(GamepadButton.A | GamepadButton.DPadRight, buttons);
    }

    [Fact]
    public void HeldButtonsAreNotRepeatedAsNewPresses()
    {
        var reader = new DashboardGamepadReader();
        var states = new[]
        {
            GamepadButton.None,
            GamepadButton.B,
            GamepadButton.None,
            GamepadButton.None
        };

        Assert.Equal(GamepadButton.B, reader.Track(states, out _));
        Assert.Equal(GamepadButton.None, reader.Track(states, out var buttons));
        Assert.Equal(GamepadButton.B, buttons);
    }

    [Fact]
    public void ResetPreventsStaleEmulatorInputState()
    {
        var reader = new DashboardGamepadReader();
        var states = new[]
        {
            GamepadButton.Start,
            GamepadButton.None,
            GamepadButton.None,
            GamepadButton.None
        };
        reader.Track(states, out _);

        reader.Reset();

        Assert.Equal(GamepadButton.Start, reader.Track(states, out _));
    }
}
