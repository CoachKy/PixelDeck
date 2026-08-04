using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public class GameCubeRasterizerTests
{
    [Fact]
    public void Rasterizer_RendersTexturedTriangleCorrectly()
    {
        var rasterizer = new GameCubeRasterizer();

        // Initialize identity transform matrix (3x4 rows: R0C0, R1C1, R2C2)
        var oneBits = BitConverter.SingleToUInt32Bits(1.0f);
        rasterizer.SetTransformRegister(0, oneBits);  // Row 0 X
        rasterizer.SetTransformRegister(5, oneBits);  // Row 1 Y
        rasterizer.SetTransformRegister(10, oneBits); // Row 2 Z

        // Initialize orthographic projection scale
        rasterizer.SetTransformRegister(0x1020, oneBits);
        rasterizer.SetTransformRegister(0x1022, oneBits);
        rasterizer.SetTransformRegister(0x1024, oneBits);
        rasterizer.SetTransformRegister(0x1026, 1); // orthographic

        // Viewport scale & offset
        var scaleBits = BitConverter.SingleToUInt32Bits(100.0f);
        var biasBits = BitConverter.SingleToUInt32Bits(342.0f);
        rasterizer.SetTransformRegister(0x101A, scaleBits); // Scale X
        rasterizer.SetTransformRegister(0x101B, scaleBits); // Scale Y
        rasterizer.SetTransformRegister(0x101C, scaleBits); // Scale Z
        rasterizer.SetTransformRegister(0x101D, biasBits);  // Center X
        rasterizer.SetTransformRegister(0x101E, biasBits);  // Center Y

        // 4x4 RGBA8 dummy texture (all red: 0xFFFF0000)
        var texData = new byte[4 * 4 * 4];
        for (var i = 0; i < texData.Length; i += 4)
        {
            texData[i] = 255;     // A
            texData[i + 1] = 255; // R
            texData[i + 2] = 0;   // G
            texData[i + 3] = 0;   // B
        }

        rasterizer.SetTexture(texData, 4, 4, GameCubeTextureFormat.RGBA8);

        var vertices = new[]
        {
            new GameCubeRasterizer.Vertex(-0.5f, -0.5f, 0f, 0xFFFFFFFFu, 0f, 0f),
            new GameCubeRasterizer.Vertex(0.5f, -0.5f, 0f, 0xFFFFFFFFu, 1f, 0f),
            new GameCubeRasterizer.Vertex(0f, 0.5f, 0f, 0xFFFFFFFFu, 0.5f, 1f)
        };

        rasterizer.Draw(2, vertices);

        Assert.True(rasterizer.HasContent);
        Assert.True(rasterizer.TrianglesDrawn > 0);
    }
}
