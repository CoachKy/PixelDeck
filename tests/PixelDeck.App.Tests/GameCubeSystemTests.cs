using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public class GameCubeSystemTests
{
    [Fact]
    public void Scheduler_FiresScheduledEventsAtTargetCycle()
    {
        var scheduler = new GameCubeScheduler();
        var fired = false;

        scheduler.ScheduleEvent(100, () => fired = true);
        Assert.False(fired);

        scheduler.Step(50);
        Assert.False(fired);

        scheduler.Step(60);
        Assert.True(fired);
    }

    [Fact]
    public void AudioOutput_WritesAndReadsStereoSamplesCorrectly()
    {
        var audio = new GameCubeAudioOutput();
        var inputSamples = new short[] { 100, -100, 200, -200 };

        audio.WriteSamples(inputSamples);
        Assert.Equal(4, audio.AvailableSamples);

        var outputSamples = new short[4];
        var readCount = audio.ReadSamples(outputSamples);

        Assert.Equal(4, readCount);
        Assert.Equal(inputSamples, outputSamples);
    }

    [Fact]
    public void SaveState_SavesAndRestoresCpuAndMemoryState()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, GameCubeTestSupport.CreateDiscImage());

            var trace = new GameCubeTraceLog(null);
            using var machine = GameCubeMachine.Load(tempFile, trace);

            machine.Cpu.Pc = 0x80003000;
            machine.Cpu.Gpr[3] = 0x12345678;
            machine.Memory.MainMemory[0x100] = 0xAB;

            using var stream = new MemoryStream();
            GameCubeSaveState.Save(machine, stream);

            // Mutate state
            machine.Cpu.Pc = 0;
            machine.Cpu.Gpr[3] = 0;
            machine.Memory.MainMemory[0x100] = 0;

            stream.Position = 0;
            GameCubeSaveState.Load(machine, stream);

            Assert.Equal(0x80003000u, machine.Cpu.Pc);
            Assert.Equal(0x12345678u, machine.Cpu.Gpr[3]);
            Assert.Equal((byte)0xAB, machine.Memory.MainMemory[0x100]);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
