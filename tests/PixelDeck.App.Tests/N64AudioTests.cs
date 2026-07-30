using PixelDeck.Emulation.N64;
using Xunit.Abstractions;

namespace PixelDeck.App.Tests;

public sealed class N64AudioTests(ITestOutputHelper output)
{
    [Fact]
    public void AudioListLoadsInterleavesAndSavesStereoPcm()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        var processor = new N64AudioProcessor(memory);
        memory.WriteUInt16(0x80002000, 0x1111);
        memory.WriteUInt16(0x80002002, 0x2222);
        memory.WriteUInt16(0x80002100, 0x3333);
        memory.WriteUInt16(0x80002102, 0x4444);

        var commands = new uint[]
        {
            0x07000000, 0x00000000, // A_SEGMENT 0 -> 0
            0x08000000, 0x00000004, // A_SETBUFF in=0x000 count=4
            0x04000000, 0x00002000, // A_LOADBUFF left
            0x08000100, 0x00000004, // A_SETBUFF in=0x100 count=4
            0x04000000, 0x00002100, // A_LOADBUFF right
            0x08000000, 0x03000004, // A_SETBUFF out=0x300 count=4
            0x0D000000, 0x00000100, // A_INTERLEAVE left=0x000 right=0x100
            0x08000000, 0x03000008, // A_SETBUFF out=0x300 count=8
            0x06000000, 0x00003000  // A_SAVEBUFF -> 0x3000
        };
        WriteCommandList(memory, 0x1000, commands);

        processor.Execute(new N64RspTask(
            2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x1000, (uint)(commands.Length * 4), 0, 0));

