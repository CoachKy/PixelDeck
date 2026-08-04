using System.Buffers.Binary;
using PixelDeck.Emulation.N64;
using Xunit;

namespace PixelDeck.App.Tests;

/// <summary>
/// Guards the raw RDP command decoder against the two table errors found in the
/// 2026-08 accuracy review: triangle command lengths inferred from a pattern
/// rather than the hardware sizes, and a hardware opcode block shifted one slot
/// high. Both classes of bug desynchronize or silently drop commands, so the
/// expectations here are stated as literal hardware values rather than being
/// derived from the implementation.
/// </summary>
public class N64RdpDecodeTests
{
    /// <summary>
    /// Hardware triangle command sizes in bytes, cross-checked against the
    /// angrylion-rdp-plus <c>rdp_commands</c> table. Previously 0x09, 0x0B,
    /// 0x0C and 0x0E were wrong; 0x0F was correct only because the depth and
    /// shade errors happened to cancel, which is why it was the one variant
    /// with coverage.
    /// </summary>
    [Theory]
    [InlineData(0x08, 32)]
    [InlineData(0x09, 48)]
    [InlineData(0x0A, 96)]
    [InlineData(0x0B, 112)]
    [InlineData(0x0C, 96)]
    [InlineData(0x0D, 112)]
    [InlineData(0x0E, 160)]
    [InlineData(0x0F, 176)]
    public void TriangleCommandLengthMatchesHardware(int opcode, int expectedBytes)
    {
        Assert.Equal(
            expectedBytes,
            Fast3dRenderer.RdpTriangleCommandWords((byte)opcode) * sizeof(uint));
    }

    /// <summary>
    /// A buffer holding a mis-sized triangle followed by a SET_FILL_COLOR would
    /// previously swallow the following command. Walking a stream of every
    /// triangle variant back to back proves the decoder stays in phase.
    /// </summary>
    [Fact]
    public void TriangleStreamStaysInPhaseAcrossEveryVariant()
    {
        var cartridge = N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage());
        var memory = new N64Memory(cartridge);
        var renderer = new Fast3dRenderer(memory) { RasterizationEnabled = false };

        const uint baseAddress = 0x2000;
        var address = baseAddress;
        for (var opcode = 0x08u; opcode <= 0x0Fu; opcode++)
        {
            var words = Fast3dRenderer.RdpTriangleCommandWords((byte)opcode);
            BinaryPrimitives.WriteUInt32BigEndian(
                memory.Rdram.AsSpan((int)address, 4), opcode << 24);
            for (var index = 1; index < words; index++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(
                    memory.Rdram.AsSpan((int)(address + (index * 4)), 4), 0);
            }

            address += (uint)words * 4;
        }

        // A SET_FILL_COLOR terminator. If any triangle length is wrong the
        // decoder lands mid-command and never observes this word.
        BinaryPrimitives.WriteUInt32BigEndian(
            memory.Rdram.AsSpan((int)address, 4), 0x37000000u);
        BinaryPrimitives.WriteUInt32BigEndian(
            memory.Rdram.AsSpan((int)(address + 4), 4), 0xDEADBEEFu);
        address += 8;

        renderer.ExecuteRdpCommandBuffer(baseAddress, address);

        Assert.Equal(0xDEADBEEFu, renderer.FillColor);
        Assert.Equal(0, renderer.UnsupportedCommands);
    }

    /// <summary>
    /// Hardware opcode == display-list opcode - 0xC0. The 0x2A-0x30 block used
    /// to be shifted one slot high, which made LOAD_TLUT decode as
    /// SET_OTHER_MODES and left SET_SCISSOR entirely unhandled.
    /// </summary>
    [Fact]
    public void HardwareScissorOpcodeIsDecoded()
    {
        var cartridge = N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage());
        var memory = new N64Memory(cartridge);
        var renderer = new Fast3dRenderer(memory) { RasterizationEnabled = false };

        // SET_SCISSOR (0x2D): XH/YH in word0 bits 23:12 and 11:0, XL/YL in
        // word1 at the same positions, each 10.2 fixed point.
        BinaryPrimitives.WriteUInt32BigEndian(
            memory.Rdram.AsSpan(0x2000, 4), 0x2D000000u | ((8u * 4) << 12) | (16u * 4));
        BinaryPrimitives.WriteUInt32BigEndian(
            memory.Rdram.AsSpan(0x2004, 4), ((200u * 4) << 12) | (120u * 4));

        renderer.ExecuteRdpCommandBuffer(0x2000, 0x2008);

        Assert.Equal(0, renderer.UnsupportedCommands);
        Assert.Equal(8, renderer.ScissorLeft);
        Assert.Equal(16, renderer.ScissorTop);
        Assert.Equal(200, renderer.ScissorRight);
        Assert.Equal(120, renderer.ScissorBottom);
    }
}
