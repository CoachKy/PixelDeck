using System.Numerics;

namespace PixelDeck.App.Input;

internal interface IGamepadBackend : IDisposable
{
    string Name { get; }

    GamepadConnections ReadConnections();

    GamepadButton ReadButtons(int userIndex);

    string? GetControllerName(int userIndex);
}

internal readonly record struct GamepadConnections(int ConnectedMask)
{
    public int Count => BitOperations.PopCount((uint)ConnectedMask);

    public bool IsConnected(int userIndex) =>
        userIndex is >= 0 and < GamepadManager.MaximumControllers &&
        (ConnectedMask & (1 << userIndex)) != 0;
}
