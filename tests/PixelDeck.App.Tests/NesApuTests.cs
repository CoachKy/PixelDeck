using PixelDeck.Emulation.Nes;

namespace PixelDeck.App.Tests;

public sealed class NesApuTests
{
    [Fact]
    public void PulseChannelProducesBoundedNonSilentSamples()
    {
        var apu = new NesApu();
        apu.WriteRegister(0x4015, 0x01);
        apu.WriteRegister(0x4000, 0b0101_1111);
        apu.WriteRegister(0x4001, 0x08);
        apu.WriteRegister(0x4002, 0xFD);
        apu.WriteRegister(0x4003, 0x08);

        apu.Clock(178_977);
        var samples = new float[4_800];
        var count = apu.ReadSamples(samples);

        Assert.InRange(count, 4_799, 4_800);
        Assert.All(samples.AsSpan(0, count).ToArray(), sample => Assert.InRange(sample, -1f, 1f));
        Assert.Contains(samples.AsSpan(0, count).ToArray(), sample => Math.Abs(sample) > 0.001f);
    }

    [Fact]
    public void TriangleAndNoiseChannelsProduceNonSilentSamples()
    {
        var triangle = new NesApu();
        triangle.WriteRegister(0x4015, 0x04);
        triangle.WriteRegister(0x4008, 0xFF);
        triangle.WriteRegister(0x400A, 0x20);
        triangle.WriteRegister(0x400B, 0x08);
        triangle.Clock(100_000);

        var noise = new NesApu();
        noise.WriteRegister(0x4015, 0x08);
        noise.WriteRegister(0x400C, 0x1F);
        noise.WriteRegister(0x400E, 0x03);
        noise.WriteRegister(0x400F, 0x08);
        noise.Clock(100_000);

        Assert.True(ReadPeak(triangle) > 0.001f);
        Assert.True(ReadPeak(noise) > 0.001f);
    }

    [Fact]
    public void MixedOutputKeepsHeadroomInsteadOfHardClipping()
    {
        var apu = new NesApu();
        apu.WriteRegister(0x4015, 0x0F);
        apu.WriteRegister(0x4000, 0xFF);
        apu.WriteRegister(0x4002, 0x08);
        apu.WriteRegister(0x4003, 0x08);
        apu.WriteRegister(0x4004, 0xFF);
        apu.WriteRegister(0x4006, 0x08);
        apu.WriteRegister(0x4007, 0x08);
        apu.WriteRegister(0x4008, 0xFF);
        apu.WriteRegister(0x400A, 0x02);
        apu.WriteRegister(0x400B, 0x08);
        apu.WriteRegister(0x400C, 0x1F);
        apu.WriteRegister(0x400E, 0x00);
        apu.WriteRegister(0x400F, 0x08);
        apu.WriteRegister(0x4011, 0x7F);

        apu.Clock(100_000);
        var samples = new float[apu.BufferedSampleCount];
        apu.ReadSamples(samples);

        Assert.NotEmpty(samples);
        Assert.True(samples.Max(sample => Math.Abs(sample)) < 1.0f);
    }

    [Fact]
    public void DmcSampleChangesTheMixedAudioOutput()
    {
        var apu = new NesApu();
        apu.WriteRegister(0x4010, 0x0F);
        apu.WriteRegister(0x4012, 0x00);
        apu.WriteRegister(0x4013, 0x00);
        apu.WriteRegister(0x4015, 0x10);

        apu.Clock(1);
        Assert.True(apu.DmcDmaPending);
        apu.CompleteDmcDma(0xFF);
        apu.Clock(9_999);

        Assert.True(ReadPeak(apu) > 0.001f);
    }

    [Fact]
    public void FrameCounterRaisesAndStatusReadClearsItsIrq()
    {
        var apu = new NesApu();

        apu.Clock(29_826);
        Assert.False(apu.IrqPending);

        apu.Clock(1);

        Assert.True(apu.IrqPending);
        Assert.True((apu.ReadStatus() & 0x40) != 0);
        Assert.False(apu.IrqPending);
    }

    [Fact]
    public void SoftResetClearsStatusAndIrqButKeepsTheFrameCounterMode()
    {
        var apu = new NesApu();
        apu.WriteRegister(0x4015, 0x01);
        apu.WriteRegister(0x4003, 0x08);
        Assert.True((apu.ReadStatus() & 0x01) != 0);

        apu.WriteRegister(0x4017, 0x80);
        apu.Clock(5);
        apu.Reset(softReset: true);

        Assert.Equal(0, apu.ReadStatus() & 0x5F);
        apu.Clock(29_900);
        Assert.False(apu.IrqPending);

        apu.WriteRegister(0x4015, 0x01);
        apu.WriteRegister(0x4003, 0x08);
        Assert.True((apu.ReadStatus() & 0x01) != 0);
    }

    [Fact]
    public void PowerResetReturnsTheFrameCounterToFourStepMode()
    {
        var apu = new NesApu();
        apu.WriteRegister(0x4017, 0x80);

        apu.Reset(softReset: false);
        apu.Clock(29_831);

        Assert.True(apu.IrqPending);
    }

    [Fact]
    public void DmcQueuesItsBusTransferAndRaisesIrqWhenTheCpuCompletesIt()
    {
        var apu = new NesApu();
        apu.WriteRegister(0x4010, 0x8F);
        apu.WriteRegister(0x4012, 0x00);
        apu.WriteRegister(0x4013, 0x00);
        apu.WriteRegister(0x4015, 0x10);

        apu.Clock(1);

        Assert.True(apu.DmcDmaPending);
        Assert.Equal(0xC000, apu.DmcDmaAddress);
        Assert.False(apu.IrqPending);

        apu.CompleteDmcDma(0xFF);

        Assert.False(apu.DmcDmaPending);
        Assert.True(apu.IrqPending);
        Assert.True((apu.ReadStatus() & 0x80) != 0);

        apu.WriteRegister(0x4015, 0x00);
        Assert.False(apu.IrqPending);
    }

    [Fact]
    public void SaveStateRestoresTheExactAudioSequence()
    {
        var apu = new NesApu();
        apu.WriteRegister(0x4015, 0x01);
        apu.WriteRegister(0x4000, 0b1001_1111);
        apu.WriteRegister(0x4001, 0x08);
        apu.WriteRegister(0x4002, 0x80);
        apu.WriteRegister(0x4003, 0x08);
        apu.Clock(50_000);
        apu.ClearSamples();

        byte[] state;
        using (var stateStream = new MemoryStream())
        {
            using var writer = new BinaryWriter(stateStream);
            apu.SaveState(writer);
            writer.Flush();
            state = stateStream.ToArray();
        }

        apu.Clock(20_000);
        var expected = ReadAllSamples(apu);

        using (var stateStream = new MemoryStream(state, writable: false))
        using (var reader = new BinaryReader(stateStream))
        {
            apu.LoadState(reader);
        }

        apu.Clock(20_000);
        var actual = ReadAllSamples(apu);

        Assert.Equal(expected, actual);
    }

    private static float[] ReadAllSamples(NesApu apu)
    {
        var samples = new float[apu.BufferedSampleCount];
        Assert.Equal(samples.Length, apu.ReadSamples(samples));
        return samples;
    }

    private static float ReadPeak(NesApu apu)
    {
        var samples = ReadAllSamples(apu);
        return samples.Length == 0 ? 0 : samples.Max(sample => Math.Abs(sample));
    }
}
