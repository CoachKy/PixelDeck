namespace PixelDeck.App.Audio;

/// <summary>
/// Turns an unavoidable audio underrun into a short fade instead of an
/// instantaneous waveform-to-zero edge, then fades the recovered stream back
/// in. The latter edge is perceived as a click or burst of static.
/// </summary>
internal sealed class AudioUnderrunSmoother
{
    private const int FadeFrames = 64;

    private readonly object _sync = new();
    private readonly float[] _lastOutput = new float[2];
    private bool _recovering;

    internal void Process(Span<float> destination, int sourceValues, int channels)
    {
        lock (_sync)
        {
            ProcessCore(destination, sourceValues, channels);
        }
    }

    private void ProcessCore(Span<float> destination, int sourceValues, int channels)
    {
        if (channels is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        sourceValues = Math.Clamp(sourceValues, 0, destination.Length);
        sourceValues -= sourceValues % channels;
        var sourceFrames = sourceValues / channels;

        if (_recovering && sourceFrames > 0)
        {
            var fadeFrames = Math.Min(FadeFrames, sourceFrames);
            for (var frame = 0; frame < fadeFrames; frame++)
            {
                var amount = (frame + 1f) / fadeFrames;
                for (var channel = 0; channel < channels; channel++)
                {
                    var index = (frame * channels) + channel;
                    destination[index] =
                        _lastOutput[channel] +
                        ((destination[index] - _lastOutput[channel]) * amount);
                }
            }

            _recovering = false;
        }

        if (sourceValues == destination.Length)
        {
            RememberLastFrame(destination, channels);
            return;
        }

        Span<float> fadeFrom = stackalloc float[2];
        for (var channel = 0; channel < channels; channel++)
        {
            fadeFrom[channel] = sourceFrames > 0
                ? destination[sourceValues - channels + channel]
                : _lastOutput[channel];
        }

        var missingFrames = (destination.Length - sourceValues) / channels;
        var fadeOutFrames = Math.Min(FadeFrames, missingFrames);
        for (var frame = 0; frame < missingFrames; frame++)
        {
            var amount = frame < fadeOutFrames
                ? 1f - ((frame + 1f) / fadeOutFrames)
                : 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                destination[sourceValues + (frame * channels) + channel] =
                    fadeFrom[channel] * amount;
            }
        }

        var alignedLength = sourceValues + (missingFrames * channels);
        destination[alignedLength..].Clear();
        RememberLastFrame(destination, channels);
        _recovering = true;
    }

    internal void Reset()
    {
        lock (_sync)
        {
            Array.Clear(_lastOutput);
            _recovering = false;
        }
    }

    private void RememberLastFrame(ReadOnlySpan<float> samples, int channels)
    {
        if (samples.Length < channels)
        {
            Array.Clear(_lastOutput);
            return;
        }

        var frameStart = samples.Length - channels;
        for (var channel = 0; channel < channels; channel++)
        {
            _lastOutput[channel] = samples[frameStart + channel];
        }
    }
}
