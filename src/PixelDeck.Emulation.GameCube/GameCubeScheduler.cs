namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Cycle-accurate event timing scheduler for PixelCube components.
/// </summary>
public sealed class GameCubeScheduler
{
    public const long CoreClockFrequency = 486_000_000; // 486 MHz IBM Gekko CPU
    public const long CyclesPerFrame = CoreClockFrequency / 60; // 8,100,000 cycles at 60Hz

    private readonly PriorityQueue<Action, long> _events = new();

    public long CurrentCycle { get; private set; }

    /// <summary>
    /// Schedules an event callback to fire after <paramref name="delayCycles"/>.
    /// </summary>
    public void ScheduleEvent(long delayCycles, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _events.Enqueue(callback, CurrentCycle + Math.Max(1, delayCycles));
    }

    /// <summary>
    /// Advances system time by <paramref name="cycles"/> and executes pending events.
    /// </summary>
    public void Step(long cycles)
    {
        var targetCycle = CurrentCycle + Math.Max(1, cycles);

        while (_events.Count > 0 && _events.TryPeek(out _, out var priority) && priority <= targetCycle)
        {
            if (_events.TryDequeue(out var callback, out var eventCycle))
            {
                CurrentCycle = eventCycle;
                callback();
            }
        }

        CurrentCycle = targetCycle;
    }

    public void Reset()
    {
        _events.Clear();
        CurrentCycle = 0;
    }
}
