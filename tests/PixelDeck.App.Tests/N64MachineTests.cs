using PixelDeck.Emulation.N64;
using Xunit.Abstractions;

namespace PixelDeck.App.Tests;

public sealed class N64MachineTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(N64ImageByteOrder.BigEndian)]
    [InlineData(N64ImageByteOrder.ByteSwapped)]
    [InlineData(N64ImageByteOrder.LittleEndian)]
    public void CartridgeNormalizesEveryStandardDumpByteOrder(N64ImageByteOrder byteOrder)
    {
        var canonical = CreateCartridgeImage();
        var source = ConvertByteOrder(canonical, byteOrder);

        var cartridge = N64Cartridge.FromBytes(source);

        Assert.Equal(byteOrder, cartridge.SourceByteOrder);
        Assert.Equal("PIXEL64 TEST", cartridge.Title);
        Assert.Equal("NPXE", cartridge.GameCode);
        Assert.Equal(0x80000400u, cartridge.EntryPoint);
        Assert.True(canonical.AsSpan().SequenceEqual(cartridge.Rom));
    }

    [Fact]
    public void ControllerSerializesButtonsAndSignedAnalogStick()
    {
        var controller = new N64ControllerState(
            N64Button.A | N64Button.Start | N64Button.CRight,
            StickX: -80,
            StickY: 72);

        Assert.Equal(0x0190B048u, controller.ToPifWord());
    }

    [Fact]
    public void DelaySlotExceptionRecordsTheBranchEpcAndCauseBdBit()
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0xA4000040, 0x10000002);
        machine.Memory.WriteUInt32(0xA4000044, 0x0000000C);

        machine.RunInstructions(2);

        Assert.Equal(0x80000180u, machine.Cpu.ProgramCounter);
        Assert.Equal(0xA4000040u, machine.Cpu.ReadCoprocessor0(14));
        Assert.Equal(0x80000020u, machine.Cpu.ReadCoprocessor0(13) & 0x8000007C);
    }

    [Fact]
    public void SinglePrecisionArithmeticAndTruncateWordKeepTheCorrectFprFormat()
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0xA4000040, 0x3C083FC0);
        machine.Memory.WriteUInt32(0xA4000044, 0x44880000);
        machine.Memory.WriteUInt32(0xA4000048, 0x46000080);
        machine.Memory.WriteUInt32(0xA400004C, 0x4600110D);
        machine.Memory.WriteUInt32(0xA4000050, 0x44092000);

        machine.RunInstructions(5);

        Assert.Equal(3u, (uint)machine.Cpu.Registers[9]);
    }

    [Fact]
    public void ViCurrentAdvancesWithinAFieldAndInterruptsOnlyOncePerCompareLine()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(CreateCartridgeImage()));
        memory.WriteUInt32(0xA4400018, 9);
        memory.WriteUInt32(0xA440000C, 4);
        var ticksPerLine = memory.CpuTicksPerField / 10;

        memory.AdvanceCpuTicks(ticksPerLine * 4);

        Assert.Equal(4u, memory.ViCurrent);
        Assert.NotEqual(0u, memory.MiInterrupt & (1u << 3));

        memory.WriteUInt32(0xA4400010, 0);
        memory.AdvanceCpuTicks(1);

        Assert.Equal(0u, memory.MiInterrupt & (1u << 3));

        memory.AdvanceCpuTicks(
            memory.CpuTicksPerField - (ticksPerLine * 4) - 1 + (ticksPerLine * 4));

        Assert.Equal(4u, memory.ViCurrent);
        Assert.NotEqual(0u, memory.MiInterrupt & (1u << 3));
    }

    [Fact]
    public void AudioInterfaceQueuesTwoDmasAndInterruptsAsEachCompletes()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(CreateCartridgeImage()));
        memory.WriteUInt32(0xA4500010, 1519);
        memory.WriteUInt32(0xA4500008, 1);
        memory.WriteUInt32(0xA4500000, 0x1000);
        memory.WriteUInt32(0xA4500004, 0x1000);
        memory.WriteUInt32(0xA4500000, 0x2000);
        memory.WriteUInt32(0xA4500004, 0x0800);

        Assert.Equal(0xC0000000u, memory.ReadUInt32(0xA450000C));
        Assert.InRange(memory.ReadUInt32(0xA4500004), 1u, 0x1000u);

        memory.AdvanceCpuTicks(memory.CpuTicksPerField * 4);

        Assert.Equal(2, memory.AudioDmasCompleted);
        Assert.Equal(0u, memory.ReadUInt32(0xA450000C));
        Assert.NotEqual(0u, memory.MiInterrupt & (1u << 2));

        memory.WriteUInt32(0xA450000C, 0);

        Assert.Equal(0u, memory.MiInterrupt & (1u << 2));
    }

    [Fact]
    public void PiDmaCopiesBigEndianCartridgeBytesIntoRdram()
    {
        var image = CreateCartridgeImage();
        image[0x1000] = 0x12;
        image[0x1001] = 0x34;
        image[0x1002] = 0x56;
        image[0x1003] = 0x78;
        var memory = new N64Memory(N64Cartridge.FromBytes(image));

        memory.WriteUInt32(0xA4600000, 0x00000100);
        memory.WriteUInt32(0xA4600004, 0x10001000);
        memory.WriteUInt32(0xA460000C, 3);

        Assert.Equal(0x12345678u, memory.ReadUInt32(0x80000100));
        Assert.NotEqual(0u, memory.MiInterrupt & (1u << 4));
    }

    [Fact]
    public void SiPifReturnsPortOneButtonsAndAnalogStick()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(CreateCartridgeImage()));
        memory.SetControllerState(
            1,
            new N64ControllerState(N64Button.A | N64Button.Z, -60, 42));
        memory.Rdram[0x100] = 1;
        memory.Rdram[0x101] = 4;
        memory.Rdram[0x102] = 1;
        memory.Rdram[0x107] = 0xFE;
        memory.Rdram[0x13F] = 1;

        memory.WriteUInt32(0xA4800000, 0x100);
        memory.WriteUInt32(0xA4800010, 0x1FC007C0);
        memory.WriteUInt32(0xA4800004, 0x1FC007C0);

        Assert.Equal(0x00A0C42Au, memory.ReadUInt32(0x80000103));
        Assert.NotEqual(0u, memory.MiInterrupt & (1u << 1));
    }

    [Fact]
    public void SaveStateRestoresCpuMemoryVideoAndBothControllerPortsExactly()
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0x80000100, 0x12345678);
        machine.Memory.WriteUInt32(0xA4400000, 2);
        machine.Memory.WriteUInt32(0xA4400004, 0x100);
        machine.Memory.WriteUInt32(0xA4400008, 1);
        machine.SetControllerState(1, new(N64Button.A | N64Button.Z, 50, -20));
        machine.SetControllerState(2, new(N64Button.B | N64Button.Start, -40, 10));
        var state = machine.SaveState();

        machine.Memory.WriteUInt32(0x80000100, 0);
        machine.SetControllerState(1, default);
        machine.SetControllerState(2, default);
        machine.LoadState(state);

        Assert.Equal(0x12345678u, machine.Memory.ReadUInt32(0x80000100));
        Assert.Equal(new N64ControllerState(N64Button.A | N64Button.Z, 50, -20), machine.GetControllerState(1));
        Assert.Equal(new N64ControllerState(N64Button.B | N64Button.Start, -40, 10), machine.GetControllerState(2));
    }

    [Fact]
    public void SpTaskSchedulerRecognizesAndCompletesGraphicsTasks()
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0xA4000FC0, 1);
        machine.Memory.WriteUInt32(0xA4000FF0, 0x00123456);
        machine.Memory.WriteUInt32(0xA4000FF4, 0x00000400);
        machine.Memory.WriteUInt32(0xA4000FF8, 0x00654321);
        machine.Memory.WriteUInt32(0xA4000FFC, 0x00000800);
        machine.Memory.WriteUInt32(0xA4040010, 1);

        machine.RunInstructions(1);

        Assert.Equal(1, machine.GraphicsTasksSubmitted);
        Assert.Equal(0x00123456u, machine.LastRspTask?.DataPointer);
        Assert.Equal(0x00000400u, machine.LastRspTask?.DataSize);
        Assert.Equal(0x00654321u, machine.LastRspTask?.YieldDataPointer);
        Assert.Equal(0x00000800u, machine.LastRspTask?.YieldDataSize);
        Assert.Equal(3u, machine.Memory.ReadUInt32(0xA4040010) & 3);
        Assert.NotEqual(0u, machine.Memory.ReadUInt32(0xA4040010) & (1u << 9));
        Assert.NotEqual(0u, machine.Memory.MiInterrupt & (1u << 5));
    }

    [Fact]
    public void Fast3dFillRectangleWritesTheSelectedRgba16ColorImage()
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0xA4000FC0, 1);
        machine.Memory.WriteUInt32(0xA4000FF0, 0x00001000);
        machine.Memory.WriteUInt32(0xA4000FF4, 32);
        machine.Memory.WriteUInt32(0x80001000, 0xFF100003);
        machine.Memory.WriteUInt32(0x80001004, 0x00002000);
        machine.Memory.WriteUInt32(0x80001008, 0xF7000000);
        machine.Memory.WriteUInt32(0x8000100C, 0x7C1F7C1F);
        machine.Memory.WriteUInt32(0x80001010, 0xF600C00C);
        machine.Memory.WriteUInt32(0x80001014, 0x00000000);
        machine.Memory.WriteUInt32(0x80001018, 0xB8000000);
        machine.Memory.WriteUInt32(0x8000101C, 0);
        machine.Memory.WriteUInt32(0xA4040010, 1);

        machine.RunInstructions(1);

        Assert.Equal(0x7C1Fu, machine.Memory.ReadUInt16(0x80002000));
        Assert.Equal(0x7C1Fu, machine.Memory.ReadUInt16(0x8000201E));
        Assert.Equal(1, machine.Renderer.FillRectanglesDrawn);
    }

    [Fact]
    public void MiModeClearDpCommandAcknowledgesTheDisplayProcessorInterrupt()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(CreateCartridgeImage()));
        memory.CompleteDisplayProcessor();

        Assert.NotEqual(0u, memory.MiInterrupt & (1u << 5));

        memory.WriteUInt32(0xA4300000, 1u << 11);

        Assert.Equal(0u, memory.MiInterrupt & (1u << 5));
    }

    [Fact]
    public void LocalSuperMario64CompletesIpl3WhenPresent()
    {
        var path = FindLocalSuperMario64();
        if (path is null)
        {
            output.WriteLine("Local Super Mario 64 target is not installed; optional boot gate skipped.");
            return;
        }

        var cartridge = N64Cartridge.Load(path);
        Assert.True(cartridge.IsSuperMario64UsRevision0);
        Assert.Equal(N64Cic.Cic6102, cartridge.Cic);
        var machine = N64Machine.Create(cartridge);

        for (var index = 0; index < 20_000_000 && !machine.ReachedCartridgeEntryPoint; index++)
        {
            machine.RunInstructions(1);
        }

        output.WriteLine(
            $"PC=0x{machine.Cpu.ProgramCounter:X8}, instructions={machine.Cpu.InstructionsExecuted:N0}, " +
            $"entry=0x{cartridge.EntryPoint:X8}");
        Assert.True(machine.ReachedCartridgeEntryPoint);
        Assert.Equal(0, machine.Cpu.UnsupportedInstructionCount);
    }

    [Fact]
    public void LocalSuperMario64ServicesVideoInterruptsWhenPresent()
    {
        var path = FindLocalSuperMario64();
        if (path is null)
        {
            output.WriteLine("Local Super Mario 64 target is not installed; optional VI gate skipped.");
            return;
        }

        var machine = N64Machine.Load(path);
        for (var frame = 0; frame < 120; frame++)
        {
            machine.RunFrame();
        }

        var visibleColors = machine.CurrentFrame.ToArray().Distinct().Count();
        output.WriteLine(
            $"PC=0x{machine.Cpu.ProgramCounter:X8}, frames={machine.FrameNumber}, " +
            $"instructions={machine.Cpu.InstructionsExecuted:N0}, MI=0x{machine.Memory.MiInterrupt:X2}, " +
            $"mask=0x{machine.Memory.MiInterruptMask:X2}, VI=0x{machine.Memory.ViOrigin:X6}/" +
            $"{machine.Memory.ViWidth}, colors={visibleColors}, gfx={machine.GraphicsTasksSubmitted}, " +
            $"audio={machine.AudioTasksSubmitted}, SP=0x{machine.Memory.SpStatus:X4}, " +
            $"VI IRQs={machine.Memory.VerticalInterruptsRaised}, AI DMAs={machine.Memory.AudioDmasCompleted}, " +
            $"last task={machine.LastRspTask}");
        Assert.Equal(0, machine.Cpu.UnsupportedInstructionCount);
        Assert.True(machine.ReachedCartridgeEntryPoint);
        Assert.True(machine.Memory.VerticalInterruptsRaised >= 100);
        Assert.True(machine.AudioTasksSubmitted >= 1);
    }

    [Fact]
    public void TraceLocalSuperMario64PostBootWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PIXEL64_TRACE_BOOT"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var path = FindLocalSuperMario64();
        Assert.NotNull(path);
        var machine = N64Machine.Load(path);
        var samples = new Dictionary<uint, int>();
        const int fields = 120;
        const int instructionsPerField = 781_250;
        const int sampleInterval = 1_000;

        for (var field = 0; field < fields; field++)
        {
            var interval = field == 13 ? 1 : sampleInterval;
            var previousRunQueue = machine.Memory.ReadUInt32(0x803359A8);
            var previousRunningThread = machine.Memory.ReadUInt32(0x803359B0);
            var previousMiInterrupt = machine.Memory.MiInterrupt;
            var queueChanges = 0;
            for (var executed = 0; executed < instructionsPerField; executed += interval)
            {
                var instructionAddress = machine.Cpu.ProgramCounter;
                machine.RunInstructions(Math.Min(interval, instructionsPerField - executed));
                samples[machine.Cpu.ProgramCounter] =
                    samples.GetValueOrDefault(machine.Cpu.ProgramCounter) + 1;
                var runQueue = machine.Memory.ReadUInt32(0x803359A8);
                var runningThread = machine.Memory.ReadUInt32(0x803359B0);
                if (field == 13 &&
                    (runQueue != previousRunQueue ||
                     runningThread != previousRunningThread ||
                     machine.Memory.MiInterrupt != previousMiInterrupt) &&
                    queueChanges++ < 40)
                {
                    output.WriteLine(
                        $"queue-change at 0x{instructionAddress:X8}/" +
                        $"0x{machine.Cpu.LastInstruction:X8}: runq 0x{previousRunQueue:X8}->" +
                        $"0x{runQueue:X8}, running 0x{previousRunningThread:X8}->" +
                        $"0x{runningThread:X8}, MI 0x{previousMiInterrupt:X2}->" +
                        $"0x{machine.Memory.MiInterrupt:X2}");
                }

                previousRunQueue = runQueue;
                previousRunningThread = runningThread;
                previousMiInterrupt = machine.Memory.MiInterrupt;
            }

            if (field < 20 || field % 10 == 9)
            {
                output.WriteLine(
                    $"field={field + 1:D3} PC=0x{machine.Cpu.ProgramCounter:X8} " +
                    $"status=0x{machine.Cpu.ReadCoprocessor0(12):X8} " +
                    $"cause=0x{machine.Cpu.ReadCoprocessor0(13):X8} " +
                    $"VI={machine.Memory.ViCurrent}/" +
                    $"{machine.Memory.ReadUInt32(0xA440000C)}/" +
                    $"{machine.Memory.ReadUInt32(0xA4400018)} " +
                    $"MI=0x{machine.Memory.MiInterrupt:X2} " +
                    $"runq=0x{machine.Memory.ReadUInt32(0x803359A8):X8} " +
                    $"p359B0=0x{machine.Memory.ReadUInt32(0x803359B0):X8} " +
                    $"p35A20=0x{machine.Memory.ReadUInt32(0x80335A20):X8}");
                if (field is >= 7 and <= 14)
                {
                    var thread = machine.Memory.ReadUInt32(0x803359A8);
                    for (var queueIndex = 0; queueIndex < 8; queueIndex++)
                    {
                        if (thread is < 0x80000000 or > 0x807FFFFF)
                        {
                            output.WriteLine($"  queue[{queueIndex}]=0x{thread:X8} INVALID");
                            break;
                        }

                        var next = machine.Memory.ReadUInt32(thread);
                        var priority = machine.Memory.ReadUInt32(thread + 4);
                        output.WriteLine(
                            $"  queue[{queueIndex}]=0x{thread:X8} priority={priority} next=0x{next:X8}");
                        thread = next;
                    }
                }
            }
        }

        output.WriteLine(
            $"PC=0x{machine.Cpu.ProgramCounter:X8} SP=0x{machine.Cpu.Registers[29]:X16} " +
            $"RA=0x{machine.Cpu.Registers[31]:X16} status=0x{machine.Cpu.ReadCoprocessor0(12):X8} " +
            $"cause=0x{machine.Cpu.ReadCoprocessor0(13):X8} EPC=0x{machine.Cpu.ReadCoprocessor0(14):X8} " +
            $"MI=0x{machine.Memory.MiInterrupt:X2}/{machine.Memory.MiInterruptMask:X2} " +
            $"VI=0x{machine.Memory.ViOrigin:X8}/{machine.Memory.ViWidth}");
        foreach (var (address, count) in samples.OrderByDescending(pair => pair.Value).Take(24))
        {
            output.WriteLine(
                $"0x{address:X8} x{count:N0} instruction=0x{machine.Memory.ReadUInt32(address):X8}");
        }

        for (var address = 0x803274E0u; address <= 0x80327618; address += 4)
        {
            output.WriteLine($"0x{address:X8}: 0x{machine.Memory.ReadUInt32(address):X8}");
        }

        for (var address = 0x80327C40u; address <= 0x80327D48; address += 4)
        {
            output.WriteLine($"0x{address:X8}: 0x{machine.Memory.ReadUInt32(address):X8}");
        }

        for (var register = 0; register < 32; register++)
        {
            output.WriteLine($"r{register:D2}=0x{machine.Cpu.Registers[register]:X16}");
        }

        for (var address = 0x80335980u; address <= 0x80335A40; address += 4)
        {
            output.WriteLine($"0x{address:X8}: 0x{machine.Memory.ReadUInt32(address):X8}");
        }

        foreach (var thread in new[]
                 {
                     0x8033A730u,
                     0x8033A8E0u,
                     0x8033AA90u,
                     0x8033AC40u,
                     0x80364C60u
                 })
        {
            output.WriteLine(
                $"thread 0x{thread:X8}: next=0x{machine.Memory.ReadUInt32(thread):X8} " +
                $"pri={machine.Memory.ReadUInt32(thread + 4)} " +
                $"state=0x{machine.Memory.ReadUInt32(thread + 0x10):X8} " +
                $"sp=0x{machine.Memory.ReadUInt64(thread + 0xF0):X16} " +
                $"ra=0x{machine.Memory.ReadUInt64(thread + 0x100):X16} " +
                $"status=0x{machine.Memory.ReadUInt32(thread + 0x118):X8} " +
                $"epc=0x{machine.Memory.ReadUInt32(thread + 0x11C):X8}");
        }
    }

    [Fact]
    public void TraceLocalSuperMario64SchedulerWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PIXEL64_TRACE_SCHEDULER"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var path = FindLocalSuperMario64();
        Assert.NotNull(path);
        var machine = N64Machine.Load(path);
        for (var field = 0; field < 600; field++)
        {
            machine.RunFrame();
        }
        machine.RunInstructions(100_000);

        foreach (var (name, address) in new (string Name, uint Address)[]
                 {
                     ("gVblankHandler1", 0x8032D560),
                     ("gVblankHandler2", 0x8032D564),
                     ("gActiveSPTask", 0x8032D568),
                     ("sCurrentAudioSPTask", 0x8032D56C),
                     ("sCurrentDisplaySPTask", 0x8032D570),
                     ("sNextAudioSPTask", 0x8032D574),
                     ("sNextDisplaySPTask", 0x8032D578),
                     ("sAudioEnabled", 0x8032D57C),
                     ("gNumVblanks", 0x8032D580),
                     ("gGlobalTimer", 0x8032D5D4),
                     ("gGfxSPTask", 0x8033B068),
                     ("gDisplayListHead", 0x8033B06C),
                     ("gGfxPool", 0x8033B074),
                     ("gControllerBits", 0x8033B078)
                 })
        {
            output.WriteLine($"{name}=0x{machine.Memory.ReadUInt32(address):X8}");
        }

        foreach (var (name, address) in new (string Name, uint Address)[]
                 {
                     ("gIntrMesgQueue", 0x8033AE08),
                     ("gSPTaskMesgQueue", 0x8033AE20),
                     ("gDmaMesgQueue", 0x8033AF60),
                     ("gSIEventMesgQueue", 0x8033AF78),
                     ("gGameVblankQueue", 0x8033B010),
                     ("gGfxVblankQueue", 0x8033B028)
                 })
        {
            output.WriteLine(
                $"{name}: mt=0x{machine.Memory.ReadUInt32(address):X8} " +
                $"full=0x{machine.Memory.ReadUInt32(address + 4):X8} " +
                $"valid={machine.Memory.ReadUInt32(address + 8)} " +
                $"first={machine.Memory.ReadUInt32(address + 12)} " +
                $"count={machine.Memory.ReadUInt32(address + 16)} " +
                $"msg=0x{machine.Memory.ReadUInt32(address + 20):X8}");
        }

        foreach (var (name, thread) in new (string Name, uint Address)[]
                 {
                     ("main", 0x8033A8E0),
                     ("game", 0x8033AA90),
                     ("sound", 0x8033AC40)
                 })
        {
            output.WriteLine(
                $"{name}: next=0x{machine.Memory.ReadUInt32(thread):X8} " +
                $"pri={machine.Memory.ReadUInt32(thread + 4)} " +
                $"queue=0x{machine.Memory.ReadUInt32(thread + 8):X8} " +
                $"tlnext=0x{machine.Memory.ReadUInt32(thread + 12):X8} " +
                $"state=0x{machine.Memory.ReadUInt32(thread + 0x10):X8} " +
                $"sp=0x{machine.Memory.ReadUInt64(thread + 0xF0):X16} " +
                $"ra=0x{machine.Memory.ReadUInt64(thread + 0x100):X16} " +
                $"status=0x{machine.Memory.ReadUInt32(thread + 0x118):X8} " +
                $"epc=0x{machine.Memory.ReadUInt32(thread + 0x11C):X8}");
        }

        foreach (var thread in new[] { 0x8033A8E0u, 0x8033AA90u, 0x8033AC40u })
        {
            var references = new List<uint>();
            for (var address = 0x80000000u; address < 0x80800000u; address += 4)
            {
                if (machine.Memory.ReadUInt32(address) == thread)
                {
                    references.Add(address);
                }
            }

            output.WriteLine(
                $"references to 0x{thread:X8}: " +
                string.Join(", ", references.Select(address => $"0x{address:X8}")));
        }

        foreach (var address in new[] { 0x80365D60u, 0x80365D88u, 0x803670B0u })
        {
            output.WriteLine(
                $"dynamic 0x{address:X8}: " +
                string.Join(
                    " ",
                    Enumerable.Range(0, 8)
                        .Select(index => $"0x{machine.Memory.ReadUInt32(address + ((uint)index * 4)):X8}")));
        }

        output.WriteLine(
            $"tasks gfx={machine.GraphicsTasksSubmitted} audio={machine.AudioTasksSubmitted} " +
            $"ai={machine.Memory.AudioDmasCompleted} last={machine.LastRspTask}");
        output.WriteLine(
            $"VI origin=0x{machine.Memory.ViOrigin:X8} width={machine.Memory.ViWidth} " +
            $"colors={machine.CurrentFrame.ToArray().Distinct().Count()} " +
            $"RDP commands={machine.Renderer.CommandsProcessed} " +
            $"lists={machine.Renderer.DisplayListsProcessed} " +
            $"fills={machine.Renderer.FillRectanglesDrawn} " +
            $"vertices={machine.Renderer.VerticesTransformed} " +
            $"triangles={machine.Renderer.TrianglesDrawn} " +
            $"unsupported={machine.Renderer.UnsupportedCommands} " +
            $"opcodes={string.Join(", ", machine.Renderer.UnsupportedCommandCounts.OrderBy(pair => pair.Key).Select(pair => $"0x{pair.Key:X2}:{pair.Value}"))}");
        output.WriteLine(
            $"PC=0x{machine.Cpu.ProgramCounter:X8} " +
            $"running=0x{machine.Memory.ReadUInt32(0x803359B0):X8} " +
            $"runq=0x{machine.Memory.ReadUInt32(0x803359A8):X8} " +
            $"CP0 count=0x{machine.Cpu.ReadCoprocessor0(9):X8} " +
            $"compare=0x{machine.Cpu.ReadCoprocessor0(11):X8} " +
            $"status=0x{machine.Cpu.ReadCoprocessor0(12):X8} " +
            $"cause=0x{machine.Cpu.ReadCoprocessor0(13):X8}");
        if (string.Equals(
                Environment.GetEnvironmentVariable("PIXEL64_DUMP_FRAME"),
                "1",
                StringComparison.Ordinal))
        {
            var framePath = Path.Combine(Path.GetTempPath(), "pixel64-sm64-frame.ppm");
            using var frame = File.Create(framePath);
            using var writer = new BinaryWriter(frame);
            writer.Write(System.Text.Encoding.ASCII.GetBytes(
                $"P6\n{machine.Width} {machine.Height}\n255\n"));
            foreach (var pixel in machine.CurrentFrame)
            {
                writer.Write((byte)(pixel >> 16));
                writer.Write((byte)(pixel >> 8));
                writer.Write((byte)pixel);
            }

            output.WriteLine($"frame={framePath}");
        }

        if (machine.LastGraphicsTask is { Type: 1 } task)
        {
            for (var offset = 0u; offset < task.DataSize; offset += 8)
            {
                output.WriteLine(
                    $"DL 0x{task.DataPointer + offset:X8}: " +
                    $"0x{machine.Memory.ReadUInt32(task.DataPointer + offset):X8} " +
                    $"0x{machine.Memory.ReadUInt32(task.DataPointer + offset + 4):X8}");
            }
        }
    }

    private static string? FindLocalSuperMario64()
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
                .Where(path => Path.GetExtension(path) is ".z64" or ".n64" or ".v64")
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
                })
            : null;
    }

    private static byte[] CreateCartridgeImage()
    {
        var image = new byte[0x2000];
        image[0] = 0x80;
        image[1] = 0x37;
        image[2] = 0x12;
        image[3] = 0x40;
        WriteUInt32(image, 0x08, 0x80000400);
        "PIXEL64 TEST        "u8.CopyTo(image.AsSpan(0x20, 20));
        image[0x3B] = (byte)'N';
        image[0x3C] = (byte)'P';
        image[0x3D] = (byte)'X';
        image[0x3E] = (byte)'E';
        return image;
    }

    private static byte[] ConvertByteOrder(byte[] canonical, N64ImageByteOrder byteOrder)
    {
        var converted = canonical.ToArray();
        if (byteOrder == N64ImageByteOrder.ByteSwapped)
        {
            for (var offset = 0; offset < converted.Length; offset += 2)
            {
                (converted[offset], converted[offset + 1]) = (converted[offset + 1], converted[offset]);
            }
        }
        else if (byteOrder == N64ImageByteOrder.LittleEndian)
        {
            for (var offset = 0; offset < converted.Length; offset += 4)
            {
                (converted[offset], converted[offset + 3]) = (converted[offset + 3], converted[offset]);
                (converted[offset + 1], converted[offset + 2]) = (converted[offset + 2], converted[offset + 1]);
            }
        }

        return converted;
    }

    private static void WriteUInt32(byte[] destination, int offset, uint value)
    {
        destination[offset] = (byte)(value >> 24);
        destination[offset + 1] = (byte)(value >> 16);
        destination[offset + 2] = (byte)(value >> 8);
        destination[offset + 3] = (byte)value;
    }
}
