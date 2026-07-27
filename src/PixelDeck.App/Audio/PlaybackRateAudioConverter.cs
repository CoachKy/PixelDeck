namespace PixelDeck.App.Audio;

internal static class PlaybackRateAudioConverter
{
    public static int GetRequiredSourceValueCount(
        int destinationValueCount,
        int channels,
        int playbackRate)
    {
        Validate(channels, playbackRate);
        ArgumentOutOfRangeException.ThrowIfNegative(destinationValueCount);

        var destinationFrames = destinationValueCount / channels;
        return checked(destinationFrames * channels * playbackRate);
    }

    public static int Convert(
        ReadOnlySpan<float> source,
        Span<float> destination,
        int channels,
        int playbackRate,
        ref int sourceFramePhase)
    {
        Validate(channels, playbackRate);
        if ((uint)sourceFramePhase >= (uint)playbackRate)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceFramePhase));
        }

        var sourceFrames = source.Length / channels;
        var destinationFrames = destination.Length / channels;
        var outputFrame = 0;
        for (var sourceFrame = 0; sourceFrame < sourceFrames; sourceFrame++)
        {
            if (sourceFramePhase == 0 && outputFrame < destinationFrames)
            {
                source.Slice(sourceFrame * channels, channels)
                    .CopyTo(destination.Slice(outputFrame * channels, channels));
                outputFrame++;
            }

            sourceFramePhase++;
            if (sourceFramePhase == playbackRate)
            {
                sourceFramePhase = 0;
            }
        }

        return outputFrame * channels;
    }

    private static void Validate(int channels, int playbackRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(playbackRate);
    }
}
