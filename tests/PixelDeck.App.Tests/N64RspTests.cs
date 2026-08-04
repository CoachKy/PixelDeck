using System.Buffers.Binary;
using PixelDeck.Emulation.N64;
using Xunit;

namespace PixelDeck.App.Tests;

public sealed class N64RspTests
{
    [Fact]
    public void RspStateEnforcesZeroRegisterImmutability()
    {
        var state = new N64RspState();
        Assert.Equal(0u, state.GetGpr(0));

        state.SetGpr(0, 0xDEADBEEF);
        Assert.Equal(0u, state.GetGpr(0));

        state.SetGpr(1, 0x12345678);
        Assert.Equal(0x12345678u, state.GetGpr(1));
    }

    [Fact]
    public void RspStateHandlesVectorRegisterAccessAndElements()
    {
        var state = new N64RspState();
        state.SetVectorElement(5, 2, 0xABCD);
        Assert.Equal(0xABCD, state.GetVectorElement(5, 2));

        Span<ushort> target = stackalloc ushort[8];
        state.ReadVectorRegister(5, target);
        Assert.Equal(0xABCD, target[2]);

        ushort[] source = [10, 20, 30, 40, 50, 60, 70, 80];
        state.WriteVectorRegister(7, source);
        Assert.Equal(30, state.GetVectorElement(7, 2));
    }

    [Fact]
    public void RspStateSerializesAndDeserializesRoundTrip()
    {
        var state = new N64RspState();
        state.SetGpr(2, 0x11223344);
        state.SetVectorElement(3, 4, 0x5566);
        state.AccHi[0] = 0x7788;
        state.Pc = 0x0100;
        state.Halted = false;
        state.Broke = true;

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            state.SaveState(writer);
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var restored = new N64RspState();
        restored.LoadState(reader);

        Assert.Equal(0x11223344u, restored.GetGpr(2));
        Assert.Equal(0x5566, restored.GetVectorElement(3, 4));
        Assert.Equal(0x7788, restored.AccHi[0]);
        Assert.Equal(0x0100u, restored.Pc);
        Assert.False(restored.Halted);
        Assert.True(restored.Broke);
    }

    [Fact]
    public void RspProcessorExecutesScalarMipsInstructions()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        var processor = new N64RspProcessor(memory);

        // IMEM instructions:
        // 0x000: ORI $r1, $r0, 0x1234  => GPR[1] = 0x1234
        // 0x004: ADDIU $r2, $r1, 0x0010 => GPR[2] = 0x1244
        // 0x008: BREAK
        BinaryPrimitives.WriteUInt32BigEndian(memory.SpImem.AsSpan(0x000, 4), 0x34011234);
        BinaryPrimitives.WriteUInt32BigEndian(memory.SpImem.AsSpan(0x004, 4), 0x24220010);
        BinaryPrimitives.WriteUInt32BigEndian(memory.SpImem.AsSpan(0x008, 4), 0x0000000D);

        processor.State.Halted = false;
        processor.State.Pc = 0;

        while (!processor.State.Halted)
        {
            processor.Step();
        }

        Assert.Equal(0x1234u, processor.State.GetGpr(1));
        Assert.Equal(0x1244u, processor.State.GetGpr(2));
        Assert.True(processor.State.Broke);
        Assert.True(processor.State.Halted);
        Assert.Equal(3, processor.InstructionsExecuted);
    }

    [Fact]
    public void RspProcessorPerformsSpDmaTransferBetweenRdramAndSpMemory()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        var processor = new N64RspProcessor(memory);

        // Populate RDRAM at 0x1000 with known pattern
        memory.Rdram[0x1000] = 0xAA;
        memory.Rdram[0x1001] = 0xBB;
        memory.Rdram[0x1002] = 0xCC;
        memory.Rdram[0x1003] = 0xDD;

        // IMEM program to DMA 4 bytes from RDRAM 0x1000 to DMEM 0x0100
        // 0x000: LUI $r1, 0x0000
        // 0x004: ORI $r1, $r1, 0x0100 (SP_MEM_ADDR)
        // 0x008: MTC0 $r1, $0 (SP_MEM_ADDR)
        // 0x00C: LUI $r2, 0x0000
        // 0x010: ORI $r2, $r2, 0x1000 (SP_DRAM_ADDR)
        // 0x014: MTC0 $r2, $1 (SP_DRAM_ADDR)
        // 0x018: ADDIU $r3, $r0, 3 (Length - 1)
        // 0x01C: MTC0 $r3, $2 (SP_RD_LEN triggers DMA)
        // 0x020: BREAK
        BinaryPrimitives.WriteUInt32BigEndian(memory.SpImem.AsSpan(0x000, 4), 0x3C010000);
        BinaryPrimitives.WriteUInt32BigEndian(memory.SpImem.AsSpan(0x004, 4), 0x34210100);
        BinaryPrimitives.WriteUInt32BigEndian(memory.SpImem.AsSpan(0x008, 4), 0x40810000);
        BinaryPrimitives.WriteUInt32BigEndian(memory.SpImem.AsSpan(0x00C, 4), 0x3C020000);
        BinaryPrimitives.WriteUInt32BigEndian(memory.SpImem.AsSpan(0x010, 4), 0x34421000);
        BinaryPrimitives.WriteUInt32BigEndian(memory.SpImem.AsSpan(0x014, 4), 0x40820800);
        BinaryPrimitives.WriteUInt32BigEndian(memory.SpImem.AsSpan(0x018, 4), 0x24030003);
        BinaryPrimitives.WriteUInt32BigEndian(memory.SpImem.AsSpan(0x01C, 4), 0x40831000);
        BinaryPrimitives.WriteUInt32BigEndian(memory.SpImem.AsSpan(0x020, 4), 0x0000000D);

        processor.State.Halted = false;
        processor.State.Pc = 0;

        while (!processor.State.Halted)
        {
            processor.Step();
        }

        Assert.Equal(0xAA, memory.SpDmem[0x0100]);
        Assert.Equal(0xBB, memory.SpDmem[0x0101]);
        Assert.Equal(0xCC, memory.SpDmem[0x0102]);
        Assert.Equal(0xDD, memory.SpDmem[0x0103]);
    }

    [Fact]
    public void MachineExposesRspBackendSeam()
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));

        Assert.NotNull(machine.RspBackend);
        Assert.NotNull(machine.RspProcessor);
        Assert.Equal("Pixel64 Low-Level RSP Engine", machine.RspBackend.Name);
        Assert.True(machine.RspBackend.HleFallbackEnabled);
    }
}
