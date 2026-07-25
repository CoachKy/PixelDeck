namespace PixelDeck.App.Input;

internal sealed class HeldButtonRepeater
{
    private readonly GamepadButton _repeatableButtons;
    private readonly long _initialDelayMilliseconds;
    private readonly long _repeatIntervalMilliseconds;
    private GamepadButton _heldButton;
    private long _nextRepeatAtMilliseconds;

    public HeldButtonRepeater(
        GamepadButton repeatableButtons,
        long initialDelayMilliseconds = 350,
        long repeatIntervalMilliseconds = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialDelayMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(repeatIntervalMilliseconds);

        _repeatableButtons = repeatableButtons;
        _initialDelayMilliseconds = initialDelayMilliseconds;
        _repeatIntervalMilliseconds = repeatIntervalMilliseconds;
    }

    public GamepadButton ReadRepeat(GamepadButton buttons, long nowMilliseconds)
    {
        var heldButton = buttons & _repeatableButtons;
        if (!IsSingleButton(heldButton))
        {
            Reset();
            return GamepadButton.None;
        }

        if (heldButton != _heldButton)
        {
            _heldButton = heldButton;
            _nextRepeatAtMilliseconds = nowMilliseconds + _initialDelayMilliseconds;
            return GamepadButton.None;
        }

        if (nowMilliseconds < _nextRepeatAtMilliseconds)
        {
            return GamepadButton.None;
        }

        var elapsedIntervals =
            ((nowMilliseconds - _nextRepeatAtMilliseconds) / _repeatIntervalMilliseconds) + 1;
        _nextRepeatAtMilliseconds += elapsedIntervals * _repeatIntervalMilliseconds;
        return heldButton;
    }

    public void Reset()
    {
        _heldButton = GamepadButton.None;
        _nextRepeatAtMilliseconds = 0;
    }

    private static bool IsSingleButton(GamepadButton buttons)
    {
        var value = (uint)buttons;
        return value != 0 && (value & (value - 1)) == 0;
    }
}