        Assert.Equal(0x1111, memory.ReadUInt16(0x80003000));
        Assert.Equal(0x3333, memory.ReadUInt16(0x80003002));
        Assert.Equal(0x2222, memory.ReadUInt16(0x80003004));
        Assert.Equal(0x4444, memory.ReadUInt16(0x80003006));
        Assert.Equal(0, processor.UnsupportedCommands);
    }

    [Fact]
    public void AdpcmWithZeroCodebookDecodesResidualsVerbatim()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        var processor = new N64AudioProcessor(memory);

        // One 9-byte frame: scale 0, predictor 0, nibbles 1,2,3,...,7,-8,...
        var frame = new byte[] { 0x00, 0x12, 0x34, 0x56, 0x77, 0x89, 0xAB, 0xCD, 0xEF };
        for (var index = 0; index < frame.Length; index++)
        {
            memory.WriteByte((uint)(0x80002000 + index), frame[index]);
        }

        var commands = new uint[]
        {
            0x07000000, 0x00000000, // A_SEGMENT 0 -> 0
            0x0B000020, 0x00004000, // A_LOADADPCM 32 bytes (all zero) from 0x4000
            0x08000000, 0x00000010, // A_SETBUFF in=0x000 count=16
            0x04000000, 0x00002000, // A_LOADBUFF frame bytes
            0x08000000, 0x01000020, // A_SETBUFF in=0x000 out=0x100 count=32
            0x01010000, 0x00005000, // A_ADPCM A_INIT, state at 0x5000
            0x08000000, 0x01000040, // save history + decoded output (64 bytes)
            0x06000000, 0x00003000  // A_SAVEBUFF -> 0x3000
        };
        WriteCommandList(memory, 0x1000, commands);

        processor.Execute(new N64RspTask(
            2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x1000, (uint)(commands.Length * 4), 0, 0));

        // ABI ADPCM output starts with the previous 16-sample history window.
        for (var index = 0; index < 16; index++)
        {
            Assert.Equal(0, memory.ReadUInt16((uint)(0x80003000 + (index * 2))));
        }

        short[] expected = [1, 2, 3, 4, 5, 6, 7, 7, -8, -7, -6, -5, -4, -3, -2, -1];
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(
                expected[index],
                unchecked((short)memory.ReadUInt16((uint)(0x80003020 + (index * 2)))));
        }

        // The decoder persists the trailing window for the next continuation.
        Assert.Equal(
            unchecked((ushort)expected[^1]),
            memory.ReadUInt16(0x80005000 + 30));
        Assert.Equal(0, processor.UnsupportedCommands);
    }

    [Fact]
    public void TwoBitAdpcmDecodesFourSamplesPerSourceByte()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        var processor = new N64AudioProcessor(memory);
        byte[] frame = [0x00, 0x1B, 0x1B, 0x1B, 0x1B];
        for (var index = 0; index < frame.Length; index++)
        {
            memory.WriteByte((uint)(0x80002000 + index), frame[index]);
        }

        var commands = new uint[]
        {
            0x0B000020, 0x00004000, // zero predictor coefficients
            0x08000000, 0x00000008, // input=0, load one aligned frame
            0x04000000, 0x00002000,
            0x08000000, 0x01000020, // output=0x100, sixteen samples
            0x01050000, 0x00005000, // A_INIT | A_2
            0x08000000, 0x01000040,
            0x06000000, 0x00003000
        };
        WriteCommandList(memory, 0x1000, commands);

        processor.Execute(new N64RspTask(
            2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0x1000, (uint)(commands.Length * 4), 0, 0));

        short[] expected = [0, 1, -2, -1, 0, 1, -2, -1, 0, 1, -2, -1, 0, 1, -2, -1];
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(
                expected[index],
                unchecked((short)memory.ReadUInt16(
                    (uint)(0x80003020 + (index * 2)))));
        }
    }

    [Fact]
    public void AdpcmHighScaleSaturatesAtTheAbiFourBitMagnitude()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        var processor = new N64AudioProcessor(memory);
        memory.WriteByte(0x80002000, 0xF0);
        for (var index = 1; index < 9; index++)
        {
            memory.WriteByte((uint)(0x80002000 + index), 0x11);
        }

        var commands = new uint[]
        {
            0x07000000, 0x00000000,
            0x0B000020, 0x00004000,
            0x08000000, 0x00000010,
            0x04000000, 0x00002000,
            0x08000000, 0x01000020,
            0x01010000, 0x00005000,
            0x08000000, 0x01000040,
            0x06000000, 0x00003000
        };
        WriteCommandList(memory, 0x1000, commands);

        processor.Execute(new N64RspTask(
            2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0x1000, (uint)(commands.Length * 4), 0, 0));

        for (var index = 0; index < 16; index++)
        {
            Assert.Equal(
                4_096,
                unchecked((short)memory.ReadUInt16(0x80003020u + (uint)(index * 2))));
        }
    }

    [Fact]
    public void ResampleProducesStableConstantPcmAfterItsHistoryWindow()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        var processor = new N64AudioProcessor(memory);
        const short sampleValue = 12_000;
        for (var index = 0; index < 16; index++)
        {
            memory.WriteUInt16(
                (uint)(0x80002000 + (index * 2)),
                unchecked((ushort)sampleValue));
        }

        var commands = new uint[]
        {
            0x07000000, 0x00000000, // A_SEGMENT 0 -> 0
            0x08000000, 0x00000020, // A_SETBUFF in=0x000 count=32
            0x04000000, 0x00002000, // A_LOADBUFF source PCM
            0x08000000, 0x01000010, // A_SETBUFF in=0x000 out=0x100 count=16
            0x05018000, 0x00005000, // A_RESAMPLE A_INIT, 1:1 pitch
            0x08000000, 0x01000010, // A_SETBUFF out=0x100 count=16
            0x06000000, 0x00003000  // A_SAVEBUFF -> 0x3000
        };
        WriteCommandList(memory, 0x1000, commands);

        processor.Execute(new N64RspTask(
            2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x1000, (uint)(commands.Length * 4), 0, 0));

        // A_INIT starts with an empty four-sample history. The exact ABI
        // coefficient ROM gradually introduces the constant input and has a
        // Q15 DC sum of 32779 at phase zero.
        short[] expected = [0, -13, 1_232, 10_858, 12_004, 12_004, 12_004, 12_004];
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(
                expected[index],
                unchecked((short)memory.ReadUInt16((uint)(0x80003000 + (index * 2)))));
        }

        Assert.Equal(0, processor.UnsupportedCommands);
    }

    [Fact]
    public void ResamplePersistsTheNextAbiHistoryWindow()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        var processor = new N64AudioProcessor(memory);
        short[] source = [101, 203, 307, 409, 503, 601, 701, 809, 907, 1_009, 1_103, 1_201];
        for (var index = 0; index < source.Length; index++)
        {
            memory.WriteUInt16(
                (uint)(0x80002000 + (index * 2)),
                unchecked((ushort)source[index]));
        }

        var commands = new uint[]
        {
            0x07000000, 0x00000000, // A_SEGMENT 0 -> 0
            0x08000000, 0x00000020, // A_SETBUFF in=0x000 count=32
            0x04000000, 0x00002000, // A_LOADBUFF source PCM
            0x08000000, 0x01000010, // A_SETBUFF in=0x000 out=0x100 count=16
            0x05018000, 0x00005000  // A_RESAMPLE A_INIT, 1:1 pitch
        };
        WriteCommandList(memory, 0x1000, commands);

        processor.Execute(new N64RspTask(
            2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x1000, (uint)(commands.Length * 4), 0, 0));

        // Eight unity-rate outputs advance the four-sample input window by
        // eight positions. The next invocation must therefore resume with
        // source samples 4..7, not replay the preceding history.
        for (var index = 0; index < 4; index++)
        {
            Assert.Equal(
                unchecked((ushort)source[index + 4]),
                memory.ReadUInt16((uint)(0x80005000 + (index * 2))));
        }

        Assert.Equal(0, memory.ReadUInt16(0x80005008));
    }

    [Fact]
    public void PoleFilterAppliesQ14FeedbackAndPersistsItsTrailingOutput()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        var processor = new N64AudioProcessor(memory);
        for (var index = 0; index < 16; index++)
        {
            memory.WriteUInt16(
                (uint)(0x80002000 + (index * 2)),
                (ushort)((index + 1) * 100));
        }

        // Response 1 feeds the previous group's penultimate output into its
        // first sample. Response 2 feeds both the prior final output and the
        // preceding input sample. All coefficients and gain are Q14.
        memory.WriteUInt16(0x80004000, 0x4000);
        memory.WriteUInt16(0x80004010, 0x2000);
        var commands = new uint[]
        {
            0x07000000, 0x00000000, // A_SEGMENT 0 -> 0
            0x0B000020, 0x00004000, // A_LOADADPCM 16 filter coefficients
            0x08000000, 0x00000020, // A_SETBUFF in=0x000 count=32
            0x04000000, 0x00002000, // A_LOADBUFF source PCM
            0x08000000, 0x01000020, // A_SETBUFF in=0x000 out=0x100 count=32
            0x0E012000, 0x00005000, // A_POLEF A_INIT, gain=0.5
            0x08000000, 0x01000020, // A_SETBUFF out=0x100 count=32
            0x06000000, 0x00003000  // A_SAVEBUFF -> 0x3000
        };
        WriteCommandList(memory, 0x1000, commands);

        processor.Execute(new N64RspTask(
            2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x1000, (uint)(commands.Length * 4), 0, 0));

        short[] expected =
        [
            50, 125, 200, 275, 350, 425, 500, 575,
            1_237, 725, 800, 875, 950, 1_025, 1_100, 1_175
        ];
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(
                expected[index],
                unchecked((short)memory.ReadUInt16((uint)(0x80003000 + (index * 2)))));
        }

        Assert.Equal((ushort)950, memory.ReadUInt16(0x80005000));
        Assert.Equal((ushort)1_025, memory.ReadUInt16(0x80005002));
        Assert.Equal((ushort)1_100, memory.ReadUInt16(0x80005004));
        Assert.Equal((ushort)1_175, memory.ReadUInt16(0x80005006));
        Assert.Equal(0, processor.UnsupportedCommands);
    }

    [Fact]
    public void AudioMicrocodeDetectionDistinguishesAbi1VariantsAndNeadMarioKart()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        var processor = new N64AudioProcessor(memory);
        uint[] commands = [0, 0];
        WriteCommandList(memory, 0x1000, commands);

        memory.WriteUInt32(0x80006000, 1);
        memory.WriteUInt32(0x80006030, 0xF0000F00);
        memory.WriteUInt32(0x80006028, 0x1DC8138C);
        processor.Execute(new N64RspTask(
            2, 0, 0, 0, 0, 0, 0x6000, 0x40, 0, 0, 0, 0, 0x1000, 8, 0, 0));

        Assert.Equal(N64AudioMicrocode.Abi1GoldenEye, processor.DetectedMicrocode);

        memory.WriteUInt32(0x80006030, 0);
        memory.WriteUInt32(0x80006010, 0x11181350);
        processor.Execute(new N64RspTask(
            2, 0, 0, 0, 0, 0, 0x6000, 0x40, 0, 0, 0, 0, 0x1000, 8, 0, 0));

        Assert.Equal(N64AudioMicrocode.NeadMarioKart, processor.DetectedMicrocode);
        Assert.Equal(0, processor.UnsupportedCommands);
    }

    [Fact]
    public void MusyxV1TaskTreatsDataSizeAsASubframeCountAndWritesStereoPcm()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        var processor = new N64AudioProcessor(memory);
        const uint microcodeData = 0x6000;
        const uint subframe = 0x1000;
        const uint state = 0x5000;
        const uint outputAddress = 0x7000;
        const uint voice = subframe + 0x10;

        memory.WriteUInt32(0x80000000 + microcodeData + 0x10, 1);
        memory.WriteUInt32(0x80000000 + subframe + 0x08, state);
        memory.WriteUInt32(0x80000000 + voice + 0x44, outputAddress);
        // MusyX stores the high halves of four 32-bit base values first,
        // followed by all four low halves.
        memory.WriteUInt16(0x80000000 + state + 0x100, 0);
        memory.WriteUInt16(0x80000000 + state + 0x102, 0xFFFF);
        memory.WriteUInt16(0x80000000 + state + 0x108, 1_000);
        memory.WriteUInt16(0x80000000 + state + 0x10A, unchecked((ushort)-1_000));

        processor.Execute(new N64RspTask(
            2, 0, 0, 0, 0, 0, microcodeData, 0x20, 0, 0, 0, 0,
            subframe, 1, 0, 0));

        Assert.Equal(N64AudioMicrocode.MusyxV1, processor.DetectedMicrocode);
        Assert.Equal(969, unchecked((short)memory.ReadUInt16(0x80000000 + outputAddress)));
        Assert.Equal(-970, unchecked((short)memory.ReadUInt16(0x80000000 + outputAddress + 2)));
        Assert.Equal(0, processor.UnsupportedCommands);
        Assert.Equal(1, processor.CommandsProcessed);
    }

    [Fact]
    public void MusyxV1PcmVoiceResamplesAndMixesAnActiveVoice()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        var processor = new N64AudioProcessor(memory);
        const uint microcodeData = 0x6000;
        const uint subframe = 0x1000;
        const uint state = 0x5000;
        const uint outputAddress = 0x7000;
        const uint sourceAddress = 0x7400;
        const uint voice = subframe + 0x10;

        memory.WriteUInt32(0x80000000 + microcodeData + 0x10, 1);
        memory.WriteUInt32(0x80000000 + subframe + 0x08, state);
        memory.WriteUInt32(0x80000000 + voice, 0x7FFF0000);
        memory.WriteUInt32(0x80000000 + voice + 4, 0x7FFF0000);
        memory.WriteUInt16(0x80000000 + voice + 0x22, 0x1000);
        memory.WriteUInt32(0x80000000 + voice + 0x24, sourceAddress);
        memory.WriteUInt16(0x80000000 + voice + 0x2C, 16);
        memory.WriteUInt16(0x80000000 + voice + 0x40, 8);
        memory.WriteUInt32(0x80000000 + voice + 0x44, outputAddress);
        memory.WriteUInt16(0x80000000 + voice + 0x48, 8);
        for (var sample = 0; sample < 8; sample++)
        {
            memory.WriteUInt16(
                0x80000000 + sourceAddress + (uint)(sample * 2),
                1_000);
        }

        processor.Execute(new N64RspTask(
            2, 0, 0, 0, 0, 0, microcodeData, 0x20, 0, 0, 0, 0,
            subframe, 1, 0, 0));

        var left = unchecked((short)memory.ReadUInt16(0x80000000 + outputAddress));
        var right = unchecked((short)memory.ReadUInt16(0x80000000 + outputAddress + 2));
        Assert.InRange(left, (short)900, (short)1_100);
        Assert.InRange(right, (short)900, (short)1_100);
        Assert.Equal(0, processor.UnsupportedCommands);
    }

    [Fact]
    public void NeadMarioKartListLoadsInterleavesAndSavesStereoPcm()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        var processor = new N64AudioProcessor(memory);
        memory.WriteUInt32(0x80006000, 1);
        memory.WriteUInt32(0x80006010, 0x11181350);
        memory.WriteUInt16(0x80002000, 0x1111);
        memory.WriteUInt16(0x80002002, 0x2222);
        memory.WriteUInt16(0x80002100, 0x3333);
        memory.WriteUInt16(0x80002102, 0x4444);

        var commands = new uint[]
        {
            0x14004000, 0x00002000, // four bytes -> DMEM 0x000
            0x14004100, 0x00002100, // four bytes -> DMEM 0x100
            0x08000000, 0x03000004, // output=0x300, channel count=4
            0x0D000000, 0x00000100, // interleave left=0x000, right=0x100
            0x15008300, 0x00003000  // eight bytes from DMEM 0x300
        };
        WriteCommandList(memory, 0x1000, commands);

        processor.Execute(new N64RspTask(
            2, 0, 0, 0, 0, 0, 0x6000, 0x40, 0, 0, 0, 0,
            0x1000, (uint)(commands.Length * 4), 0, 0));

        Assert.Equal(N64AudioMicrocode.NeadMarioKart, processor.DetectedMicrocode);
        Assert.Equal(0x1111, memory.ReadUInt16(0x80003000));
        Assert.Equal(0x3333, memory.ReadUInt16(0x80003002));
        Assert.Equal(0x2222, memory.ReadUInt16(0x80003004));
        Assert.Equal(0x4444, memory.ReadUInt16(0x80003006));
        Assert.Equal(0, processor.UnsupportedCommands);
    }

    [Fact]
    public void NeadOcarinaListUsesTheLaterExplicitInterleaveLayout()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        var processor = new N64AudioProcessor(memory);
        memory.WriteUInt32(0x80006000, 1);
        memory.WriteUInt32(0x80006010, 0x1F681230);
        for (var index = 0; index < 8; index++)
        {
            memory.WriteUInt16((uint)(0x80002000 + (index * 2)), (ushort)(0x1100 + index));
            memory.WriteUInt16((uint)(0x80002100 + (index * 2)), (ushort)(0x3300 + index));
        }

        var commands = new uint[]
        {
            0x14010000, 0x00002000, // sixteen bytes -> DMEM 0x000
            0x14010100, 0x00002100, // sixteen bytes -> DMEM 0x100
            0x0D010300, 0x00000100, // sixteen bytes/channel -> DMEM 0x300
            0x15020300, 0x00003000  // thirty-two interleaved bytes
        };
        WriteCommandList(memory, 0x1000, commands);

        processor.Execute(new N64RspTask(
            2, 0, 0, 0, 0, 0, 0x6000, 0x40, 0, 0, 0, 0,
            0x1000, (uint)(commands.Length * 4), 0, 0));

        Assert.Equal(N64AudioMicrocode.NeadZeldaOcarinaOfTime, processor.DetectedMicrocode);
        Assert.Equal(0x1100, memory.ReadUInt16(0x80003000));
        Assert.Equal(0x3300, memory.ReadUInt16(0x80003002));
        Assert.Equal(0x1101, memory.ReadUInt16(0x80003004));
        Assert.Equal(0x3301, memory.ReadUInt16(0x80003006));
        Assert.Equal(0, processor.UnsupportedCommands);
    }

    [Fact]
    public void NAudioListUsesItsFixedDmemMapForStereoOutput()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        var processor = new N64AudioProcessor(memory);
        memory.WriteUInt32(0x80006010, 0x0000127C);
        memory.WriteUInt16(0x80002000, 0x1111);
        memory.WriteUInt16(0x80002002, 0x2222);
        memory.WriteUInt16(0x80002100, 0x3333);
        memory.WriteUInt16(0x80002102, 0x4444);

        var commands = new uint[]
        {
            0x040044E0, 0x00002000, // four bytes -> NAudio dry-left
            0x04004650, 0x00002100, // four bytes -> NAudio dry-right
            0x0D000000, 0x00000000, // fixed-map interleave -> NAudio main
            0x06008000, 0x00003000  // eight bytes from NAudio main
        };
        WriteCommandList(memory, 0x1000, commands);

        processor.Execute(new N64RspTask(
            2, 0, 0, 0, 0, 0, 0x6000, 0x40, 0, 0, 0, 0,
            0x1000, (uint)(commands.Length * 4), 0, 0));

        Assert.Equal(N64AudioMicrocode.NAudio, processor.DetectedMicrocode);
        Assert.Equal(0x1111, memory.ReadUInt16(0x80003000));
        Assert.Equal(0x3333, memory.ReadUInt16(0x80003002));
        Assert.Equal(0x2222, memory.ReadUInt16(0x80003004));
        Assert.Equal(0x4444, memory.ReadUInt16(0x80003006));
        Assert.Equal(0, processor.UnsupportedCommands);
    }

    [Fact]
    public void GoldenEyeEnvelopeMixerUsesPerSampleLinearVolumeRamps()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        var processor = new N64AudioProcessor(memory);
        for (var index = 0; index < 8; index++)
        {
            memory.WriteUInt16(0x80002000u + (uint)(index * 2), 10_000);
        }

        memory.WriteUInt32(0x80006000, 1);
        memory.WriteUInt32(0x80006030, 0xF0000F00);
        memory.WriteUInt32(0x80006028, 0x1DC8138C);
        var commands = new uint[]
        {
            0x07000000, 0x00000000, // A_SEGMENT 0 -> 0
            0x09060000, 0x00000000, // A_SETVOL left=0
            0x09040000, 0x00000000, // A_SETVOL right=0
            0x09024000, 0x40000000, // left target=0.5, eight-sample ramp
            0x09004000, 0x40000000, // right target=0.5, eight-sample ramp
            0x09087FFF, 0x00000000, // dry=almost unity, wet=0
            0x08000000, 0x00000010, // input=0x000, count=16
            0x04000000, 0x00002000, // load eight input samples
            0x08000000, 0x01000010, // dry left=0x100, count=16
            0x08080200, 0x03000400, // dry right=0x200, wet L/R=0x300/0x400
            0x03090000, 0x00005000, // A_ENVMIXER A_INIT | A_AUX
            0x08000000, 0x01000010,
            0x06000000, 0x00003000, // save dry left
            0x08000000, 0x02000010,
            0x06000000, 0x00003100  // save dry right
        };
        WriteCommandList(memory, 0x1000, commands);

        processor.Execute(new N64RspTask(
            2, 0, 0, 0, 0, 0, 0x6000, 0x40, 0, 0, 0, 0,
            0x1000, (uint)(commands.Length * 4), 0, 0));

        short[] expected = [625, 1_250, 1_875, 2_500, 3_125, 3_750, 4_375, 5_000];
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(
                expected[index],
                unchecked((short)memory.ReadUInt16(0x80003000u + (uint)(index * 2))));
            Assert.Equal(
                expected[index],
                unchecked((short)memory.ReadUInt16(0x80003100u + (uint)(index * 2))));
        }

        Assert.Equal(0x40000000u, memory.ReadUInt32(0x80005008));
        Assert.Equal(0x40000000u, memory.ReadUInt32(0x8000500C));
        Assert.Equal(0u, memory.ReadUInt32(0x80005010));
        Assert.Equal(0u, memory.ReadUInt32(0x80005014));
        Assert.Equal(0x40000000u, memory.ReadUInt32(0x80005020));
        Assert.Equal(0x40000000u, memory.ReadUInt32(0x80005024));
    }

    [Fact]
    public void Abi1EnvelopeMixerUsesExponentialRampAndPersistsFullState()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        var processor = new N64AudioProcessor(memory);
        for (var index = 0; index < 8; index++)
        {
            memory.WriteUInt16(0x80002000u + (uint)(index * 2), 10_000);
        }

        var commands = new uint[]
        {
            0x07000000, 0x00000000, // A_SEGMENT 0 -> 0
            0x09061000, 0x00000000, // A_SETVOL left=0x1000
            0x09041000, 0x00000000, // A_SETVOL right=0x1000
            0x09024000, 0x00020000, // left target=0x4000, exponential rate=2.0
            0x09004000, 0x00020000, // right target=0x4000, exponential rate=2.0
            0x09087FFF, 0x00000000, // dry=almost unity, wet=0
            0x08000000, 0x00000010, // input=0x000, count=16
            0x04000000, 0x00002000, // load eight input samples
            0x08000000, 0x01000010, // dry left=0x100, count=16
            0x08080200, 0x03000400, // dry right=0x200, wet L/R=0x300/0x400
            0x03090000, 0x00005000, // A_ENVMIXER A_INIT | A_AUX
            0x08000000, 0x01000010,
            0x06000000, 0x00003000, // save dry left
            0x08000000, 0x02000010,
            0x06000000, 0x00003100  // save dry right
        };
        WriteCommandList(memory, 0x1000, commands);

        processor.Execute(new N64RspTask(
            2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0x1000, (uint)(commands.Length * 4), 0, 0));

        short[] expected = [1_718, 2_187, 2_656, 3_125, 3_593, 4_062, 4_531, 5_000];
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(
                expected[index],
                unchecked((short)memory.ReadUInt16(0x80003000u + (uint)(index * 2))));
            Assert.Equal(
                expected[index],
                unchecked((short)memory.ReadUInt16(0x80003100u + (uint)(index * 2))));
        }

        Assert.Equal(0x7FFF, memory.ReadUInt16(0x80005004));
        Assert.Equal(0x40000000u, memory.ReadUInt32(0x80005008));
        Assert.Equal(0x40000000u, memory.ReadUInt32(0x8000500C));
        Assert.Equal(0x00020000u, memory.ReadUInt32(0x80005010));
        Assert.Equal(0x00020000u, memory.ReadUInt32(0x80005014));
        Assert.Equal(0x40000000u, memory.ReadUInt32(0x80005018));
        Assert.Equal(0x40000000u, memory.ReadUInt32(0x8000501C));
        Assert.Equal(0x40000000u, memory.ReadUInt32(0x80005020));
        Assert.Equal(0x40000000u, memory.ReadUInt32(0x80005024));
    }

    [Fact]
    public void MachineExposesReplaceableAudioBackendBoundary()
    {
        var machine = N64Machine.Create(
            N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));

        Assert.Same(machine.AudioProcessor, machine.AudioBackend);
        Assert.Equal("Pixel64 Audio HLE", machine.AudioBackend.Name);
    }

    [Fact]
    public void LocalSuperMario64ProducesAudibleAudioWhenPresent()
    {
        var path = N64TestSupport.FindSuperMario64();
        if (path is null)
        {
            output.WriteLine("Local Super Mario 64 target is not installed; optional audio gate skipped.");
            return;
        }

        var machine = N64Machine.Load(path);
        var samples = new float[8_192];
        var peak = 0f;
        var totalValues = 0L;
        var squareSum = 0.0;
        var clippedValues = 0L;
        var largeSteps = 0L;
        var previousSamples = new float[2];
        var hasPreviousSamples = new bool[2];
        var audibleField = -1;
        const int maximumFields = 600;
        var field = 0;
        for (; field < maximumFields; field++)
        {
            machine.RunFrame();
            int read;
            while ((read = machine.ReadAudioSamples(samples)) > 0)
            {
                totalValues += read;
                for (var index = 0; index < read; index++)
                {
                    var sample = samples[index];
                    var magnitude = Math.Abs(sample);
                    squareSum += sample * sample;
                    if (magnitude >= 0.999f)
                    {
                        clippedValues++;
                    }

                    var channel = index & 1;
                    if (hasPreviousSamples[channel] &&
                        Math.Abs(sample - previousSamples[channel]) >= 0.5f)
                    {
                        largeSteps++;
                    }

                    previousSamples[channel] = sample;
                    hasPreviousSamples[channel] = true;
                    if (magnitude > peak)
                    {
                        peak = magnitude;
                        if (audibleField < 0 && magnitude > 0.01f)
                        {
                            audibleField = field;
                        }
                    }
                }
            }

            if (audibleField >= 0 && field >= audibleField + 30)
            {
                break;
            }
        }

        output.WriteLine(
            $"fields={field + 1}, audio tasks={machine.AudioTasksSubmitted}, " +
            $"AI DMAs={machine.Memory.AudioDmasCompleted}, sample values={totalValues:N0}, " +
            $"AI rate={machine.Memory.CurrentAudioSampleRate:N0} Hz, " +
            $"peak={peak:0.0000}, first audible field={audibleField}, " +
            $"RMS={Math.Sqrt(squareSum / Math.Max(totalValues, 1)):0.0000}, " +
            $"clipped={clippedValues}/{totalValues}, large steps={largeSteps}/{totalValues}, " +
            $"HLE commands={machine.AudioProcessor.CommandsProcessed:N0}, " +
            $"unsupported={machine.AudioProcessor.UnsupportedCommands}");

        Assert.True(machine.AudioTasksSubmitted > 0, "No audio tasks were submitted.");
        Assert.True(totalValues > 0, "No audio samples were captured from AI DMAs.");
        Assert.Equal(0, machine.AudioProcessor.UnsupportedCommands);
        Assert.Equal(0, machine.DroppedAudioSampleCount);
        Assert.Equal(0, clippedValues);
        Assert.Equal(0, largeSteps);
        Assert.InRange(machine.Memory.CurrentAudioSampleRate, 31_900, 32_100);
        Assert.True(
            peak > 0.01f,
            $"Audio output never became audible within {maximumFields} fields (peak {peak:0.0000}).");
        Assert.True(
            peak < 0.5f,
            $"Mario 64 audio exceeded the clean-signal peak gate ({peak:0.0000}).");
    }

    [Fact]
    public void TraceLocalCartridgeWhenRequested()
    {
        var requested = Environment.GetEnvironmentVariable("PIXEL64_TRACE_CART");
        if (string.IsNullOrWhiteSpace(requested))
        {
            return;
        }

        var path = N64TestSupport.FindCartridges()
            .FirstOrDefault(candidate =>
                Path.GetFileName(candidate).Contains(requested, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(path);
        var cartridge = N64Cartridge.Load(path);
        output.WriteLine(
            $"{cartridge.Title} ({cartridge.GameCode}) {cartridge.Cic} entry=0x{cartridge.EntryPoint:X8}");

        var machine = N64Machine.Create(cartridge);
        var failure = default(Exception);
        var fields = 0;
        var bestFrame = default(uint[]);
        var bestNonBlack = -1;
        var bestField = -1;
        var maximumFields = int.TryParse(
            Environment.GetEnvironmentVariable("PIXEL64_TRACE_FIELDS"),
            out var configuredFields)
            ? configuredFields
            : 600;
        try
        {
            var driveInput = Environment.GetEnvironmentVariable("PIXEL64_TRACE_INPUT") == "1";
            for (; fields < maximumFields; fields++)
            {
                if (driveInput)
                {
                    // Alternating Start/A walks title screens, file selects and
                    // cutscenes without depending on exact per-game timings.
                    var phase = fields % 200;
                    machine.SetControllerState(
                        1,
                        phase switch
                        {
                            >= 20 and < 40 => new N64ControllerState(N64Button.Start, 0, 0),
                            >= 120 and < 140 => new N64ControllerState(N64Button.A, 0, 0),
                            _ => N64ControllerState.Neutral
                        });
                }

                machine.RunFrame();
                if (fields % 10 != 9)
                {
                    continue;
                }

                var candidate = machine.CurrentFrame.ToArray();
                var nonBlack = candidate.Count(pixel => (pixel & 0x00FFFFFF) != 0);
                if (nonBlack > bestNonBlack)
                {
                    bestFrame = candidate;
                    bestNonBlack = nonBlack;
                    bestField = fields;
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        output.WriteLine(
            $"fields={fields}, entry-reached={machine.ReachedCartridgeEntryPoint}, " +
            $"instructions={machine.Cpu.InstructionsExecuted:N0}, PC=0x{machine.Cpu.ProgramCounter:X8}, " +
            $"unsupported-cpu={machine.Cpu.UnsupportedInstructionCount}");
        output.WriteLine(
            $"gfx tasks={machine.GraphicsTasksSubmitted}, audio tasks={machine.AudioTasksSubmitted}, " +
            $"VI IRQs={machine.Memory.VerticalInterruptsRaised}, AI DMAs={machine.Memory.AudioDmasCompleted}, " +
            $"SI polls={machine.Memory.ControllerPolls}, " +
            $"VI origin=0x{machine.Memory.ViOrigin:X8} width={machine.Memory.ViWidth} control=0x{machine.Memory.ViControl:X4}");
        output.WriteLine(
            $"microcode={machine.Renderer.DetectedMicrocode} " +
            $"crc=0x{machine.Renderer.MicrocodeCrc32:X8} " +
            $"banner=\"{machine.Renderer.MicrocodeBanner}\"");
        if (machine.LastGraphicsTask is { } graphicsTask)
        {
            var checksum = 0u;
            for (var offset = 0u;
                 offset < Math.Min(graphicsTask.MicrocodeSize, 4096);
                 offset += 4)
            {
                checksum += machine.Memory.ReadUInt32(
                    graphicsTask.MicrocodePointer + offset);
            }

            output.WriteLine(
                $"last graphics task: ucode=0x{graphicsTask.MicrocodePointer:X8}/" +
                $"{graphicsTask.MicrocodeSize} checksum=0x{checksum:X8}, " +
                $"data=0x{graphicsTask.DataPointer:X8}/{graphicsTask.DataSize}");
        }

        if (machine.LastRspTask is { } task)
        {
            var checksum = 0u;
            for (var offset = 0u; offset < Math.Min(task.MicrocodeSize, 4096); offset += 4)
            {
                checksum += machine.Memory.ReadUInt32(task.MicrocodePointer + offset);
            }

            output.WriteLine(
                $"last RSP task: type={task.Type}, ucode=0x{task.MicrocodePointer:X8}/{task.MicrocodeSize} " +
                $"checksum=0x{checksum:X8}, data=0x{task.DataPointer:X8}/{task.DataSize}");
        }
        else
        {
            output.WriteLine("no RSP task was ever submitted.");
        }

        output.WriteLine(
            $"geometry: verts={machine.Renderer.VerticesTransformed:N0}, " +
            $"tris={machine.Renderer.TrianglesDrawn:N0}, " +
            $"clipRejected={machine.Renderer.TriviallyClippedTriangles:N0}, " +
            $"depthRejected={machine.Renderer.DepthPixelsRejected:N0}, " +
            $"maxTri={machine.Renderer.MaximumTriangleWidth}x{machine.Renderer.MaximumTriangleHeight}, " +
            $"colorImage=0x{machine.Renderer.ColorImageAddress:X6}/{machine.Renderer.ColorImageWidth}");
        output.WriteLine(
            "opcodes: " +
            string.Join(
                " ",
                machine.Renderer.OpcodeHistogram.Take(18)
                    .Select(entry => $"0x{entry.Opcode:X2}:{entry.Count}")));
        output.WriteLine(
            "unsupported texture formats: " +
            string.Join(
                " ",
                machine.Renderer.UnsupportedTextureFormats
                    .Select(entry => $"fmt{entry.Format}/size{entry.Size}:{entry.Count:N0}")));
        output.WriteLine(
            $"renderer: lists={machine.Renderer.DisplayListsProcessed}, " +
            $"commands={machine.Renderer.CommandsProcessed}, triangles={machine.Renderer.TrianglesDrawn}, " +
            $"unsupported={string.Join(", ", machine.Renderer.UnsupportedCommandCounts.Select(pair => $"0x{pair.Key:X2}:{pair.Value}"))}");
        if (machine.Renderer.FirstUnsupportedCommandAddress is { } unsupportedAddress)
        {
            output.WriteLine(
                $"first unsupported graphics command at 0x{unsupportedAddress:X8} " +
                $"({machine.Renderer.FirstUnsupportedCommandContext}): " +
                string.Join(
                    " ",
                    Enumerable.Range(-4, 12)
                        .Select(offset => machine.Memory.ReadUInt32(
                            unsupportedAddress + unchecked((uint)(offset * 4))).ToString("X8"))));
            if (machine.Renderer.FirstUnsupportedListHeaderAddress is { } listHeader)
            {
                for (var offset = 0; offset < 0x110; offset += 16)
                {
                    output.WriteLine(
                        $"f5[0x{listHeader + (uint)offset:X8}] " +
                        string.Join(
                            " ",
                            Enumerable.Range(0, 4)
                                .Select(word => machine.Memory.ReadUInt32(
                                    listHeader + (uint)offset + (uint)(word * 4)).ToString("X8"))));
                }
            }
        }

        if (machine.LastAudioTask is { } audioTask)
        {
            output.WriteLine(
                $"audio microcode={machine.AudioProcessor.DetectedMicrocode}; " +
                $"last task data=0x{audioTask.DataPointer:X8}/{audioTask.DataSize}");
            if (machine.AudioProcessor.DetectedMicrocode == N64AudioMicrocode.MusyxV1)
            {
                var subframe = audioTask.DataPointer;
                var voice = subframe + 0x10;
                var outputAddress = machine.Memory.ReadUInt32(voice + 0x44);
                output.WriteLine(
                    $"MusyX SFD: voices={machine.Memory.ReadUInt16(subframe):X4} " +
                    $"mask=0x{machine.Memory.ReadUInt32(subframe + 4):X8} " +
                    $"state=0x{machine.Memory.ReadUInt32(subframe + 8):X8} " +
                    $"sfx=0x{machine.Memory.ReadUInt32(subframe + 12):X8}; " +
                    $"voice0 sizes={machine.Memory.ReadUInt16(voice + 0x2C)}/" +
                    $"{machine.Memory.ReadUInt16(voice + 0x38)} " +
                    $"frames={machine.Memory.ReadByte(voice + 0x3C)}/" +
                    $"{machine.Memory.ReadByte(voice + 0x3D)} " +
                    $"out=0x{outputAddress:X8} " +
                    $"pcm=0x{machine.Memory.ReadUInt32(outputAddress):X8}");
            }

            if (Environment.GetEnvironmentVariable("PIXEL64_TRACE_AUDIO_COMMANDS") == "1")
            {
                var commandCount = (int)Math.Min(audioTask.DataSize / 8, 64);
                for (var command = 0; command < commandCount; command++)
                {
                    var address = audioTask.DataPointer + (uint)(command * 8);
                    output.WriteLine(
                        $"audio[{command:D2}] " +
                        $"{machine.Memory.ReadUInt32(address):X8} " +
                        $"{machine.Memory.ReadUInt32(address + 4):X8}");
                }
            }
        }
        if (failure is not null)
        {
            output.WriteLine($"halted by: {failure.Message}");
        }

        // The IPL3 boot block: osTvType, osRomType, osRomBase, osResetType,
        // osCicId, osVersion, osMemSize, osAppNMIBuffer.
        output.WriteLine(
            "boot block 0x80000300: " +
            string.Join(
                " ",
                Enumerable.Range(0, 8)
                    .Select(index =>
                        $"0x{machine.Memory.ReadUInt32((uint)(0x80000300 + (index * 4))):X8}")));
        output.WriteLine(
            $"CP0 status=0x{machine.Cpu.ReadCoprocessor0(12):X8} " +
            $"cause=0x{machine.Cpu.ReadCoprocessor0(13):X8} " +
            $"MI mask=0x{machine.Memory.MiInterruptMask:X2} " +
            $"SP status=0x{machine.Memory.SpStatus:X4}");
        output.WriteLine(
            $"boot wait probes: [0xA02FE1C0]=0x{machine.Memory.ReadUInt32(0xA02FE1C0):X8} " +
            $"[0xA030E1C0]=0x{machine.Memory.ReadUInt32(0xA030E1C0):X8}; " +
            $"v0=0x{machine.Cpu.Registers[2]:X16} v1=0x{machine.Cpu.Registers[3]:X16} " +
            $"t6=0x{machine.Cpu.Registers[14]:X16}");
        for (var sample = 0; sample < 8; sample++)
        {
            machine.RunInstructions(1_000);
            output.WriteLine(
                $"PC sample: 0x{machine.Cpu.ProgramCounter:X8} " +
                $"instr=0x{machine.Cpu.LastInstruction:X8}");
        }

        output.WriteLine(
            $"color image=0x{machine.Renderer.ColorImageAddress:X8} " +
            $"width={machine.Renderer.ColorImageWidth} " +
            $"vs VI origin=0x{machine.Memory.ViOrigin & 0x7FFFFF:X8} width={machine.Memory.ViWidth}");
        var horizontal = machine.Memory.ViHorizontalVideo;
        var vertical = machine.Memory.ViVerticalVideo;
        output.WriteLine(
            $"VI regs: width={machine.Memory.ViWidth} " +
            $"hVideo={(horizontal >> 16) & 0x3FF}..{horizontal & 0x3FF} " +
            $"vVideo={(vertical >> 16) & 0x3FF}..{vertical & 0x3FF} " +
            $"xScale=0x{machine.Memory.ViXScale & 0xFFF:X3} " +
            $"yScale=0x{machine.Memory.ViYScale & 0xFFF:X3} " +
            $"control=0x{machine.Memory.ViControl:X5}");
        output.WriteLine($"texture rectangles drawn={machine.Renderer.TextureRectanglesDrawn:N0}");
        WritePpm(
            Path.Combine(Path.GetTempPath(), "pixel64-cart-frame.ppm"),
            machine.CurrentFrame.ToArray(),
            machine.Width,
            machine.Height,
            output);
        if (bestFrame is not null)
        {
            output.WriteLine($"best frame: field={bestField} non-black={bestNonBlack}");
            WritePpm(
                Path.Combine(Path.GetTempPath(), "pixel64-cart-best-frame.ppm"),
                bestFrame,
                machine.Width,
                machine.Height,
                output);
        }
    }


    private static void WritePpm(
        string path,
        uint[] pixels,
        int width,
        int height,
        ITestOutputHelper output)
    {
        using var frame = File.Create(path);
        using var writer = new BinaryWriter(frame);
        writer.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
        foreach (var pixel in pixels)
        {
            writer.Write((byte)(pixel >> 16));
            writer.Write((byte)(pixel >> 8));
            writer.Write((byte)pixel);
        }

        output.WriteLine($"frame={path}");
    }

    private static void WriteCommandList(N64Memory memory, uint address, uint[] commands)
    {
        for (var index = 0; index < commands.Length; index++)
        {
            memory.WriteUInt32(0x80000000 + address + (uint)(index * 4), commands[index]);
        }
    }


    [Fact]
    public void TraceLocalSuperMario64AudioCommandListsWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PIXEL64_TRACE_AUDIO"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var path = N64TestSupport.FindSuperMario64();
        Assert.NotNull(path);
        var machine = N64Machine.Load(path);
        var opcodeHistogram = new Dictionary<uint, long>();
        var listLengths = new Dictionary<uint, long>();
        var seenAudioTasks = 0L;
        var dumpedLists = 0;
        var describedMicrocode = false;
        const int fields = 240;
        const int instructionsPerField = 781_250;
        const int stepSize = 2_000;

        for (var field = 0; field < fields; field++)
        {
            for (var executed = 0; executed < instructionsPerField; executed += stepSize)
            {
                machine.RunInstructions(stepSize);
                if (machine.AudioTasksSubmitted == seenAudioTasks ||
                    machine.LastRspTask is not { Type: 2 } task)
                {
                    continue;
                }

                seenAudioTasks = machine.AudioTasksSubmitted;
                if (!describedMicrocode)
                {
                    describedMicrocode = true;
                    var microcodeChecksum = 0u;
                    for (var offset = 0u; offset < Math.Min(task.MicrocodeSize, 4096); offset += 4)
                    {
                        microcodeChecksum += machine.Memory.ReadUInt32(task.MicrocodePointer + offset);
                    }

                    output.WriteLine(
                        $"microcode ptr=0x{task.MicrocodePointer:X8} size={task.MicrocodeSize} " +
                        $"data=0x{task.MicrocodeDataPointer:X8}/{task.MicrocodeDataSize} " +
                        $"checksum=0x{microcodeChecksum:X8}");
                }

                listLengths[task.DataSize] = listLengths.GetValueOrDefault(task.DataSize) + 1;
                var shouldDump = dumpedLists < 3;
                if (shouldDump)
                {
                    dumpedLists++;
                    output.WriteLine(
                        $"--- audio task #{seenAudioTasks} at field {field}: " +
                        $"data=0x{task.DataPointer:X8} size={task.DataSize} ---");
                }

                for (var offset = 0u; offset + 8 <= task.DataSize; offset += 8)
                {
                    var w0 = machine.Memory.ReadUInt32(task.DataPointer + offset);
                    var w1 = machine.Memory.ReadUInt32(task.DataPointer + offset + 4);
                    var opcode = w0 >> 24;
                    opcodeHistogram[opcode] = opcodeHistogram.GetValueOrDefault(opcode) + 1;
                    if (shouldDump)
                    {
                        output.WriteLine($"  0x{w0:X8} 0x{w1:X8}  op={opcode}");
                    }
                }
            }
        }

        output.WriteLine($"audio tasks observed: {seenAudioTasks}");
        output.WriteLine(
            "list sizes: " +
            string.Join(", ", listLengths.OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Key}B x{pair.Value}")));
        output.WriteLine("opcode histogram (op: count):");
        foreach (var (opcode, count) in opcodeHistogram.OrderBy(pair => pair.Key))
        {
            output.WriteLine($"  {opcode,3} (0x{opcode:X2}): {count:N0}");
        }
    }


}
