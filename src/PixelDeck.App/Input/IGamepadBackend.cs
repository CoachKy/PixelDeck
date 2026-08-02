using System.Numerics;

namespace PixelDeck.App.Input;

internal interface IGamepadBackend : IDisposable
{
    string Name { get; }

    GamepadConnections ReadConnections();

    GamepadButton ReadButtons(int userIndex);

    GamepadState ReadState(int userIndex) =>
        new(ReadButtons(userIndex), 0, 0, 0, 0);

    string? GetControllerName(int userIndex);

    /// <summary>
    /// Starts or stops force feedback. Backends without haptics ignore it,
    /// which is why this defaults to doing nothing rather than throwing.
    /// </summary>
    void SetRumble(int userIndex, bool active)
    {
    }
}

internal readonly record struct GamepadState(
    GamepadButton Buttons,
    short LeftX,
    short LeftY,
    short RightX,
    short RightY);

internal readonly record struct GamepadConnections(int ConnectedMask)
{
    public int Count => BitOperations.PopCount((uint)ConnectedMask);

    public bool IsConnected(int userIndex) =>
        userIndex is >= 0 and < GamepadManager.MaximumControllers &&
        (ConnectedMask & (1 << userIndex)) != 0;
}
