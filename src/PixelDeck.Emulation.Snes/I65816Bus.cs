namespace PixelDeck.Emulation.Snes;

/// <summary>
/// Everything a 65C816 core needs from the system around it. The console's
/// main CPU runs against <see cref="SnesBus"/>; the SA-1 coprocessor is a
/// second 65C816 running against its own bus, so the core is written against
/// this interface rather than either concrete type.
/// </summary>
internal interface I65816Bus
{
    /// <summary>True while an interrupt request is asserted and not yet
    /// acknowledged. The core additionally honours the I flag.</summary>
    bool IrqPending { get; }

    /// <summary>Takes a pending non-maskable interrupt, if one is latched.</summary>
    bool ConsumeNmi();

    byte CpuRead(uint address);

    void CpuWrite(uint address, byte value);

    /// <summary>Marks the start of an instruction so the bus can accumulate
    /// the access-speed dependent cycle cost of each of its memory accesses.</summary>
    void BeginCpuInstructionTiming();

    /// <summary>Returns the cycle cost accumulated since
    /// <see cref="BeginCpuInstructionTiming"/>, never below the supplied
    /// minimum for the instruction.</summary>
    CpuInstructionTiming EndCpuInstructionTiming(int minimumCpuCycles);
}
