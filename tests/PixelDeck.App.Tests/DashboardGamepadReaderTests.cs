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
    public void ResetDoesNotTurnAHeldButtonIntoAFreshPress()
    {
        // The dashboard resets when a game exits, and whatever button ended the
        // game is often still down. Reporting it as a new press made the
        // dashboard act on it immediately - an A press there launches a game, so
        // quitting with a slightly long press relaunched the game just quit.
        var reader = new DashboardGamepadReader();
        var held = new[]
        {
            GamepadButton.A,
            GamepadButton.None,
            GamepadButton.None,
            GamepadButton.None
        };
        reader.Track(held, out _);

        reader.Reset();

        Assert.Equal(GamepadButton.None, reader.Track(held, out var buttons));
        // The button is still reported as down; it simply is not a new press.
        Assert.Equal(GamepadButton.A, buttons);
    }

    [Fact]
    public void AfterResetTheButtonCountsAgainOnceReleasedAndPressed()
    {
        var reader = new DashboardGamepadReader();
        var held = new[]
        {
            GamepadButton.A,
            GamepadButton.None,
            GamepadButton.None,
            GamepadButton.None
        };
        var released = new[]
        {
            GamepadButton.None,
            GamepadButton.None,
            GamepadButton.None,
            GamepadButton.None
        };

        reader.Reset();
        reader.Track(held, out _);
        reader.Track(released, out _);

        // Suppression lasts exactly as long as the button stays down.
        Assert.Equal(GamepadButton.A, reader.Track(held, out _));
    }

    [Fact]
    public void ResetSuppressesEveryButtonNotJustTheOneThatQuit()
    {
        var reader = new DashboardGamepadReader();
        var many = new[]
        {
            GamepadButton.A | GamepadButton.Start,
            GamepadButton.B,
            GamepadButton.None,
            GamepadButton.None
        };

        reader.Reset();

        Assert.Equal(GamepadButton.None, reader.Track(many, out _));
    }
}
