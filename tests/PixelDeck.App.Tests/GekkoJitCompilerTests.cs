using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public class GekkoJitCompilerTests
{
    [Fact]
    public void JitCompiler_CompilesAndExecutesBasicBlock()
    {
        var trace = new GameCubeTraceLog(null);
        var memory = new GameCubeMemory(trace);
        var cpu = new GekkoCpu(memory, trace);
        var jit = new GekkoJitCompiler();

        // Write an unconditional branch instruction 'b 0x80003020' (0x48000020) at 0x80003000
        memory.WriteUInt32(0x80003000, 0x48000020);

        var compiled = jit.CompileBlock(cpu, 0x80003000);
        Assert.NotNull(compiled);
        Assert.Equal(1, jit.CompiledBlockCount);

        var executed = jit.TryExecuteBlock(cpu, 0x80003000, out var nextPc);
        Assert.True(executed);
        Assert.Equal(0x80003020u, nextPc);
    }

    [Fact]
    public void VertexDecoder_ReadsUByteComponent()
    {
        var data = new byte[] { 255, 128, 64 };
        var offset = 0;

        var val1 = GameCubeVertexDecoder.ReadComponent(data, ref offset, ComponentType.UByte);
        var val2 = GameCubeVertexDecoder.ReadComponent(data, ref offset, ComponentType.UByte);

        Assert.Equal(255f, val1);
        Assert.Equal(128f, val2);
        Assert.Equal(2, offset);
    }

    [Fact]
    public void CisoDiscImage_ParsesValidHeader()
    {
        using var stream = new MemoryStream();
        var header = new byte[0x8000];
        header[0] = (byte)'C';
        header[1] = (byte)'I';
        header[2] = (byte)'S';
        header[3] = (byte)'O';
        header[0x20] = 1; // Block 0 present

        stream.Write(header);
        stream.Position = 0;

        var ciso = new CisoDiscImage(stream);
        Assert.True(ciso.IsBlockPresent(0));
        Assert.False(ciso.IsBlockPresent(0x8000));
    }
}
