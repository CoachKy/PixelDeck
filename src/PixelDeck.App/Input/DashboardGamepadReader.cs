namespace PixelDeck.App.Input;

/// <summary>
/// Treats every connected controller as a valid dashboard remote. Player
/// assignments only matter after a game launches; requiring the Player 1
/// assignment here made an otherwise working controller appear dead.
/// </summary>
internal sealed class DashboardGamepadReader
{
    private readonly GamepadButton[] _previousButtons =
        new GamepadButton[GamepadManager.MaximumControllers];

    public GamepadButton ReadNewPresses(out GamepadButton buttons)
    {
        Span<GamepadButton> states =
            stackalloc GamepadButton[GamepadManager.MaximumControllers];
        var connections = GamepadManager.Shared.ReadConnections();
        for (var index = 0; index < states.Length; index++)
        {
            states[index] = connections.IsConnected(index)
                ? GamepadManager.Shared.ReadButtons(index)
                : GamepadButton.None;
        }

        return Track(states, out buttons);
    }

    internal GamepadButton Track(
        ReadOnlySpan<GamepadButton> states,
        out GamepadButton buttons)
    {
        buttons = GamepadButton.None;
        var presses = GamepadButton.None;
        for (var index = 0; index < _previousButtons.Length; index++)
        {
            var current = index < states.Length
                ? states[index]
                : GamepadButton.None;
            buttons |= current;
            presses |= current & ~_previousButtons[index];
            _previousButtons[index] = current;
        }

        return presses;
    }

    /// <summary>
    /// Drops the press history, treating everything as though it were already
    /// held so a button has to be released before it counts again.
    /// </summary>
    /// <remarks>
    /// Called when the dashboard takes back control after a game exits. Clearing
    /// the history looked equivalent and was not: the next poll compares against
    /// "nothing was pressed", so a button still physically down registered as a
    /// fresh press. The button holding it down is usually the one that just left
    /// the game, and a fresh A on the dashboard launches a game - so holding the
    /// button a moment too long relaunched the game the player had just quit.
    /// </remarks>
    public void Reset() => Array.Fill(_previousButtons, ~GamepadButton.None);
}
