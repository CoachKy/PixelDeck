using System.Numerics;
using PixelDeck.Emulation.N64;

namespace PixelDeck.App.Tests;

public sealed class N64RdpTriangleEncoderTests
{
    [Fact]
    public void EncodesNativeShadeTextureDepthPacket()
    {
        var color = new Vector4(0.25f, 0.5f, 0.75f, 1);
        var first = new N64RdpHleVertex(10, 10, 0.5f, 0, 0, 0.49f, color);
        var second = new N64RdpHleVertex(100, 10, 0.5f, 1, 0, 0.49f, color);
        var third = new N64RdpHleVertex(10, 100, 0.5f, 0, 1, 0.49f, color);

        var encoded = N64RdpTriangleEncoder.TryEncode(
            first,
            second,
            third,
            tile: 3,
            maximumMipLevel: 2,
            out var command);

        Assert.True(encoded);
        Assert.NotNull(command);
        Assert.Equal(0x0F, command.Opcode);
        Assert.Equal(44, command.Words.Length);

        var words = command.Words.Span;
        Assert.Equal(3u, (words[0] >> 16) & 7);
        Assert.Equal(2u, (words[0] >> 19) & 7);
        Assert.Equal(400u, words[0] & 0x3FFF);
        Assert.Equal(40u, (words[1] >> 16) & 0x3FFF);
        Assert.Equal(40u, words[1] & 0x3FFF);

        Assert.Equal(QuantizeColor(0.25), DecodeR(words));
        Assert.Equal(QuantizeColor(0.5), DecodeG(words));
        Assert.Equal(QuantizeColor(0.75), DecodeB(words));
        Assert.Equal(QuantizeColor(1), DecodeA(words));
        Assert.Equal(0u, words[10]);
        Assert.Equal(0u, words[11]);
        Assert.Equal(0u, words[14]);
        Assert.Equal(0u, words[15]);
        Assert.Equal(0u, words[41]);
        Assert.Equal(0u, words[42]);
        Assert.Equal(0u, words[43]);
    }

    [Fact]
    public void RejectsDegenerateTriangleWithoutInventingPacket()
    {
        var vertex = new N64RdpHleVertex(
            20,
            20,
            0.5f,
            0,
            0,
            0.49f,
            Vector4.One);

        Assert.False(N64RdpTriangleEncoder.TryEncode(
            vertex,
            vertex,
            vertex,
            tile: 0,
            maximumMipLevel: 0,
            out var command));
        Assert.Null(command);
    }

    private static uint QuantizeColor(double value) =>
        unchecked((uint)Math.Round(
            value * 255.0 * 65536.0,
            MidpointRounding.AwayFromZero));

    private static uint DecodeR(ReadOnlySpan<uint> words) =>
        (words[8] & 0xFFFF0000u) | ((words[12] >> 16) & 0xFFFFu);

    private static uint DecodeG(ReadOnlySpan<uint> words) =>
        (words[8] << 16) | (words[12] & 0xFFFFu);

    private static uint DecodeB(ReadOnlySpan<uint> words) =>
        (words[9] & 0xFFFF0000u) | ((words[13] >> 16) & 0xFFFFu);

    private static uint DecodeA(ReadOnlySpan<uint> words) =>
        (words[9] << 16) | (words[13] & 0xFFFFu);
}
