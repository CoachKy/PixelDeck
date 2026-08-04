using PixelDeck.Emulation.N64;
using Xunit;

namespace PixelDeck.App.Tests;

public sealed class N64GameProfileTests
{
    [Fact]
    public void RegistryLooksUpKnownGameCodeProfiles()
    {
        var cartridgeImage = N64TestSupport.CreateCartridgeImage(); // NPXE / Pixel64 Test
        var cartridge = N64Cartridge.FromBytes(cartridgeImage);

        var profile = N64GameProfileRegistry.LookupProfile(cartridge);

        Assert.Equal("NPXE", profile.GameCode);
        Assert.Equal("Pixel64 Test", profile.Title);
        Assert.Equal(N64SaveType.Eeprom4Kbit, profile.SaveType);
        Assert.Equal(N64Cic.Cic6102, profile.CicOverride);
    }

    [Fact]
    public void MachineAppliesProfileRspExecutionMode()
    {
        var cartridgeImage = N64TestSupport.CreateCartridgeImage();
        var cartridge = N64Cartridge.FromBytes(cartridgeImage);

        var machine = N64Machine.Create(cartridge);

        Assert.NotNull(machine.Profile);
        Assert.Equal("NPXE", machine.Profile.GameCode);
        Assert.True(machine.RspProcessor.HleFallbackEnabled);
    }

    [Theory]
    [InlineData("RSP Gfx ucode S2DEX fifo 2.08", "S2DEX")]
    [InlineData("RSP Gfx ucode S2DEX2 fifo 2.08", "S2DEX")]
    [InlineData("RSP Gfx ucode F3DEX fifo 1.21", "F3dex")]
    [InlineData("RSP Gfx ucode F3DEX.NoN fifo 2.08", "F3dex2")]
    [InlineData("RSP Gfx ucode F3DZEX.NoN fifo 2.08", "F3dex2")]
    public void ClassifyMicrocodeDetectsS2dexAndFifo208(string banner, string expected)
    {
        var classified = N64GraphicsTaskProfile.ClassifyMicrocodeName(banner, crc32: 0x12345678);
        Assert.Equal(expected, classified);
    }

    [Theory]
    [InlineData("NPDE", "Perfect Dark")]
    [InlineData("NMQE", "Paper Mario")]
    [InlineData("NZSE", "The Legend of Zelda: Majora's Mask")]
    [InlineData("NGEE", "GoldenEye 007")]
    public void RegistryLooksUpCommercialGameProfiles(string gameCode, string expectedTitle)
    {
        var cartridgeImage = N64TestSupport.CreateCartridgeImage();
        cartridgeImage[0x3B] = (byte)gameCode[0];
        cartridgeImage[0x3C] = (byte)gameCode[1];
        cartridgeImage[0x3D] = (byte)gameCode[2];
        cartridgeImage[0x3E] = (byte)gameCode[3];

        var cartridge = N64Cartridge.FromBytes(cartridgeImage);
        var profile = N64GameProfileRegistry.LookupProfile(cartridge);

        Assert.Equal(gameCode, profile.GameCode);
        Assert.Equal(expectedTitle, profile.Title);
    }
}
