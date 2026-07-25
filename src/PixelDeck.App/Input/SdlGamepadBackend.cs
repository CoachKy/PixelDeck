using SDL3;
using SdlAxis = SDL3.SDL.GamepadAxis;
using SdlButton = SDL3.SDL.GamepadButton;

namespace PixelDeck.App.Input;

internal sealed class SdlGamepadBackend : IGamepadBackend
{
    private const short StickThreshold = 18_000;
    private const short TriggerThreshold = 4_096;
    private const long DiscoveryIntervalMilliseconds = 500;

    private readonly object _sync = new();
    private readonly OpenGamepad?[] _slots = new OpenGamepad?[GamepadManager.MaximumControllers];
    private long _nextDiscoveryAt;
    private bool _disposed;

    private SdlGamepadBackend()
    {
        if (!SDL.InitSubSystem(SDL.InitFlags.Gamepad))
        {
            throw new InvalidOperationException($"SDL gamepad initialization failed: {SDL.GetError()}");
        }

        RefreshDevices(force: true);
    }

    public string Name => "SDL3";

    public static bool TryCreate(out SdlGamepadBackend backend)
    {
        try
        {
            backend = new SdlGamepadBackend();
            return true;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
                EntryPointNotFoundException or
                BadImageFormatException or
                TypeInitializationException or
                InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            backend = null!;
            return false;
        }
    }

    public GamepadConnections ReadConnections()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return default;
            }

            RefreshDevices(force: false);
            var connectedMask = 0;
            for (var index = 0; index < _slots.Length; index++)
            {
                if (_slots[index] is not null)
                {
                    connectedMask |= 1 << index;
                }
            }

            return new GamepadConnections(connectedMask);
        }
    }

    public GamepadButton ReadButtons(int userIndex)
    {
        lock (_sync)
        {
            if (_disposed || userIndex is < 0 or >= GamepadManager.MaximumControllers)
            {
                return GamepadButton.None;
            }

            RefreshDevices(force: false);
            SDL.UpdateGamepads();
            var gamepad = _slots[userIndex];
            if (gamepad is null || !SDL.GamepadConnected(gamepad.Handle))
            {
                RefreshDevices(force: true);
                gamepad = _slots[userIndex];
                if (gamepad is null)
                {
                    return GamepadButton.None;
                }
            }

            return Translate(gamepad.Handle);
        }
    }

    public string? GetControllerName(int userIndex)
    {
        lock (_sync)
        {
            if (_disposed || userIndex is < 0 or >= GamepadManager.MaximumControllers)
            {
                return null;
            }

            RefreshDevices(force: false);
            return _slots[userIndex]?.Name;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (var index = 0; index < _slots.Length; index++)
            {
                CloseSlot(index);
            }

            SDL.QuitSubSystem(SDL.InitFlags.Gamepad);
        }
    }

    private void RefreshDevices(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now < _nextDiscoveryAt)
        {
            return;
        }

        _nextDiscoveryAt = now + DiscoveryIntervalMilliseconds;
        SDL.UpdateGamepads();
        var connectedIds = SDL.GetGamepads(out var count) ?? [];
        var currentIds = connectedIds.Take(count).ToHashSet();

        for (var index = 0; index < _slots.Length; index++)
        {
            var slot = _slots[index];
            if (slot is not null &&
                (!currentIds.Contains(slot.InstanceId) || !SDL.GamepadConnected(slot.Handle)))
            {
                CloseSlot(index);
            }
        }

        foreach (var instanceId in connectedIds.Take(count))
        {
            if (_slots.Any(slot => slot?.InstanceId == instanceId))
            {
                continue;
            }

            var emptyIndex = Array.FindIndex(_slots, static slot => slot is null);
            if (emptyIndex < 0)
            {
                break;
            }

            var handle = SDL.OpenGamepad(instanceId);
            if (handle == IntPtr.Zero)
            {
                continue;
            }

            _slots[emptyIndex] = new OpenGamepad(
                instanceId,
                handle,
                SDL.GetGamepadName(handle) ?? $"Controller {emptyIndex + 1}");
        }
    }

    private void CloseSlot(int index)
    {
        var slot = _slots[index];
        if (slot is null)
        {
            return;
        }

        SDL.CloseGamepad(slot.Handle);
        _slots[index] = null;
    }

    private static GamepadButton Translate(IntPtr gamepad)
    {
        var digitalButtons = GamepadButton.None;
        if (Pressed(gamepad, SdlButton.DPadLeft)) digitalButtons |= GamepadButton.DPadLeft;
        if (Pressed(gamepad, SdlButton.DPadRight)) digitalButtons |= GamepadButton.DPadRight;
        if (Pressed(gamepad, SdlButton.DPadUp)) digitalButtons |= GamepadButton.DPadUp;
        if (Pressed(gamepad, SdlButton.DPadDown)) digitalButtons |= GamepadButton.DPadDown;
        if (Pressed(gamepad, SdlButton.South)) digitalButtons |= GamepadButton.A;
        if (Pressed(gamepad, SdlButton.East)) digitalButtons |= GamepadButton.B;
        if (Pressed(gamepad, SdlButton.West)) digitalButtons |= GamepadButton.X;
        if (Pressed(gamepad, SdlButton.North)) digitalButtons |= GamepadButton.Y;
        if (Pressed(gamepad, SdlButton.Back)) digitalButtons |= GamepadButton.Back;
        if (Pressed(gamepad, SdlButton.Start)) digitalButtons |= GamepadButton.Start;
        if (Pressed(gamepad, SdlButton.Guide)) digitalButtons |= GamepadButton.Guide;
        if (Pressed(gamepad, SdlButton.LeftShoulder)) digitalButtons |= GamepadButton.LeftShoulder;
        if (Pressed(gamepad, SdlButton.RightShoulder)) digitalButtons |= GamepadButton.RightShoulder;
        if (Pressed(gamepad, SdlButton.LeftStick)) digitalButtons |= GamepadButton.LeftThumb;
        if (Pressed(gamepad, SdlButton.RightStick)) digitalButtons |= GamepadButton.RightThumb;

        return Translate(new SdlGamepadState(
            digitalButtons,
            SDL.GetGamepadAxis(gamepad, SdlAxis.LeftX),
            SDL.GetGamepadAxis(gamepad, SdlAxis.LeftY),
            SDL.GetGamepadAxis(gamepad, SdlAxis.LeftTrigger),
            SDL.GetGamepadAxis(gamepad, SdlAxis.RightTrigger)));
    }

    internal static GamepadButton Translate(SdlGamepadState state)
    {
        var result = state.DigitalButtons;

        if (state.LeftX < -StickThreshold) result |= GamepadButton.DPadLeft;
        if (state.LeftX > StickThreshold) result |= GamepadButton.DPadRight;
        if (state.LeftY < -StickThreshold) result |= GamepadButton.DPadUp;
        if (state.LeftY > StickThreshold) result |= GamepadButton.DPadDown;
        if (state.LeftTrigger > TriggerThreshold) result |= GamepadButton.LeftTrigger;
        if (state.RightTrigger > TriggerThreshold) result |= GamepadButton.RightTrigger;

        return result;
    }

    private static bool Pressed(IntPtr gamepad, SdlButton button) =>
        SDL.GetGamepadButton(gamepad, button);

    private sealed record OpenGamepad(uint InstanceId, IntPtr Handle, string Name);
}

internal readonly record struct SdlGamepadState(
    GamepadButton DigitalButtons,
    short LeftX,
    short LeftY,
    short LeftTrigger,
    short RightTrigger);
