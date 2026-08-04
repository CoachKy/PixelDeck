using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public class GekkoAdvancedJitTests
{
    [Fact]
    public void JitCompiler_CompilesAddiInstruction()
    {
        var trace = new GameCubeTraceLog(null);
        var memory = new GameCubeMemory(trace);
        var cpu = new GekkoCpu(memory, trace);
        var jit = new GekkoJitCompiler();

        // addi r3, r0, 42 -> 0x3860002A
        memory.WriteUInt32(0x80003000, 0x3860002A);

        var compiled = jit.CompileBlock(cpu, 0x80003000);
        Assert.NotNull(compiled);

        jit.TryExecuteBlock(cpu, 0x80003000, out _);
        Assert.Equal(42u, cpu.GetGpr(3));
    }

    [Fact]
    public void SymbolMap_ResolvesAddressToName()
    {
        var mapText = "80003100 00000040 00000000 main\n80003200 00000020 00000000 OSInit\n";
        using var reader = new StringReader(mapText);
        var symbolMap = new GameCubeSymbolMap();

        symbolMap.LoadMap(reader);
        Assert.Equal(2, symbolMap.SymbolCount);
        Assert.Equal("main (0x80003100)", symbolMap.Resolve(0x80003100));
    }

    [Fact]
    public void Rtc_CalculatesCorrectSecondsSinceEpoch()
    {
        var targetTime = new DateTime(2000, 1, 1, 1, 0, 0, DateTimeKind.Utc);
        var counter = GameCubeRtc.GetRtcCounter(targetTime);

        Assert.Equal(3600u, counter);
        Assert.Equal(targetTime, GameCubeRtc.GetDateTime(counter));
    }
}
