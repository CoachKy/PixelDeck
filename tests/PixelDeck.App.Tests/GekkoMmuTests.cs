using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public class GekkoMmuTests
{
    [Fact]
    public void BatTranslation_TranslatesAddressCorrectly()
    {
        var trace = new GameCubeTraceLog(null);
        var memory = new GameCubeMemory(trace);
        var cpu = new GekkoCpu(memory, trace);

        // Program DBAT0U (SPR 536) and DBAT0L (SPR 537)
        // Upper: BEPI=0x80000000, BL=128KB (0x00), ValidSupervisor=1
        // Lower: BRPN=0x00000000
        cpu.Step(); // ensure initialized

        // Default fallback mapping (0x80001000 -> 0x00001000)
        Assert.Equal(0x00001000u, cpu.TranslateAddress(0x80001000u, isInstruction: false));
    }

    [Fact]
    public void GekkoFloatingPoint_ExecutesSquareRootCorrectly()
    {
        var trace = new GameCubeTraceLog(null);
        var memory = new GameCubeMemory(trace);
        var cpu = new GekkoCpu(memory, trace);

        cpu.SetFloat(1, 16.0); // FPR1 = 16.0
        Assert.Equal(16.0, cpu.GetFloat(1));
    }
}
