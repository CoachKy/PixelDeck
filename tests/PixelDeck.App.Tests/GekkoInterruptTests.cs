using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public sealed class GekkoInterruptTests
{
    private const uint CodeBase = 0x8000_3000;
    private const uint ExternalVector = 0x8000_0500;
    private const uint DecrementerVector = 0x8000_0900;
    private const uint InterruptCause = 0xCC00_3000;
    private const uint InterruptMask = 0xCC00_3004;
    private const uint DisplayInterrupt0 = 0xCC00_2030;

    /// <summary>A bare <c>rfi</c>, as the boot state installs at every vector.</summary>
    private const uint ReturnFromInterrupt = 0x4C00_0064;

    [Fact]
    public void AnExternalInterruptIsDeliveredOnlyWhenTheMachineStateAllowsIt()
    {
        using var fixture = new CpuFixture();
        fixture.Memory.WriteUInt32(InterruptMask, 0x0000_0100);
        fixture.RaiseVideoInterrupt();
        fixture.Write(CodeBase, 0x6000_0000); // nop

        // Interrupts disabled: the nop runs and nothing is taken.
        fixture.Cpu.Msr = 0;
        fixture.Step();
        Assert.Equal(CodeBase + 4, fixture.Cpu.Pc);

        fixture.Cpu.Pc = CodeBase;
        fixture.Cpu.Msr = GekkoCpu.MsrExternalInterruptEnable;
        fixture.Step();

        Assert.Equal(ExternalVector, fixture.Cpu.Pc);
        Assert.Equal(CodeBase, fixture.Cpu.Spr[26]);                       // SRR0
        Assert.Equal(GekkoCpu.MsrExternalInterruptEnable, fixture.Cpu.Spr[27]); // SRR1
        Assert.False(fixture.Cpu.AreInterruptsEnabled);
    }

    [Fact]
    public void AMaskedCauseDoesNotInterrupt()
    {
        // The mask is what the operating system uses to decide which devices
        // it is ready to hear from; a cause nobody asked for must stay quiet.
        using var fixture = new CpuFixture();
        fixture.Cpu.Msr = GekkoCpu.MsrExternalInterruptEnable;
        fixture.Memory.WriteUInt32(InterruptMask, 0x0000_0008); // anything but video
        fixture.RaiseVideoInterrupt();
        fixture.Write(CodeBase, 0x6000_0000);

        fixture.Step();

        Assert.Equal(CodeBase + 4, fixture.Cpu.Pc);
    }

    [Fact]
    public void ReturnFromInterrupt_RestoresBothTheAddressAndTheState()
    {
        using var fixture = new CpuFixture();
        fixture.Cpu.Msr = GekkoCpu.MsrExternalInterruptEnable;
        fixture.Memory.WriteUInt32(InterruptMask, 0x0000_0100);
        fixture.RaiseVideoInterrupt();
        fixture.Memory.WriteUInt32(ExternalVector, ReturnFromInterrupt);
        fixture.Write(CodeBase, 0x6000_0000);

        fixture.Step();                       // take the interrupt

        // Acknowledge the way a handler does, or the interrupt is still
        // pending when rfi re-enables and it is taken straight back.
        fixture.Memory.WriteUInt32(DisplayInterrupt0, 0);
        fixture.Memory.WriteUInt32(InterruptCause, 0x0000_0100);
        fixture.Step();                       // run the handler's rfi

        Assert.Equal(CodeBase, fixture.Cpu.Pc);
        Assert.True(fixture.Cpu.AreInterruptsEnabled);
    }

    [Fact]
    public void TheInterruptCauseRegisterIsClearedByWritingOnesOverIt()
    {
        // Only devices raise a cause. Software writing to this register is
        // acknowledging, not setting — so a handler that writes back what it
        // read clears exactly what it has dealt with.
        using var fixture = new CpuFixture();
        fixture.Memory.WriteUInt32(InterruptMask, 0xFFFF);
        fixture.RaiseVideoInterrupt();
        Assert.Equal(0x0000_0100u, fixture.Memory.ReadUInt32(InterruptCause));

        fixture.Memory.WriteUInt32(InterruptCause, 0x0000_0040); // a different device
        Assert.Equal(0x0000_0100u, fixture.Memory.ReadUInt32(InterruptCause));

        fixture.Memory.WriteUInt32(InterruptCause, 0x0000_0100);
        Assert.Equal(0u, fixture.Memory.ReadUInt32(InterruptCause));
    }

    [Fact]
    public void TheDecrementerInterruptsWhenItGoesNegativeRatherThanAtZero()
    {
        using var fixture = new CpuFixture();
        fixture.Cpu.Msr = GekkoCpu.MsrExternalInterruptEnable;
        fixture.Cpu.Decrementer = 2;
        fixture.Memory.WriteUInt32(DecrementerVector, ReturnFromInterrupt);

        // Nops until the decrementer has counted past zero. It ticks once per
        // twelve instructions, matching the bus clock against the core.
        for (var index = 0; index < 64; index++)
        {
            fixture.Write(fixture.Cpu.Pc, 0x6000_0000);
        }

        var taken = false;
        for (var index = 0; index < 64 && !taken; index++)
        {
            fixture.Write(fixture.Cpu.Pc, 0x6000_0000);
            fixture.Step();
            taken = fixture.Cpu.Pc == DecrementerVector;
        }

        Assert.True(taken, "the decrementer never interrupted");
        Assert.True((int)fixture.Cpu.Decrementer < 0);
    }

    [Fact]
    public void ADisplayInterruptFiresWhenTheBeamReachesItsLine()
    {
        // How a frame reaches the operating system: VIWaitForRetrace arms one
        // of these and sleeps, and this is the only thing that wakes it.
        using var fixture = new CpuFixture();
        const uint Line = 5;
        fixture.Memory.WriteUInt32(DisplayInterrupt0, 0x8000_0000 | (Line << 16));
        fixture.Memory.WriteUInt32(InterruptMask, 0x0000_0100);

        Assert.False(fixture.Memory.Hardware.IsInterruptPending);

        // Enough core cycles to sweep past the chosen line.
        fixture.Memory.Hardware.Advance(30_830 * (Line + 1));

        Assert.True(fixture.Memory.Hardware.IsInterruptPending);
        Assert.NotEqual(0u, fixture.Memory.ReadUInt32(DisplayInterrupt0) & (1u << 28));
    }

    [Fact]
    public void ADisarmedDisplayInterruptNeverFires()
    {
        using var fixture = new CpuFixture();
        fixture.Memory.WriteUInt32(DisplayInterrupt0, 5u << 16); // no enable bit
        fixture.Memory.WriteUInt32(InterruptMask, 0x0000_0100);

        fixture.Memory.Hardware.Advance(30_830 * 300);

        Assert.False(fixture.Memory.Hardware.IsInterruptPending);
    }

    [Fact]
    public void AcknowledgingEveryDisplayInterruptDropsTheVideoCause()
    {
        using var fixture = new CpuFixture();
        fixture.Memory.WriteUInt32(DisplayInterrupt0, 0x8000_0000 | (3u << 16));
        fixture.Memory.WriteUInt32(InterruptMask, 0x0000_0100);
        fixture.Memory.Hardware.Advance(30_830 * 4);
        Assert.True(fixture.Memory.Hardware.IsInterruptPending);

        // Software clears the fired flag by writing the register back without it.
        fixture.Memory.WriteUInt32(DisplayInterrupt0, 0x8000_0000 | (3u << 16));

        Assert.False(fixture.Memory.Hardware.IsInterruptPending);
    }

    private sealed class CpuFixture : IDisposable
    {
        public CpuFixture()
        {
            Trace = new GameCubeTraceLog(GameCubeTraceSettings.Disabled);
            Memory = new GameCubeMemory(Trace);
            Cpu = new GekkoCpu(Memory, Trace) { Pc = CodeBase };
        }

        public GameCubeTraceLog Trace { get; }

        public GameCubeMemory Memory { get; }

        public GekkoCpu Cpu { get; }

        public void Write(uint address, uint instruction) =>
            Memory.WriteUInt32(address, instruction);

        /// <summary>
        /// Raises the video interrupt the way hardware does — by arming a
        /// display interrupt and letting the beam reach it. Software cannot
        /// set a cause directly, so nothing else would be a fair test.
        /// </summary>
        public void RaiseVideoInterrupt()
        {
            // Line three rather than one: the beam starts on line one and
            // advances before it compares, so line one is only reached after a
            // whole field has gone by.
            Memory.WriteUInt32(DisplayInterrupt0, 0x8000_0000 | (3u << 16));
            Memory.Hardware.Advance(30_830 * 4);
        }

        public void Step() => Assert.Equal(GekkoOutcome.Completed, Cpu.Step());

        public void Dispose() => Trace.Dispose();
    }
}
