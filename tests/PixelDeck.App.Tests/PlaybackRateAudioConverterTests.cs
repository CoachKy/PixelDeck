using PixelDeck.App.Audio;

namespace PixelDeck.App.Tests;

public sealed class PlaybackRateAudioConverterTests
{
    [Fact]
    public void TwoTimesMonoConsumesEverySecondSourceFrame()
    {
        float[] source = [0, 1, 2, 3, 4, 5, 6, 7];
        var destination = new float[4];
        var phase = 0;

        var converted = PlaybackRateAudioConverter.Convert(
            source,
            destination,
            channels: 1,
            playbackRate: 2,
            ref phase);

        Assert.Equal(4, converted);
        Assert.Equal([0, 2, 4, 6], destination);
        Assert.Equal(0, phase);
    }

    [Fact]
    public void TwoTimesStereoKeepsLeftAndRightFramesTogether()
    {
        float[] source =
        [
            0, 10,
            1, 11,
            2, 12,
            3, 13
        ];
        var destination = new float[4];
        var phase = 0;

        var converted = PlaybackRateAudioConverter.Convert(
            source,
            destination,
            channels: 2,
            playbackRate: 2,
            ref phase);

        Assert.Equal(4, converted);
        Assert.Equal([0, 10, 2, 12], destination);
        Assert.Equal(0, phase);
    }

    [Fact]
    public void PartialReadsPreserveTheSourceFramePhase()
    {
        var firstDestination = new float[2];
        var secondDestination = new float[2];
        var phase = 0;

        var firstConverted = PlaybackRateAudioConverter.Convert(
            new float[] { 0, 1, 2 },
            firstDestination,
            channels: 1,
            playbackRate: 2,
            ref phase);
        var secondConverted = PlaybackRateAudioConverter.Convert(
            new float[] { 3, 4, 5, 6 },
            secondDestination,
            channels: 1,
            playbackRate: 2,
            ref phase);

        Assert.Equal(2, firstConverted);
        Assert.Equal([0, 2], firstDestination);
        Assert.Equal(2, secondConverted);
        Assert.Equal([4, 6], secondDestination);
        Assert.Equal(1, phase);
    }

    [Fact]
    public void SourceRequirementAccountsForChannelsAndRate()
    {
        Assert.Equal(
            4_096,
            PlaybackRateAudioConverter.GetRequiredSourceValueCount(
                destinationValueCount: 2_048,
                channels: 2,
                playbackRate: 2));
    }
}
