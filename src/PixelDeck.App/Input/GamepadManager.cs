namespace PixelDeck.App.Input;

internal sealed class GamepadManager : IDisposable
{
    public const int MaximumControllers = 4;

    private static readonly Lazy<GamepadManager> LazyShared = new(
        static () => new GamepadManager(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly IGamepadBackend _backend;
    private bool _disposed;

    private GamepadManager()
    {
        _backend = SdlGamepadBackend.TryCreate(out var sdlBackend)
            ? sdlBackend
            : new XInputGamepadBackend();
    }

    public static GamepadManager Shared => LazyShared.Value;

    public string BackendName => _backend.Name;

    public GamepadConnections ReadConnections() =>
        _disposed ? default : _backend.ReadConnections();

    public GamepadButton ReadButtons(int userIndex) =>
        _disposed ? GamepadButton.None : _backend.ReadButtons(userIndex);

    public string? GetControllerName(int userIndex) =>
        _disposed ? null : _backend.GetControllerName(userIndex);

    public static void Shutdown()
    {
        if (LazyShared.IsValueCreated)
        {
            LazyShared.Value.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _backend.Dispose();
    }
}
