using PixelDeck.App.Audio;

namespace PixelDeck.App.Tests;

public sealed class AudioBufferSynchronizerTests
{
    private const double FramesPerSecond = 60.0988;
    private const int SampleRate = 48_000;

    [Fact]
    public void UnavailableAudioUsesTheExactNominalFrameInterval()
    {
        var synchronizer = new AudioBufferSynchronizer();

        var interval = synchronizer.GetFrameInterval(
            FramesPerSecond,
            playbackRate: 1,
            audioAvailable: false,
            bufferedSampleValues: 0,
            SampleRate,
            channels: 1);

        Assert.Equal(TimeSpan.FromSeconds(1 / FramesPerSecond), interval);
    }

    [Fact]
    public void FastForwardUsesTheExactRequestedRateAndResetsFeedback()
    {
        var synchronizer = new AudioBufferSynchronizer();
        _ = synchronizer.GetFrameInterval(
            FramesPerSecond,
            playbackRate: 1,
            audioAvailable: true,
            bufferedSampleValues: 0,
            SampleRate,
            channels: 1);

        var fastInterval = synchronizer.GetFrameInterval(
            FramesPerSecond,
            playbackRate: 2,
            audioAvailable: true,
            bufferedSampleValues: 0,
            SampleRate,
            channels: 1);
        var recoveredInterval = synchronizer.GetFrameInterval(
            FramesPerSecond,
            playbackRate: 1,
            audioAvailable: true,
            bufferedSampleValues: (int)(SampleRate * AudioBufferSynchronizer.TargetBufferSeconds),
            SampleRate,
            channels: 1);

        Assert.Equal(TimeSpan.FromSeconds(1 / (FramesPerSecond * 2)), fastInterval);
        Assert.Equal(TimeSpan.FromSeconds(1 / FramesPerSecond), recoveredInterval);
    }

    [Fact]
    public void LowAndHighQueuesApplyOnlyBoundedHostWaitCorrections()
    {
        var lowQueue = new AudioBufferSynchronizer();
        var highQueue = new AudioBufferSynchronizer();
        var nominal = TimeSpan.FromSeconds(1 / FramesPerSecond);

        var shortened = lowQueue.GetFrameInterval(
            FramesPerSecond,
            playbackRate: 1,
            audioAvailable: true,
            bufferedSampleValues: 0,
            SampleRate,
            channels: 1);
        var lengthened = highQueue.GetFrameInterval(
            FramesPerSecond,
            playbackRate: 1,
            audioAvailable: true,
            bufferedSampleValues: SampleRate / 5,
            SampleRate,
            channels: 1);

        Assert.True(shortened < nominal);
        Assert.True(lengthened > nominal);
        Assert.True(
            shortened.Ticks >=
            Math.Floor(nominal.Ticks * (1 - AudioBufferSynchronizer.MaximumRateCorrection)));
        Assert.True(
            lengthened.Ticks <=
            Math.Ceiling(nominal.Ticks * (1 + AudioBufferSynchronizer.MaximumRateCorrection)));
    }

    [Fact]
    public void QueueJitterInsideTheDeadbandDoesNotChangeTheFrameClock()
    {
        var synchronizer = new AudioBufferSynchronizer();
        var targetSamples = (int)(SampleRate * AudioBufferSynchronizer.TargetBufferSeconds);
        var jitterSamples = (int)(SampleRate * (AudioBufferSynchronizer.DeadbandSeconds / 2));

        var first = synchronizer.GetFrameInterval(
            FramesPerSecond,
            playbackRate: 1,
            audioAvailable: true,
            bufferedSampleValues: targetSamples - jitterSamples,
            SampleRate,
            channels: 1);
        var second = synchronizer.GetFrameInterval(
            FramesPerSecond,
            playbackRate: 1,
            audioAvailable: true,
            bufferedSampleValues: targetSamples + jitterSamples,
            SampleRate,
            channels: 1);

        var nominal = TimeSpan.FromSeconds(1 / FramesPerSecond);
        Assert.Equal(nominal, first);
        Assert.Equal(nominal, second);
    }

    [Fact]
    public void StereoQueueMeasuresBufferedFramesInsteadOfRawSampleValues()
    {
        const int snesSampleRate = 32_000;
        const int channels = 2;
        var synchronizer = new AudioBufferSynchronizer();
        var targetSampleValues = (int)(
            snesSampleRate *
            channels *
            AudioBufferSynchronizer.TargetBufferSeconds);

        var interval = synchronizer.GetFrameInterval(
            FramesPerSecond,
            playbackRate: 1,
            audioAvailable: true,
            bufferedSampleValues: targetSampleValues,
            snesSampleRate,
            channels);

        Assert.Equal(TimeSpan.FromSeconds(1 / FramesPerSecond), interval);
    }
}
