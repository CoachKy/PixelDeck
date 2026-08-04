using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public class GameCubeAudioSynthesisTests
{
    [Fact]
    public void AudioSynthesizer_GeneratesPcmSamplesFromActiveVoice()
    {
        var trace = new GameCubeTraceLog(null);
        var memory = new GameCubeMemory(trace);
        var synth = new DspVoiceSynthesizer();

        // Setup mock ADPCM block in main RAM
        memory.WriteByte(0, 0x10); // Predictor 1, Scale 0
        for (var i = 1; i < 8; i++)
        {
            memory.WriteByte((uint)i, 0x12);
        }

        synth.Voices[0].Active = true;
        synth.Voices[0].AramAddress = 0;
        synth.Voices[0].Length = 16;
        synth.Voices[0].Volume = 65535;

        Span<short> buffer = stackalloc short[28];
        synth.Synthesize(memory, buffer);

        Assert.True(synth.Voices[0].CurrentOffset > 0);
    }
}
