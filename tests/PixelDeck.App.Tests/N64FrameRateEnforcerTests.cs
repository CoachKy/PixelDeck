using PixelDeck.App.Services;

namespace PixelDeck.App.Tests;

public sealed class N64FrameRateEnforcerTests
{
    [Fact]
    public void ExcessiveBacklogIsDroppedInsteadOfCausingRunawayCatchUp()
    {
        var enforcer = new N64FrameRateEnforcer();
        var elapsed = TimeSpan.FromSeconds(2);
        var frameInterval = TimeSpan.FromSeconds(1.0 / 60.0);

        Assert.Equal(
            elapsed,
            enforcer.BoundCatchUp(
                elapsed,
                elapsed - TimeSpan.FromSeconds(1),
                frameInterval));
        Assert.Equal(
            elapsed - frameInterval,
            enforcer.BoundCatchUp(
                elapsed,
                elapsed - frameInterval,
                frameInterval));
    }
}
