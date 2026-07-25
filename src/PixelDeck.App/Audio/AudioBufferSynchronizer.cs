namespace PixelDeck.App.Audio;

internal sealed class AudioBufferSynchronizer
{
    internal const double TargetBufferSeconds = 0.040;
    internal const double DeadbandSeconds = 0.008;
    internal const double MaximumRateCorrection = 0.005;
    private const double SmoothingFactor = 0.125;

    private bool _hasBufferMeasurement;
    private double _smoothedBufferSeconds;

    public TimeSpan GetFrameInterval(
        double framesPerSecond,
        int playbackRate,
        bool audioAvailable,
        int bufferedSampleValues,
        int sampleRate,
        int channels)
    {
        if (framesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(framesPerSecond),
                framesPerSecond,
                "The emulated frame rate must be positive.");
        }

        var nominalSeconds = 1.0 / (framesPerSecond * Math.Max(1, playbackRate));
        if (!audioAvailable ||
            playbackRate != 1 ||
            bufferedSampleValues < 0 ||
            sampleRate <= 0 ||
            channels <= 0)
        {
            Reset();
            return TimeSpan.FromSeconds(nominalSeconds);
        }

        var bufferSeconds = bufferedSampleValues / (double)(sampleRate * channels);
        if (!_hasBufferMeasurement)
        {
            _smoothedBufferSeconds = bufferSeconds;
            _hasBufferMeasurement = true;
        }
        else
        {
            _smoothedBufferSeconds +=
                (bufferSeconds - _smoothedBufferSeconds) * SmoothingFactor;
        }

        var errorSeconds = _smoothedBufferSeconds - TargetBufferSeconds;
        var errorOutsideDeadband = Math.Max(0, Math.Abs(errorSeconds) - DeadbandSeconds);
        var correction = Math.CopySign(
            Math.Min(
                MaximumRateCorrection,
                (errorOutsideDeadband / TargetBufferSeconds) * MaximumRateCorrection),
            errorSeconds);

        // A full queue means the producer is gaining on the audio device, so
        // lengthen the host wait. A low queue shortens it. Emulated work per
        // frame and the 48 kHz sample clock remain unchanged.
        return TimeSpan.FromSeconds(nominalSeconds * (1.0 + correction));
    }

    public void Reset()
    {
        _hasBufferMeasurement = false;
        _smoothedBufferSeconds = 0;
    }
}
