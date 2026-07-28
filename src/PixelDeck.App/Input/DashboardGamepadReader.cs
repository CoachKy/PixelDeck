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

    public void Reset() => Array.Clear(_previousButtons);
}
