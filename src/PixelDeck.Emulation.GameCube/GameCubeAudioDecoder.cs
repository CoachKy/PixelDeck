namespace PixelDeck.Emulation.GameCube;

public struct DspVoice
{
    public bool Active { get; set; }
    public uint AramAddress { get; set; }
    public uint Length { get; set; }
    public uint LoopAddress { get; set; }
    public uint CurrentOffset { get; set; }
    public ushort Volume { get; set; }
    public ushort Pitch { get; set; }
    public short Hist1 { get; set; }
    public short Hist2 { get; set; }
}

public sealed class DspVoiceSynthesizer
{
    public DspVoice[] Voices { get; } = new DspVoice[8];

    public void Synthesize(GameCubeMemory memory, Span<short> stereoPcmOutput)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (stereoPcmOutput.IsEmpty) return;

        stereoPcmOutput.Clear();

        Span<short> scratch14 = stackalloc short[14];

        for (var v = 0; v < Voices.Length; v++)
        {
            ref var voice = ref Voices[v];
            if (!voice.Active || voice.CurrentOffset >= voice.Length) continue;

            var addr = voice.AramAddress + voice.CurrentOffset;
            if (!GameCubeMemory.TryTranslate(addr, out var offset) || offset + 8 > memory.MainMemory.Length)
            {
                voice.Active = false;
                continue;
            }

            var adpcmBlock = memory.MainMemory.Slice(offset, 8);
            short h1 = voice.Hist1;
            short h2 = voice.Hist2;
            GameCubeAudioDecoder.DecodeBlock(adpcmBlock, scratch14, ref h1, ref h2);
            voice.Hist1 = h1;
            voice.Hist2 = h2;

            for (var i = 0; i < Math.Min(scratch14.Length, stereoPcmOutput.Length / 2); i++)
            {
                var sample = (short)(scratch14[i] * voice.Volume / 65535);
                var stereoIdx = i * 2;
                stereoPcmOutput[stereoIdx] = (short)Math.Clamp(stereoPcmOutput[stereoIdx] + sample, -32768, 32767);
                stereoPcmOutput[stereoIdx + 1] = (short)Math.Clamp(stereoPcmOutput[stereoIdx + 1] + sample, -32768, 32767);
            }

            voice.CurrentOffset += 8;
        }
    }
}

/// <summary>
/// Nintendo GameCube DSP 4-bit ADPCM sample decoder.
/// </summary>
public static class GameCubeAudioDecoder
{
    private static readonly short[,] CoefficientsTable = new short[8, 2]
    {
        { 0, 0 },
        { 2048, 0 },
        { 0, 2048 },
        { 1024, 1024 },
        { 4096, -2048 },
        { 3584, -1536 },
        { 3072, -1024 },
        { 4608, -2560 }
    };

    public static void DecodeBlock(
        ReadOnlySpan<byte> adpcmBlock,
        Span<short> pcmOutput,
        ref short hist1,
        ref short hist2)
    {
        if (adpcmBlock.Length < 8 || pcmOutput.Length < 14)
        {
            return;
        }

        var header = adpcmBlock[0];
        var predictor = (header >> 4) & 0x07;
        var scale = header & 0x0F;

        var coef1 = CoefficientsTable[predictor, 0];
        var coef2 = CoefficientsTable[predictor, 1];

        var sampleIdx = 0;

        for (var i = 1; i < 8; i++)
        {
            var b = adpcmBlock[i];
            
            // High nibble
            var nibble1 = (sbyte)(b >> 4);
            if (nibble1 >= 8) nibble1 -= 16;
            pcmOutput[sampleIdx++] = DecodeSample(nibble1, scale, coef1, coef2, ref hist1, ref hist2);

            // Low nibble
            var nibble2 = (sbyte)(b & 0x0F);
            if (nibble2 >= 8) nibble2 -= 16;
            pcmOutput[sampleIdx++] = DecodeSample(nibble2, scale, coef1, coef2, ref hist1, ref hist2);
        }
    }

    private static short DecodeSample(
        int nibble,
        int scale,
        short coef1,
        short coef2,
        ref short hist1,
        ref short hist2)
    {
        var sample = (nibble << scale) << 11;
        var prediction = (coef1 * hist1) + (coef2 * hist2);
        var pcm = (sample + prediction + 1024) >> 11;

        if (pcm > 32767) pcm = 32767;
        if (pcm < -32768) pcm = -32768;

        hist2 = hist1;
        hist1 = (short)pcm;

        return (short)pcm;
    }
}
