using PixelDeck.App.Audio;

namespace PixelDeck.App.Tests;

public sealed class AudioUnderrunSmootherTests
{
    [Fact]
    public void UnderrunFadesToSilenceInsteadOfCreatingASharpEdge()
    {
        var smoother = new AudioUnderrunSmoother();
        var samples = Enumerable.Repeat(0.75f, 160).ToArray();

        smoother.Process(samples, sourceValues: 32, channels: 2);

        Assert.InRange(samples[32], 0f, 0.75f);
        Assert.True(samples[32] < samples[30]);
        Assert.Equal(0f, samples[^1]);
        Assert.Equal(0f, samples[^2]);
        Assert.True(MaximumAdjacentDelta(samples, 2) < 0.1f);
    }

    [Fact]
    public void RecoveredAudioFadesBackIn()
    {
        var smoother = new AudioUnderrunSmoother();
        var underrun = Enumerable.Repeat(0.5f, 160).ToArray();
        smoother.Process(underrun, sourceValues: 16, channels: 2);
        var recovered = Enumerable.Repeat(0.8f, 160).ToArray();

        smoother.Process(recovered, recovered.Length, channels: 2);

        Assert.InRange(recovered[0], 0f, 0.8f);
        Assert.True(recovered[0] < recovered[126]);
        Assert.Equal(0.8f, recovered[^1]);
    }

    private static float MaximumAdjacentDelta(float[] samples, int channels)
    {
        var maximum = 0f;
        for (var index = channels; index < samples.Length; index++)
        {
            maximum = Math.Max(maximum, Math.Abs(samples[index] - samples[index - channels]));
        }

        return maximum;
    }
}
