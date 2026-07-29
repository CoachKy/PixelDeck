namespace PixelDeck.App.Audio;

/// <summary>
/// Holds an audio stream at silence until enough source data is available for
/// a stable start. An underrun closes the gate again so the next callback does
/// not repeatedly start and stop on tiny fragments of recovered audio.
/// </summary>
internal sealed class AudioRebufferGate
{
    private int _needsPrebuffer = 1;

    internal bool ShouldWait(int bufferedValues, int requiredValues)
    {
        if (Volatile.Read(ref _needsPrebuffer) == 0)
        {
            return false;
        }

        if (bufferedValues < requiredValues)
        {
            return true;
        }

        Volatile.Write(ref _needsPrebuffer, 0);
        return false;
    }

    internal void OnUnderrun() => Volatile.Write(ref _needsPrebuffer, 1);

    internal void Reset() => Volatile.Write(ref _needsPrebuffer, 1);
}
