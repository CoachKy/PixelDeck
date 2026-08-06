using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public class GameCubeNativeVulkanTests
{
    [Fact]
    public void PdGxNative_ProbesNativeRendererStatus()
    {
        var deviceName = PdGxNative.GetDeviceName();
        Assert.NotNull(deviceName);
        Assert.NotEmpty(deviceName);
    }

    [Fact]
    public void VideoOutput_GeneratesBootPatternWhenFramebufferZero()
    {
        var destination = new uint[640 * 480];
        GameCubeVideoOutput.GenerateBootPattern(destination, 640, 480);

        Assert.Equal(0xFF6D72E8u, destination[0]); // Header color
        Assert.Equal(0xFF101018u, destination[640 * 50 + 10]); // Background
    }
}
