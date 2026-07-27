using PixelDeck.Emulation.N64;
using Xunit.Abstractions;

namespace PixelDeck.App.Tests;

public sealed class N64AudioTests(ITestOutputHelper output)
{
    [Fact]
    public void AudioListLoadsInterleavesAndSavesStereoPcm()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(CreateCartridgeImage()));
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
        var memory = new N64Memory(N64Cartridge.FromBytes(CreateCartridgeImage()));
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
            0x08000000, 0x01000020, // A_SETBUFF out=0x100 count=32
            0x06000000, 0x00003000  // A_SAVEBUFF -> 0x3000
        };
        WriteCommandList(memory, 0x1000, commands);

        processor.Execute(new N64RspTask(
            2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x1000, (uint)(commands.Length * 4), 0, 0));

        short[] expected = [1, 2, 3, 4, 5, 6, 7, 7, -8, -7, -6, -5, -4, -3, -2, -1];
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(
                expected[index],
                unchecked((short)memory.ReadUInt16((uint)(0x80003000 + (index * 2)))));
        }

        // The decoder persists the trailing window for the next continuation.
        Assert.Equal(
            unchecked((ushort)expected[^1]),
            memory.ReadUInt16(0x80005000 + 30));
        Assert.Equal(0, processor.UnsupportedCommands);
    }

    [Fact]
    public void LocalSuperMario64ProducesAudibleAudioWhenPresent()
    {
        var path = FindLocalSuperMario64();
        if (path is null)
        {
            output.WriteLine("Local Super Mario 64 target is not installed; optional audio gate skipped.");
            return;
        }

        var machine = N64Machine.Load(path);
        var samples = new float[8_192];
        var peak = 0f;
        var totalValues = 0L;
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
                    var magnitude = Math.Abs(samples[index]);
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
            $"peak={peak:0.0000}, first audible field={audibleField}, " +
            $"HLE commands={machine.AudioProcessor.CommandsProcessed:N0}, " +
            $"unsupported={machine.AudioProcessor.UnsupportedCommands}");

        Assert.True(machine.AudioTasksSubmitted > 0, "No audio tasks were submitted.");
        Assert.True(totalValues > 0, "No audio samples were captured from AI DMAs.");
        Assert.Equal(0, machine.AudioProcessor.UnsupportedCommands);
        Assert.True(
            peak > 0.01f,
            $"Audio output never became audible within {maximumFields} fields (peak {peak:0.0000}).");
    }

    [Fact]
    public void TraceLocalCartridgeWhenRequested()
    {
        var requested = Environment.GetEnvironmentVariable("PIXEL64_TRACE_CART");
        if (string.IsNullOrWhiteSpace(requested))
        {
            return;
        }

        var path = FindLocalNintendo64Cartridges()
            .FirstOrDefault(candidate =>
                Path.GetFileName(candidate).Contains(requested, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(path);
        var cartridge = N64Cartridge.Load(path);
        output.WriteLine(
            $"{cartridge.Title} ({cartridge.GameCode}) {cartridge.Cic} entry=0x{cartridge.EntryPoint:X8}");

        var machine = N64Machine.Create(cartridge);
        var failure = default(Exception);
        var fields = 0;
        try
        {
            for (; fields < 600; fields++)
            {
                machine.RunFrame();
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
            $"renderer: lists={machine.Renderer.DisplayListsProcessed}, " +
            $"commands={machine.Renderer.CommandsProcessed}, triangles={machine.Renderer.TrianglesDrawn}, " +
            $"unsupported={string.Join(", ", machine.Renderer.UnsupportedCommandCounts.Select(pair => $"0x{pair.Key:X2}:{pair.Value}"))}");
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
        for (var sample = 0; sample < 8; sample++)
        {
            machine.RunInstructions(1_000);
            output.WriteLine(
                $"PC sample: 0x{machine.Cpu.ProgramCounter:X8} " +
                $"instr=0x{machine.Cpu.LastInstruction:X8}");
        }
    }

    private static void WriteCommandList(N64Memory memory, uint address, uint[] commands)
    {
        for (var index = 0; index < commands.Length; index++)
        {
            memory.WriteUInt32(0x80000000 + address + (uint)(index * 4), commands[index]);
        }
    }

    private static byte[] CreateCartridgeImage()
    {
        var image = new byte[0x2000];
        image[0] = 0x80;
        image[1] = 0x37;
        image[2] = 0x12;
        image[3] = 0x40;
        image[8] = 0x80;
        image[10] = 0x04;
        "PIXEL64 AUDIO       "u8.CopyTo(image.AsSpan(0x20, 20));
        image[0x3B] = (byte)'N';
        image[0x3C] = (byte)'P';
        image[0x3D] = (byte)'X';
        image[0x3E] = (byte)'E';
        return image;
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

        var path = FindLocalSuperMario64();
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

    private static string? FindLocalSuperMario64()
    {
        return FindLocalNintendo64Cartridges()
            .FirstOrDefault(path =>
            {
                try
                {
                    return N64Cartridge.Inspect(path).IsSuperMario64UsRevision0;
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            });
    }

    private static IEnumerable<string> FindLocalNintendo64Cartridges()
    {
        var configured = Environment.GetEnvironmentVariable("PIXELDECK_GAMES_FOLDER");
        var gamesFolder = string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "Games"))
            : Path.GetFullPath(configured);
        var nintendo64Folder = Path.Combine(gamesFolder, "Nintendo64");
        return Directory.Exists(nintendo64Folder)
            ? Directory.EnumerateFiles(nintendo64Folder, "*", SearchOption.AllDirectories)
                .Where(path =>
                    Path.GetExtension(path).Equals(".z64", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(path).Equals(".n64", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(path).Equals(".v64", StringComparison.OrdinalIgnoreCase))
            : [];
    }
}
