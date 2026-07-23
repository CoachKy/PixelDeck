using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.Tests;

public sealed class SnesDspTests
{
    [Fact]
    public void BrrVoiceProducesStereoSamples()
    {
        var ram = CreateLoopingSampleRam();
        var dsp = new SnesDsp(ram);
        ConfigureVoice(dsp, voice: 0, leftVolume: 0x7F, rightVolume: 0x81);

        dsp.WriteRegister(0x4C, 0x01);
        ClockSamples(dsp, 96);

        var samples = new float[192];
        var read = dsp.ReadSamples(samples);

        Assert.Equal(samples.Length, read);
        Assert.Contains(samples, sample => Math.Abs(sample) > 0.01f);
        Assert.Contains(
            Enumerable.Range(0, read / 2),
            frame => Math.Abs(samples[frame * 2]) > 0.01f &&
                     Math.Sign(samples[frame * 2]) == -Math.Sign(samples[(frame * 2) + 1]));
    }

    [Fact]
    public void AllEightBrrVoicesCanRunTogether()
    {
        var ram = CreateLoopingSampleRam();
        var dsp = new SnesDsp(ram);
        for (var voice = 0; voice < 8; voice++)
        {
            ConfigureVoice(dsp, voice, leftVolume: 0x18, rightVolume: 0x18);
        }

        dsp.WriteRegister(0x4C, 0xFF);
        ClockSamples(dsp, 64);

        Assert.Equal(8, dsp.ActiveVoiceCount);
        var samples = new float[128];
        Assert.Equal(samples.Length, dsp.ReadSamples(samples));
        Assert.Contains(samples, sample => Math.Abs(sample) > 0.01f);
    }

    [Fact]
    public void DspStateRestoresVoiceAndEnvelopeProgress()
    {
        var ram = CreateLoopingSampleRam();
        var dsp = new SnesDsp(ram);
        ConfigureVoice(dsp, voice: 0, leftVolume: 0x7F, rightVolume: 0x7F);
        dsp.WriteRegister(0x4C, 0x01);
        ClockSamples(dsp, 48);

        using var state = new MemoryStream();
        using (var writer = new BinaryWriter(state, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            dsp.SaveState(writer);
        }

        var expected = new float[64];
        dsp.ClearSamples();
        ClockSamples(dsp, 32);
        dsp.ReadSamples(expected);

        state.Position = 0;
        using (var reader = new BinaryReader(state, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            dsp.LoadState(reader);
        }

        var actual = new float[64];
        ClockSamples(dsp, 32);
        dsp.ReadSamples(actual);

        Assert.Equal(expected, actual);
        Assert.Equal(1, dsp.ActiveVoiceCount);
    }

    private static byte[] CreateLoopingSampleRam()
    {
        var ram = new byte[64 * 1024];
        const int directory = 0x0200;
        const int sample = 0x0300;
        ram[directory] = (byte)(sample & 0xFF);
        ram[directory + 1] = (byte)(sample >> 8);
        ram[directory + 2] = (byte)(sample & 0xFF);
        ram[directory + 3] = (byte)(sample >> 8);
        ram[sample] = 0xC3; // Range 12, filter 0, loop and end.
        for (var index = 0; index < 8; index++)
        {
            ram[sample + 1 + index] = index < 4 ? (byte)0x77 : (byte)0x88;
        }

        return ram;
    }

    private static void ConfigureVoice(SnesDsp dsp, int voice, byte leftVolume, byte rightVolume)
    {
        var registerBase = (byte)(voice << 4);
        dsp.WriteRegister(registerBase, leftVolume);
        dsp.WriteRegister((byte)(registerBase + 1), rightVolume);
        dsp.WriteRegister((byte)(registerBase + 2), 0x00);
        dsp.WriteRegister((byte)(registerBase + 3), 0x10); // Pitch 1.0.
        dsp.WriteRegister((byte)(registerBase + 4), 0x00);
        dsp.WriteRegister((byte)(registerBase + 5), 0x00); // Direct GAIN.
        dsp.WriteRegister((byte)(registerBase + 7), 0x7F);
        dsp.WriteRegister(0x0C, 0x7F);
        dsp.WriteRegister(0x1C, 0x7F);
        dsp.WriteRegister(0x5D, 0x02);
        dsp.WriteRegister(0x6C, 0x20); // Unmute, disable echo writes.
    }

    private static void ClockSamples(SnesDsp dsp, int samples)
    {
        for (var cycle = 0; cycle < samples * 32; cycle++)
        {
            dsp.ClockCycle();
        }
    }
}
