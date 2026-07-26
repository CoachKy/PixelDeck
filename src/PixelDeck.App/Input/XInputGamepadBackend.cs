namespace PixelDeck.App.Input;

internal sealed class XInputGamepadBackend : IGamepadBackend
{
    private readonly WindowsGamepad[] _gamepads = Enumerable
        .Range(0, GamepadManager.MaximumControllers)
        .Select(static userIndex => new WindowsGamepad { UserIndex = userIndex })
        .ToArray();

    public string Name => "XInput fallback";

    public GamepadConnections ReadConnections() => WindowsGamepad.ReadConnections();

    public GamepadButton ReadButtons(int userIndex) =>
        userIndex is >= 0 and < GamepadManager.MaximumControllers
            ? _gamepads[userIndex].ReadButtons()
            : GamepadButton.None;

    public GamepadState ReadState(int userIndex) =>
        userIndex is >= 0 and < GamepadManager.MaximumControllers
            ? _gamepads[userIndex].ReadState()
            : default;

    public string? GetControllerName(int userIndex) =>
        ReadConnections().IsConnected(userIndex)
            ? $"XInput Controller {userIndex + 1}"
            : null;

    public void Dispose()
    {
    }
}
