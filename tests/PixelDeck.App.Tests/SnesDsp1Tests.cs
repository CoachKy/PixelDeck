using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.Tests;

public sealed class SnesDsp1Tests
{
    [Fact]
    public void MultiplyCommandUsesTheDocumentedQ15Interface()
    {
        var dsp = new SnesDsp1();

        WriteCommand(dsp, 0x00, 0x4000, 0x4000);

        Assert.Equal(0x2000, ReadWord(dsp));
        Assert.Equal(0x80, dsp.ReadData());
        Assert.Equal(0x80, dsp.ReadStatus());
        Assert.Equal(1, dsp.GetCommandExecutionCount(0x00));
    }

    [Fact]
    public void TriangleCommandReturnsSineThenCosine()
    {
        var dsp = new SnesDsp1();

        WriteCommand(dsp, 0x04, 0x4000, 0x4000);

        Assert.InRange(ReadSignedWord(dsp), 0x3FFF, 0x4001);
        Assert.InRange(ReadSignedWord(dsp), -1, 1);
    }

    [Fact]
    public void StateRestoresACommandPartWayThroughItsParameters()
    {
        var original = new SnesDsp1();
        original.WriteData(0x00);
        original.WriteData(0x00);
        original.WriteData(0x40);

        byte[] state;
        using (var stream = new MemoryStream())
        {
            using var writer = new BinaryWriter(stream);
            original.SaveState(writer);
            writer.Flush();
            state = stream.ToArray();
        }

        var restored = new SnesDsp1();
        using (var stream = new MemoryStream(state, writable: false))
        using (var reader = new BinaryReader(stream))
        {
            restored.LoadState(reader);
        }

        original.WriteData(0x00);
        original.WriteData(0x40);
        restored.WriteData(0x00);
        restored.WriteData(0x40);

        Assert.Equal(ReadWord(original), ReadWord(restored));
        Assert.Equal(1, restored.GetCommandExecutionCount(0x00));
    }

    [Fact]
    public void RasterCommandStreamsSuccessiveModeSevenMatrices()
    {
        var dsp = new SnesDsp1();
        WriteCommand(
            dsp,
            0x02,
            0,
            0,
            1_024,
            0,
            256,
            0,
            0x2000);
        for (var index = 0; index < 4; index++) ReadWord(dsp);

        WriteCommand(dsp, 0x0A, 1);
        var first = Enumerable.Range(0, 4).Select(_ => ReadSignedWord(dsp)).ToArray();
        var second = Enumerable.Range(0, 4).Select(_ => ReadSignedWord(dsp)).ToArray();

        Assert.Contains(first, value => value != 0);
        Assert.False(first.SequenceEqual(second));
        Assert.Equal(1, dsp.GetCommandExecutionCount(0x0A));
    }

    [Fact]
    public void StreamingRasterMatricesDoNotAllocate()
    {
        var dsp = new SnesDsp1();
        WriteCommand(
            dsp,
            0x02,
            0,
            0,
            1_024,
            0,
            256,
            0,
            0x2000);
        for (var index = 0; index < 8; index++) dsp.ReadData();
        WriteCommand(dsp, 0x0A, 1);
        for (var index = 0; index < 8; index++) dsp.ReadData();

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var line = 0; line < 4_096; line++)
        {
            for (var index = 0; index < 8; index++) dsp.ReadData();
        }
        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(allocatedBefore, allocatedAfter);
    }

    private static void WriteCommand(SnesDsp1 dsp, byte command, params short[] parameters)
    {
        dsp.WriteData(command);
        foreach (var parameter in parameters)
        {
            dsp.WriteData((byte)parameter);
            dsp.WriteData((byte)(parameter >> 8));
        }
    }

    private static ushort ReadWord(SnesDsp1 dsp) =>
        (ushort)(dsp.ReadData() | (dsp.ReadData() << 8));

    private static short ReadSignedWord(SnesDsp1 dsp) =>
        unchecked((short)ReadWord(dsp));
}
