namespace PixelDeck.App.Input;

internal sealed class GamepadReader
{
    private int _userIndex;
    private GamepadButton _previousButtons;

    public int UserIndex
    {
        get => _userIndex;
        set
        {
            var normalized = Math.Clamp(value, 0, GamepadManager.MaximumControllers - 1);
            if (_userIndex == normalized)
            {
                return;
            }

            _userIndex = normalized;
            _previousButtons = GamepadButton.None;
        }
    }

    public bool IsConnected => GamepadManager.Shared.ReadConnections().IsConnected(UserIndex);

    public GamepadButton ReadNewPresses()
    {
        return ReadNewPresses(out _);
    }

    public GamepadButton ReadNewPresses(out GamepadButton buttons)
    {
        buttons = ReadButtons();
        var newPresses = buttons & ~_previousButtons;
        _previousButtons = buttons;
        return newPresses;
    }

    public GamepadButton ReadButtons() => GamepadManager.Shared.ReadButtons(UserIndex);
}
