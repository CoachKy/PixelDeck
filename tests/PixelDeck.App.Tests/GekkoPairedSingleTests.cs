using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public sealed class GekkoPairedSingleTests
{
    private const uint CodeBase = 0x8000_3000;
    private const uint DataBase = 0x8000_4000;
    private const int Gqr0 = 912;

    [Fact]
    public void QuantisedLoadAndStore_RoundTripAPairOfFloats()
    {
        using var fixture = new CpuFixture();
        fixture.Cpu.Gpr[4] = DataBase;
        fixture.Cpu.Spr[Gqr0] = 0; // type 0 both ways: plain single precision
        fixture.Memory.WriteUInt32(DataBase, BitConverter.SingleToUInt32Bits(1.5f));
        fixture.Memory.WriteUInt32(DataBase + 4, BitConverter.SingleToUInt32Bits(-2.25f));

        fixture.Execute(QuantisedForm(56, 1, 4, 0, single: false, quantisation: 0));

        Assert.Equal(1.5, fixture.Cpu.GetFloat(1));
        Assert.Equal(-2.25, fixture.Cpu.GetPairedSingle(1));

        fixture.Execute(QuantisedForm(60, 1, 4, 0x20, single: false, quantisation: 0));

        Assert.Equal(1.5f, BitConverter.UInt32BitsToSingle(fixture.Memory.ReadUInt32(DataBase + 0x20)));
        Assert.Equal(-2.25f, BitConverter.UInt32BitsToSingle(fixture.Memory.ReadUInt32(DataBase + 0x24)));
    }

    [Fact]
    public void ASingleValueLoadLeavesOneInTheSecondSlot()
    {
        // What makes a quantised pair usable as a scalar by the paired
        // arithmetic: multiplying by the second slot is then a no-op.
        using var fixture = new CpuFixture();
        fixture.Cpu.Gpr[4] = DataBase;
        fixture.Cpu.Spr[Gqr0] = 0;
        fixture.Memory.WriteUInt32(DataBase, BitConverter.SingleToUInt32Bits(7.5f));
        fixture.Cpu.SetPairedSingle(1, 1234.0);

        fixture.Execute(QuantisedForm(56, 1, 4, 0, single: true, quantisation: 0));

        Assert.Equal(7.5, fixture.Cpu.GetFloat(1));
        Assert.Equal(1.0, fixture.Cpu.GetPairedSingle(1));
    }

    [Fact]
    public void TheQuantisationRegisterDecidesBothTypeAndScale()
    {
        using var fixture = new CpuFixture();
        fixture.Cpu.Gpr[4] = DataBase;

        // Load type 7 (signed 16-bit) with a scale of 8, so a stored 256
        // arrives as 1.0. Store type 7 with the same scale sends it back.
        fixture.Cpu.Spr[Gqr0] = (7u << 16) | (8u << 24) | 7u | (8u << 8);
        fixture.Memory.WriteUInt16(DataBase, unchecked((ushort)(short)256));
        fixture.Memory.WriteUInt16(DataBase + 2, unchecked((ushort)(short)-512));

        fixture.Execute(QuantisedForm(56, 2, 4, 0, single: false, quantisation: 0));

        Assert.Equal(1.0, fixture.Cpu.GetFloat(2));
        Assert.Equal(-2.0, fixture.Cpu.GetPairedSingle(2));

        fixture.Execute(QuantisedForm(60, 2, 4, 0x10, single: false, quantisation: 0));

        Assert.Equal(256, (short)fixture.Memory.ReadUInt16(DataBase + 0x10));
        Assert.Equal(-512, (short)fixture.Memory.ReadUInt16(DataBase + 0x12));
    }

    [Fact]
    public void AQuantisedStoreSaturatesRatherThanWrapping()
    {
        // Hardware clamps. A wrapped coordinate is the kind of wrong that
        // surfaces as a rendering fault far from its cause.
        using var fixture = new CpuFixture();
        fixture.Cpu.Gpr[4] = DataBase;
        fixture.Cpu.Spr[Gqr0] = 4u; // store as unsigned 8-bit, scale 0
        fixture.Cpu.SetFloat(3, 5000.0);
        fixture.Cpu.SetPairedSingle(3, -5000.0);

        fixture.Execute(QuantisedForm(60, 3, 4, 0, single: false, quantisation: 0));

        Assert.Equal(255, fixture.Memory.ReadByte(DataBase));
        Assert.Equal(0, fixture.Memory.ReadByte(DataBase + 1));
    }

    [Fact]
    public void PairedArithmeticOperatesOnBothSlots()
    {
        using var fixture = new CpuFixture();
        fixture.Cpu.SetFloat(1, 3.0);
        fixture.Cpu.SetPairedSingle(1, 4.0);
        fixture.Cpu.SetFloat(2, 10.0);
        fixture.Cpu.SetPairedSingle(2, 20.0);

        fixture.Execute(AForm(4, 3, 1, 2, 0, 21)); // ps_add f3, f1, f2

        Assert.Equal(13.0, fixture.Cpu.GetFloat(3));
        Assert.Equal(24.0, fixture.Cpu.GetPairedSingle(3));
    }

    [Fact]
    public void PairedMultiplyUsesTheThirdOperandSlot()
    {
        using var fixture = new CpuFixture();
        fixture.Cpu.SetFloat(1, 2.0);
        fixture.Cpu.SetPairedSingle(1, 3.0);
        fixture.Cpu.SetFloat(4, 5.0);
        fixture.Cpu.SetPairedSingle(4, 7.0);

        fixture.Execute(AForm(4, 5, 1, 0, 4, 25)); // ps_mul f5, f1, f4

        Assert.Equal(10.0, fixture.Cpu.GetFloat(5));
        Assert.Equal(21.0, fixture.Cpu.GetPairedSingle(5));
    }

    [Theory]
    [InlineData(528u, 1.0, 3.0)]  // ps_merge00: a.ps0, b.ps0
    [InlineData(560u, 1.0, 4.0)]  // ps_merge01: a.ps0, b.ps1
    [InlineData(592u, 2.0, 3.0)]  // ps_merge10: a.ps1, b.ps0
    [InlineData(624u, 2.0, 4.0)]  // ps_merge11: a.ps1, b.ps1
    public void MergeInterleavesTheSlotsOfTwoRegisters(
        uint extended,
        double expectedFirst,
        double expectedSecond)
    {
        using var fixture = new CpuFixture();
        fixture.Cpu.SetFloat(1, 1.0);
        fixture.Cpu.SetPairedSingle(1, 2.0);
        fixture.Cpu.SetFloat(2, 3.0);
        fixture.Cpu.SetPairedSingle(2, 4.0);

        fixture.Execute((4u << 26) | (5u << 21) | (1u << 16) | (2u << 11) | (extended << 1));

        Assert.Equal(expectedFirst, fixture.Cpu.GetFloat(5));
        Assert.Equal(expectedSecond, fixture.Cpu.GetPairedSingle(5));
    }

    [Fact]
    public void ASinglePrecisionLoadFillsBothSlots()
    {
        using var fixture = new CpuFixture();
        fixture.Cpu.Gpr[4] = DataBase;
        fixture.Memory.WriteUInt32(DataBase, BitConverter.SingleToUInt32Bits(6.5f));

        fixture.Execute((48u << 26) | (7u << 21) | (4u << 16)); // lfs f7, 0(r4)

        Assert.Equal(6.5, fixture.Cpu.GetFloat(7));
        Assert.Equal(6.5, fixture.Cpu.GetPairedSingle(7));
    }

    private static uint QuantisedForm(
        uint opcode,
        uint d,
        uint a,
        int displacement,
        bool single,
        uint quantisation) =>
        (opcode << 26) | (d << 21) | (a << 16) | (single ? 0x8000u : 0) |
        ((quantisation & 7) << 12) | ((uint)displacement & 0xFFF);

    private static uint AForm(uint opcode, uint d, uint a, uint b, uint c, uint extended) =>
        (opcode << 26) | (d << 21) | (a << 16) | (b << 11) | (c << 6) | (extended << 1);

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

        public void Execute(uint instruction)
        {
            Memory.WriteUInt32(CodeBase, instruction);
            Cpu.Pc = CodeBase;
            Assert.Equal(GekkoOutcome.Completed, Cpu.Step());
        }

        public void Dispose() => Trace.Dispose();
    }
}
