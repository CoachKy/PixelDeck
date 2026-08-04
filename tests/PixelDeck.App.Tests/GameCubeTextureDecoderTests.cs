using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public class GameCubeTextureDecoderTests
{
    [Fact]
    public void DecodeI4_ProducesExpectedPixels()
    {
        // 8x8 I4 texture requires 32 bytes (64 pixels, 4 bits per pixel).
        var source = new byte[32];
        Array.Fill(source, (byte)0xFF); // All intensity 15 -> 255

        var destination = new uint[8 * 8];
        GameCubeTextureDecoder.Decode(source, 8, 8, GameCubeTextureFormat.I4, destination);

        foreach (var px in destination)
        {
            Assert.Equal(0xFFFFFFFFu, px);
        }
    }

    [Fact]
    public void DecodeRgb565_ProducesExpectedColors()
    {
        // 4x4 RGB565 tile = 16 pixels * 2 bytes = 32 bytes.
        // Pure red in RGB565 (big endian): 0xF800 -> bytes 0xF8, 0x00.
        var source = new byte[32];
        for (var i = 0; i < 32; i += 2)
        {
            source[i] = 0xF8;
            source[i + 1] = 0x00;
        }

        var destination = new uint[4 * 4];
        GameCubeTextureDecoder.Decode(source, 4, 4, GameCubeTextureFormat.RGB565, destination);

        foreach (var px in destination)
        {
            var r = (px >> 16) & 0xFF;
            var g = (px >> 8) & 0xFF;
            var b = px & 0xFF;
            var a = (px >> 24) & 0xFF;

            Assert.Equal(255u, a);
            Assert.Equal(255u, r);
            Assert.Equal(0u, g);
            Assert.Equal(0u, b);
        }
    }

    [Fact]
    public void DecodeCmpr_DecodesBlockWithoutCrashing()
    {
        // CMPR uses 8x8 tiles (32 bytes per 8x8 tile = 4 sub-blocks of 8 bytes).
        var source = new byte[32];
        var destination = new uint[8 * 8];

        GameCubeTextureDecoder.Decode(source, 8, 8, GameCubeTextureFormat.CMPR, destination);
        Assert.Equal(64, destination.Length);
    }
}
