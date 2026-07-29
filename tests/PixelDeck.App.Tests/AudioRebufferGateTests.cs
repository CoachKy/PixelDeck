using PixelDeck.App.Audio;

namespace PixelDeck.App.Tests;

public sealed class AudioRebufferGateTests
{
    [Fact]
    public void InitialPlaybackWaitsForTheRequestedPrebuffer()
    {
        var gate = new AudioRebufferGate();

        Assert.True(gate.ShouldWait(bufferedValues: 3_199, requiredValues: 3_200));
        Assert.False(gate.ShouldWait(bufferedValues: 3_200, requiredValues: 3_200));
        Assert.False(gate.ShouldWait(bufferedValues: 0, requiredValues: 3_200));
    }

    [Fact]
    public void UnderrunRequiresAFullRebufferBeforePlaybackResumes()
    {
        var gate = new AudioRebufferGate();
        Assert.False(gate.ShouldWait(bufferedValues: 3_200, requiredValues: 3_200));

        gate.OnUnderrun();

        Assert.True(gate.ShouldWait(bufferedValues: 512, requiredValues: 3_200));
        Assert.True(gate.ShouldWait(bufferedValues: 3_199, requiredValues: 3_200));
        Assert.False(gate.ShouldWait(bufferedValues: 3_200, requiredValues: 3_200));
    }
}
