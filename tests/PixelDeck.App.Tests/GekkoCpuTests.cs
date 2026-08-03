using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public sealed class GekkoCpuTests
{
    private const uint CodeBase = 0x8000_3000;
    private const uint DataBase = 0x8000_4000;

    /// <summary>
    /// <c>twi</c>: a conditional trap. A real instruction PixelCube does not
    /// implement, chosen over an invalid encoding so the reported name stays
    /// meaningful — and replaced whenever it becomes implemented, because a
    /// test that asserts something is missing has to name something that is.
    /// </summary>
    private const uint UnimplementedInstruction = 3u << 26;

    [Fact]
    public void Addi_LoadsAnImmediateWhenTheSourceIsRegisterZero()
    {
        using var machine = new CpuFixture();

        machine.Execute(DForm(14, 3, 0, -1));

        Assert.Equal(0xFFFF_FFFFu, machine.Cpu.Gpr[3]);
    }

    [Fact]
    public void Add_WithRecordBitSetsConditionRegisterZero()
    {
        using var machine = new CpuFixture();
        machine.Cpu.Gpr[4] = 5;
        machine.Cpu.Gpr[5] = unchecked((uint)-9);

        machine.Execute(XForm(31, 3, 4, 5, 266, rc: true));

        Assert.Equal(unchecked((uint)-4), machine.Cpu.Gpr[3]);

        // A negative result sets the LT bit, the most significant of CR0.
        Assert.Equal(0x8000_0000u, machine.Cpu.Cr);
    }

    [Fact]
    public void AddcThenAdde_CarriesBetweenInstructions()
    {
        using var machine = new CpuFixture();
        machine.Cpu.Gpr[4] = 0xFFFF_FFFF;
        machine.Cpu.Gpr[5] = 1;
        machine.Cpu.Gpr[6] = 0;
        machine.Cpu.Gpr[7] = 0;

        machine.Execute(XForm(31, 3, 4, 5, 10));   // addc r3, r4, r5
        Assert.Equal(0u, machine.Cpu.Gpr[3]);

        machine.Execute(XForm(31, 8, 6, 7, 138));  // adde r8, r6, r7
        Assert.Equal(1u, machine.Cpu.Gpr[8]);
    }

    [Fact]
    public void Subf_SubtractsTheFirstOperandFromTheSecond()
    {
        using var machine = new CpuFixture();
        machine.Cpu.Gpr[4] = 30;
        machine.Cpu.Gpr[5] = 100;

        machine.Execute(XForm(31, 3, 4, 5, 40));

        Assert.Equal(70u, machine.Cpu.Gpr[3]);
    }

    [Fact]
    public void Rlwinm_BuildsTheMaskFromItsBeginAndEndBits()
    {
        using var machine = new CpuFixture();
        machine.Cpu.Gpr[4] = 0x1234_5678;

        // slwi r3, r4, 8 — rotate left 8, keep bits 0 through 23.
        machine.Execute(MForm(21, 4, 3, 8, 0, 23));

        Assert.Equal(0x3456_7800u, machine.Cpu.Gpr[3]);
    }

    [Fact]
    public void Rlwinm_HandlesAWrappedMask()
    {
        using var machine = new CpuFixture();
        machine.Cpu.Gpr[4] = 0xFFFF_FFFF;

        // A mask whose end precedes its begin selects the complement of the
        // middle, which is the encoding clrlslwi and friends rely on.
        machine.Execute(MForm(21, 4, 3, 0, 28, 3));

        Assert.Equal(0xF000_000Fu, machine.Cpu.Gpr[3]);
    }

    [Fact]
    public void ConditionalBranch_IsTakenOnlyWhenTheConditionHolds()
    {
        using var machine = new CpuFixture();
        machine.Cpu.Gpr[4] = 7;

        machine.Execute(DForm(11, 0, 4, 7));               // cmpwi cr0, r4, 7
        machine.Cpu.Pc = CodeBase;
        machine.Write(CodeBase, BForm(16, 12, 2, 0x40));   // beq +0x40
        machine.Step();

        Assert.Equal(CodeBase + 0x40, machine.Cpu.Pc);

        machine.Cpu.Gpr[4] = 8;
        machine.Cpu.Pc = CodeBase;
        machine.Write(CodeBase, DForm(11, 0, 4, 7));
        machine.Step();
        machine.Write(machine.Cpu.Pc, BForm(16, 12, 2, 0x40));
        var fallthrough = machine.Cpu.Pc + 4;
        machine.Step();

        Assert.Equal(fallthrough, machine.Cpu.Pc);
    }

    [Fact]
    public void BranchAndLink_ThenBranchToLinkRegister_Returns()
    {
        using var machine = new CpuFixture();
        machine.Write(CodeBase, IForm(18, 0x100, lk: true));   // bl +0x100
        machine.Cpu.Pc = CodeBase;

        machine.Step();

        Assert.Equal(CodeBase + 0x100, machine.Cpu.Pc);
        Assert.Equal(CodeBase + 4, machine.Cpu.Lr);

        machine.Write(machine.Cpu.Pc, XlForm(19, 20, 0, 16));  // blr
        machine.Step();

        Assert.Equal(CodeBase + 4, machine.Cpu.Pc);
    }

    [Fact]
    public void Bdnz_DecrementsTheCountRegisterAndLoopsUntilItReachesZero()
    {
        using var machine = new CpuFixture();
        machine.Cpu.Ctr = 3;
        machine.Write(CodeBase, BForm(16, 16, 0, 0)); // bdnz .
        machine.Cpu.Pc = CodeBase;

        machine.Step();
        Assert.Equal(2u, machine.Cpu.Ctr);
        Assert.Equal(CodeBase, machine.Cpu.Pc);

        machine.Step();
        machine.Step();

        Assert.Equal(0u, machine.Cpu.Ctr);
        Assert.Equal(CodeBase + 4, machine.Cpu.Pc);
    }

    [Fact]
    public void StoreThenLoad_RoundTripsThroughMainMemory()
    {
        using var machine = new CpuFixture();
        machine.Cpu.Gpr[4] = DataBase;
        machine.Cpu.Gpr[5] = 0xDEAD_BEEF;

        machine.Execute(DForm(36, 5, 4, 0x10));  // stw r5, 0x10(r4)
        machine.Execute(DForm(32, 6, 4, 0x10));  // lwz r6, 0x10(r4)

        Assert.Equal(0xDEAD_BEEFu, machine.Cpu.Gpr[6]);
        Assert.Equal(0xDEAD_BEEFu, machine.Memory.ReadUInt32(DataBase + 0x10));
    }

    [Fact]
    public void StoreWithUpdate_AdvancesTheAddressRegister()
    {
        using var machine = new CpuFixture();
        machine.Cpu.Gpr[1] = DataBase;
        machine.Cpu.Gpr[0] = 0x1111_2222;

        machine.Execute(DForm(37, 0, 1, -8)); // stwu r0, -8(r1)

        Assert.Equal(DataBase - 8, machine.Cpu.Gpr[1]);
        Assert.Equal(0x1111_2222u, machine.Memory.ReadUInt32(DataBase - 8));
    }

    [Fact]
    public void HalfwordLoads_SignExtendOnlyWhenTheOpcodeSaysTo()
    {
        using var machine = new CpuFixture();
        machine.Cpu.Gpr[4] = DataBase;
        machine.Memory.WriteUInt16(DataBase, 0xFFFE);

        machine.Execute(DForm(40, 5, 4, 0)); // lhz
        machine.Execute(DForm(42, 6, 4, 0)); // lha

        Assert.Equal(0x0000_FFFEu, machine.Cpu.Gpr[5]);
        Assert.Equal(0xFFFF_FFFEu, machine.Cpu.Gpr[6]);
    }

    [Fact]
    public void SpecialRegisterMoves_ReachTheLinkAndCountRegisters()
    {
        using var machine = new CpuFixture();
        machine.Cpu.Gpr[3] = 0x8000_1234;

        machine.Execute(SprForm(31, 3, 8, 467));   // mtlr r3
        Assert.Equal(0x8000_1234u, machine.Cpu.Lr);

        machine.Cpu.Ctr = 0x4242_4242;
        machine.Execute(SprForm(31, 7, 9, 339));   // mfctr r7
        Assert.Equal(0x4242_4242u, machine.Cpu.Gpr[7]);
    }

    [Fact]
    public void Mtcrf_WritesOnlyTheSelectedConditionFields()
    {
        using var machine = new CpuFixture();
        machine.Cpu.Cr = 0x0000_0000;
        machine.Cpu.Gpr[3] = 0xFFFF_FFFF;

        // 0x80 selects CR0 alone.
        machine.Execute((31u << 26) | (3u << 21) | (0x80u << 12) | (144u << 1));

        Assert.Equal(0xF000_0000u, machine.Cpu.Cr);
    }

    [Fact]
    public void Srawi_SetsCarryOnlyWhenANegativeValueLosesSetBits()
    {
        using var machine = new CpuFixture();

        machine.Cpu.Gpr[4] = 0xFFFF_FFF0;
        machine.Execute(XForm(31, 4, 3, 4, 824));
        Assert.Equal(0xFFFF_FFFFu, machine.Cpu.Gpr[3]);
        Assert.Equal(0u, machine.Cpu.Xer & 0x2000_0000);

        machine.Cpu.Gpr[4] = 0xFFFF_FFF1;
        machine.Execute(XForm(31, 4, 3, 4, 824));
        Assert.Equal(0x2000_0000u, machine.Cpu.Xer & 0x2000_0000);
    }

    [Fact]
    public void DivideByZero_LeavesZeroRatherThanThrowing()
    {
        using var machine = new CpuFixture();
        machine.Cpu.Gpr[4] = 100;
        machine.Cpu.Gpr[5] = 0;

        machine.Execute(XForm(31, 3, 4, 5, 491));

        Assert.Equal(0u, machine.Cpu.Gpr[3]);
    }

    [Fact]
    public void AnUnimplementedInstruction_StopsTheRunAndNamesItself()
    {
        using var machine = new CpuFixture();

        // Primary opcode 56: psq_l, the quantised paired-single load. Paired
        // singles are the next thing PixelCube cannot do, and nothing in a
        // boot sequence has reached one yet.
        machine.Write(CodeBase, UnimplementedInstruction);
        machine.Cpu.Pc = CodeBase;

        var result = machine.Cpu.Run(16);

        Assert.Equal(GekkoOutcome.Unimplemented, result.Outcome);
        Assert.Equal(0, result.InstructionsExecuted);
        Assert.Equal(CodeBase, machine.Cpu.Pc);
        Assert.Contains(
            machine.Trace.CaptureCounters(),
            counter => counter.Key == "gekko/opcode/3/0");
    }

    [Fact]
    public void SurveyMode_SkipsWhatItCannotRunSoOneRunListsEverything()
    {
        using var machine = new CpuFixture();
        machine.Cpu.UnimplementedPolicy = GekkoUnimplementedPolicy.Survey;

        machine.Write(CodeBase, UnimplementedInstruction);
        machine.Write(CodeBase + 4, DForm(14, 3, 0, 42));       // li r3, 42
        machine.Write(CodeBase + 8, UnimplementedInstruction);
        machine.Cpu.Pc = CodeBase;

        var result = machine.Cpu.Run(3);

        Assert.Equal(GekkoOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.InstructionsExecuted);
        Assert.Equal(42u, machine.Cpu.Gpr[3]);
        Assert.Equal(2, machine.Trace.CaptureCounters()
            .Single(counter => counter.Key == "gekko/opcode/3/0").Count);
    }

    [Fact]
    public void AFetchOutsideMemory_StopsTheRunRatherThanExecutingZeroes()
    {
        using var machine = new CpuFixture();
        machine.Cpu.Pc = GameCubeMemory.HardwareRegisterBase;

        var result = machine.Cpu.Run(4);

        Assert.Equal(GekkoOutcome.FetchFault, result.Outcome);
        Assert.Equal(0, result.InstructionsExecuted);
    }

    [Fact]
    public void ABranchThroughAZeroedRegister_StopsAtTheBranchNotAtAddressZero()
    {
        using var machine = new CpuFixture();

        // A call, then a branch through CTR while CTR is still zero. Address
        // zero is mapped, so without the guard this would run whatever bytes
        // live there and report an address the game never meant to reach.
        var call = IForm(18, 8, lk: true);
        machine.Write(CodeBase, call);
        machine.Write(CodeBase + 8, XlForm(19, 20, 0, 528)); // bctr
        machine.Cpu.Pc = CodeBase;

        var result = machine.Cpu.Run(8);

        Assert.Equal(GekkoOutcome.WildBranch, result.Outcome);
        Assert.Equal(1, result.InstructionsExecuted);
        Assert.Equal(CodeBase + 8, machine.Cpu.Pc);
        Assert.Equal(0u, machine.Cpu.LastBranchTarget);

        // The path that led there is what turns the report into a diagnosis.
        var transfers = machine.Cpu.CaptureRecentTransfers();
        Assert.Equal((CodeBase, CodeBase + 8, call), transfers[0]);
        Assert.Equal(CodeBase + 8, transfers[^1].From);
        Assert.Equal(0u, transfers[^1].To);
    }

    [Fact]
    public void PollingAnUnimplementedRegister_ProducesOneLineAndACount()
    {
        // The shape of the real finding: Super Mario Sunshine polls one
        // register hundreds of thousands of times waiting for a handshake. The
        // pixel engine has nothing behind it, so it stands in for whichever
        // register is unmodelled next.
        using var machine = new CpuFixture();

        // Repeats are counted only for records the log would have kept, so an
        // Information-level report needs the level to reach it.
        machine.Trace.Level = GameCubeTraceLevel.Information;
        machine.Cpu.Gpr[4] = GameCubeMemory.HardwareRegisterBase + 0x1000;

        for (var index = 0; index < 500; index++)
        {
            machine.Execute(DForm(40, 5, 4, 0)); // lhz r5, 0(r4)
        }

        var counter = Assert.Single(
            machine.Trace.CaptureCounters(),
            entry => entry.Key.StartsWith("register/read/PE", StringComparison.Ordinal));
        Assert.Equal(500, counter.Count);
        Assert.Single(
            machine.Trace.CaptureRecent(),
            record => record.Channel == GameCubeTraceChannel.Unimplemented);
    }

    // ------------------------------------------------------------- encoding

    private static uint DForm(uint opcode, uint d, uint a, int immediate) =>
        (opcode << 26) | (d << 21) | (a << 16) | (uint)(ushort)(short)immediate;

    private static uint XForm(
        uint opcode,
        uint d,
        uint a,
        uint b,
        uint extended,
        bool rc = false) =>
        (opcode << 26) | (d << 21) | (a << 16) | (b << 11) | (extended << 1) | (rc ? 1u : 0);

    private static uint MForm(uint opcode, uint s, uint a, uint shift, uint begin, uint end) =>
        (opcode << 26) | (s << 21) | (a << 16) | (shift << 11) | (begin << 6) | (end << 1);

    private static uint IForm(uint opcode, int offset, bool lk = false) =>
        (opcode << 26) | ((uint)offset & 0x03FF_FFFC) | (lk ? 1u : 0);

    private static uint BForm(uint opcode, uint bo, uint bi, int offset, bool lk = false) =>
        (opcode << 26) | (bo << 21) | (bi << 16) | ((uint)offset & 0xFFFC) | (lk ? 1u : 0);

    private static uint XlForm(uint opcode, uint bo, uint bi, uint extended, bool lk = false) =>
        (opcode << 26) | (bo << 21) | (bi << 16) | (extended << 1) | (lk ? 1u : 0);

    private static uint SprForm(uint opcode, uint d, uint spr, uint extended)
    {
        var field = ((spr & 0x1F) << 5) | ((spr >> 5) & 0x1F);
        return (opcode << 26) | (d << 21) | (field << 11) | (extended << 1);
    }

    /// <summary>A CPU over blank memory, with everything traced.</summary>
    private sealed class CpuFixture : IDisposable
    {
        public CpuFixture()
        {
            Trace = new GameCubeTraceLog(
                new GameCubeTraceSettings(GameCubeTraceLevel.Warning, GameCubeTraceChannel.All));
            Memory = new GameCubeMemory(Trace);

            // Floating point available, matching a running machine. Paired
            // singles and the quantised load/store unit are floating point
            // instructions too, so without this they trap rather than execute.
            Cpu = new GekkoCpu(Memory, Trace) { Pc = CodeBase, Msr = 0x2000 };
        }

        public GameCubeTraceLog Trace { get; }

        public GameCubeMemory Memory { get; }

        public GekkoCpu Cpu { get; }

        public void Write(uint address, uint instruction) =>
            Memory.WriteUInt32(address, instruction);

        /// <summary>Places one instruction at the code base and runs it.</summary>
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
