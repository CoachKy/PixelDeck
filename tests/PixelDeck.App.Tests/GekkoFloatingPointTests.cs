using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public sealed class GekkoFloatingPointTests
{
    private const uint CodeBase = 0x8000_3000;
    private const uint DataBase = 0x8000_4000;

    [Fact]
    public void SinglePrecisionLoadAndStore_RoundTripThroughTheDoubleRegisterFile()
    {
        using var fixture = new CpuFixture();
        fixture.Cpu.Gpr[4] = DataBase;
        fixture.Memory.WriteUInt32(DataBase, BitConverter.SingleToUInt32Bits(1.5f));

        fixture.Execute(DForm(48, 1, 4, 0));  // lfs f1, 0(r4)
        Assert.Equal(1.5, fixture.Cpu.GetFloat(1));

        fixture.Execute(DForm(52, 1, 4, 8));  // stfs f1, 8(r4)
        Assert.Equal(1.5f, BitConverter.UInt32BitsToSingle(fixture.Memory.ReadUInt32(DataBase + 8)));
    }

    [Fact]
    public void DoublePrecisionStore_PreservesTheExactBitPattern()
    {
        // The reason registers hold patterns rather than doubles: a payload
        // that survives a load and a store is the only way stfiwx and the
        // NaN-tagged results of fctiwz behave the way software expects.
        using var fixture = new CpuFixture();
        fixture.Cpu.Gpr[4] = DataBase;
        const ulong Pattern = 0x7FF8_1234_5678_9ABC;
        fixture.Memory.WriteUInt64(DataBase, Pattern);

        fixture.Execute(DForm(50, 2, 4, 0));  // lfd f2, 0(r4)
        fixture.Execute(DForm(54, 2, 4, 16)); // stfd f2, 16(r4)

        Assert.Equal(Pattern, fixture.Cpu.Fpr[2]);
        Assert.Equal(Pattern, fixture.Memory.ReadUInt64(DataBase + 16));
    }

    [Fact]
    public void SinglePrecisionArithmetic_RoundsThroughSingleWhileTheRegisterStaysDouble()
    {
        using var fixture = new CpuFixture();
        fixture.Cpu.SetFloat(1, 1.0);
        fixture.Cpu.SetFloat(2, 1.0 / 3.0);

        fixture.Execute(AForm(59, 3, 1, 2, 0, 21)); // fadds f3, f1, f2

        // Rounded to single and widened again, not computed in double.
        Assert.Equal((double)(float)(1.0 + (1.0 / 3.0)), fixture.Cpu.GetFloat(3));
    }

    [Fact]
    public void MultiplyAdd_UsesTheThirdOperandSlot()
    {
        using var fixture = new CpuFixture();
        fixture.Cpu.SetFloat(1, 3.0);  // a
        fixture.Cpu.SetFloat(2, 5.0);  // b
        fixture.Cpu.SetFloat(3, 7.0);  // c

        fixture.Execute(AForm(63, 4, 1, 2, 3, 29)); // fmadd f4, f1, f3, f2

        Assert.Equal((3.0 * 7.0) + 5.0, fixture.Cpu.GetFloat(4));
    }

    [Fact]
    public void SignManipulation_TouchesOnlyTheSignBit()
    {
        using var fixture = new CpuFixture();
        fixture.Cpu.SetFloat(1, -2.5);

        fixture.Execute(XForm(63, 2, 0, 1, 264)); // fabs f2, f1
        Assert.Equal(2.5, fixture.Cpu.GetFloat(2));

        fixture.Execute(XForm(63, 3, 0, 1, 40));  // fneg f3, f1
        Assert.Equal(2.5, fixture.Cpu.GetFloat(3));

        fixture.Execute(XForm(63, 4, 0, 1, 136)); // fnabs f4, f1
        Assert.Equal(-2.5, fixture.Cpu.GetFloat(4));

        fixture.Execute(XForm(63, 5, 0, 1, 72));  // fmr f5, f1
        Assert.Equal(-2.5, fixture.Cpu.GetFloat(5));
    }

    [Fact]
    public void Fcmpu_ReportsUnorderedForNotANumber()
    {
        using var fixture = new CpuFixture();
        fixture.Cpu.SetFloat(1, 1.0);
        fixture.Cpu.SetFloat(2, 2.0);

        fixture.Execute(XForm(63, 0, 1, 2, 0));
        Assert.Equal(0x8000_0000u, fixture.Cpu.Cr);   // less than

        fixture.Cpu.SetFloat(2, double.NaN);
        fixture.Execute(XForm(63, 0, 1, 2, 0));
        Assert.Equal(0x1000_0000u, fixture.Cpu.Cr);   // unordered
    }

    [Fact]
    public void Fsel_ChoosesOnTheSignOfItsFirstOperand()
    {
        using var fixture = new CpuFixture();
        fixture.Cpu.SetFloat(2, 100.0); // taken when a is negative
        fixture.Cpu.SetFloat(3, 200.0); // taken when a is zero or positive

        fixture.Cpu.SetFloat(1, 0.0);
        fixture.Execute(AForm(63, 4, 1, 2, 3, 23));
        Assert.Equal(200.0, fixture.Cpu.GetFloat(4));

        fixture.Cpu.SetFloat(1, -0.5);
        fixture.Execute(AForm(63, 4, 1, 2, 3, 23));
        Assert.Equal(100.0, fixture.Cpu.GetFloat(4));
    }

    [Fact]
    public void Mtfsb1_SetsTheBitCountedFromTheMostSignificantEnd()
    {
        // Bit 29 is non-IEEE mode, and it is the first floating point
        // instruction a GameCube title executes. Counting from the wrong end
        // sets bit 2 instead and nothing ever says so.
        using var fixture = new CpuFixture();

        fixture.Execute((63u << 26) | (29u << 21) | (38u << 1));

        Assert.Equal(GekkoCpu.FpscrNonIeeeMode, fixture.Cpu.Fpscr);

        fixture.Execute((63u << 26) | (29u << 21) | (70u << 1)); // mtfsb0
        Assert.Equal(0u, fixture.Cpu.Fpscr);
    }

    [Fact]
    public void Mffs_ReadsTheStatusRegisterIntoTheLowWord()
    {
        using var fixture = new CpuFixture();
        fixture.Cpu.Fpscr = 0x0000_1234;

        fixture.Execute(XForm(63, 5, 0, 0, 583));

        Assert.Equal(0x0000_1234u, (uint)fixture.Cpu.Fpr[5]);
    }

    [Fact]
    public void Fctiwz_TruncatesTowardsZeroAndStfiwxStoresTheLowWord()
    {
        using var fixture = new CpuFixture();
        fixture.Cpu.Gpr[4] = DataBase;
        fixture.Cpu.SetFloat(1, -3.75);

        fixture.Execute(XForm(63, 2, 0, 1, 15));            // fctiwz f2, f1
        fixture.Execute(XForm(31, 2, 0, 4, 983));           // stfiwx f2, 0, r4

        Assert.Equal(unchecked((uint)-3), fixture.Memory.ReadUInt32(DataBase));
    }

    [Fact]
    public void SystemCall_EntersTheHandlerAndTheDefaultHandlerReturns()
    {
        using var fixture = new CpuFixture();

        // The boot state installs a bare rfi at every vector, so sc costs two
        // instructions and lands back where it started.
        fixture.Memory.WriteUInt32(0x8000_0C00, 0x4C00_0064);
        fixture.Write(CodeBase, 17u << 26);
        fixture.Cpu.Pc = CodeBase;

        fixture.Step();
        Assert.Equal(0x8000_0C00u, fixture.Cpu.Pc);

        fixture.Step();
        Assert.Equal(CodeBase + 4, fixture.Cpu.Pc);
    }

    // ------------------------------------------------------------- encoding

    private static uint DForm(uint opcode, uint d, uint a, int immediate) =>
        (opcode << 26) | (d << 21) | (a << 16) | (uint)(ushort)(short)immediate;

    private static uint XForm(uint opcode, uint d, uint a, uint b, uint extended) =>
        (opcode << 26) | (d << 21) | (a << 16) | (b << 11) | (extended << 1);

    private static uint AForm(uint opcode, uint d, uint a, uint b, uint c, uint extended) =>
        (opcode << 26) | (d << 21) | (a << 16) | (b << 11) | (c << 6) | (extended << 1);

    private sealed class CpuFixture : IDisposable
    {
        public CpuFixture()
        {
            Trace = new GameCubeTraceLog(GameCubeTraceSettings.Disabled);
            Memory = new GameCubeMemory(Trace);

            // Floating point available, as a running machine has it. A Gekko
            // out of reset has the unit switched off and traps to the operating
            // system on first use, so a test that executes floating point
            // instructions against a bare processor is testing the exception
            // rather than the arithmetic.
            Cpu = new GekkoCpu(Memory, Trace) { Pc = CodeBase, Msr = 0x2000 };
        }

        public GameCubeTraceLog Trace { get; }

        public GameCubeMemory Memory { get; }

        public GekkoCpu Cpu { get; }

        public void Write(uint address, uint instruction) =>
            Memory.WriteUInt32(address, instruction);

        public void Execute(uint instruction)
        {
            Write(CodeBase, instruction);
            Cpu.Pc = CodeBase;
            Assert.Equal(GekkoOutcome.Completed, Cpu.Step());
        }

        public void Step() => Assert.Equal(GekkoOutcome.Completed, Cpu.Step());

        public void Dispose() => Trace.Dispose();
    }
}
