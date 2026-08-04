using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public class GameCubeAudioDecoderTests
{
    [Fact]
    public void AudioDecoder_DecodesBlockWithoutCrashing()
    {
        var adpcmBlock = new byte[8];
        adpcmBlock[0] = 0x10; // Predictor 1, Scale 0

        var pcmOutput = new short[14];
        short hist1 = 0, hist2 = 0;

        GameCubeAudioDecoder.DecodeBlock(adpcmBlock, pcmOutput, ref hist1, ref hist2);
        Assert.Equal(14, pcmOutput.Length);
    }
}
