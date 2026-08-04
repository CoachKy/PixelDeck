using System.Buffers.Binary;
using PixelDeck.Emulation.N64;
using Xunit;

namespace PixelDeck.App.Tests;

/// <summary>
/// RSP vector unit tests.
/// </summary>
/// <remarks>
/// The previous version of this file encoded VADD at funct 0x08 and VAND at
/// funct 0x19 and passed, because <see cref="N64RspProcessor"/> used the same
/// invented opcode map. Every expectation here is stated from the hardware
/// encoding instead, so a regression to a self-consistent-but-wrong table
/// fails rather than passes.
/// </remarks>
public class N64RspVectorTests
{
    private const int VmulfFunct = 0x00;
    private const int VmacfFunct = 0x08;
    private const int VaddFunct = 0x10;
    private const int VandFunct = 0x28;

    private sealed record RspHarness(N64RspProcessor Rsp, N64Memory Memory)
    {
        public N64RspState State => Rsp.State;

        public byte[] SpDmem => Memory.SpDmem;
    }

    private static RspHarness CreateProcessor()
    {
        var cartridge = N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage());
        var memory = new N64Memory(cartridge);
        return new RspHarness(new N64RspProcessor(memory), memory);
    }

    /// <summary>
    /// COP2 vector format: opcode 0x12, bit 25 set, element in bits 24:21,
    /// vt 20:16, vs 15:11, vd 10:6, funct 5:0.
    /// </summary>
    private static uint EncodeVectorOp(int funct, int vd, int vs, int vt, int element) =>
        (0x12u << 26) | (1u << 25) | ((uint)element << 21) |
        ((uint)vt << 16) | ((uint)vs << 11) | ((uint)vd << 6) | (uint)funct;

    /// <summary>
    /// LWC2/SWC2 format: base 25:21, vt 20:16, sub-opcode 15:11,
    /// element 10:7, signed offset 6:0 scaled by the access size.
    /// </summary>
    private static uint EncodeVectorMemory(int opcode, int subOpcode, int vt, int element, int offset) =>
        ((uint)opcode << 26) | ((uint)vt << 16) | ((uint)subOpcode << 11) |
        ((uint)element << 7) | ((uint)offset & 0x7F);

    private static void Execute(RspHarness harness, uint instruction)
    {
        BinaryPrimitives.WriteUInt32BigEndian(harness.Memory.SpImem.AsSpan(0, 4), instruction);
        harness.State.Pc = 0;
        harness.State.Halted = false;
        harness.Rsp.StepInstruction();
    }

    [Fact]
    public void VaddUsesHardwareFunct0x10()
    {
        var rsp = CreateProcessor();
        rsp.State.SetVectorElement(1, 0, 100);
        rsp.State.SetVectorElement(2, 0, 50);

        Execute(rsp, EncodeVectorOp(VaddFunct, vd: 3, vs: 1, vt: 2, element: 0));

        Assert.Equal(150, rsp.State.GetVectorElement(3, 0));
    }

    [Fact]
    public void VandUsesHardwareFunct0x28()
    {
        var rsp = CreateProcessor();
        rsp.State.SetVectorElement(1, 0, 0xFF00);
        rsp.State.SetVectorElement(2, 0, 0x0F0F);

        Execute(rsp, EncodeVectorOp(VandFunct, vd: 3, vs: 1, vt: 2, element: 0));

        Assert.Equal(0x0F00, rsp.State.GetVectorElement(3, 0));
    }

    /// <summary>
    /// Element specifier 0 means "no specifier": each lane reads its own
    /// element. It previously broadcast element 0 to all eight lanes.
    /// </summary>
    [Fact]
    public void ElementSpecifierZeroIsLaneIdentity()
    {
        var rsp = CreateProcessor();
        for (var lane = 0; lane < 8; lane++)
        {
            rsp.State.SetVectorElement(1, lane, (ushort)(lane + 1));
            rsp.State.SetVectorElement(2, lane, (ushort)((lane + 1) * 10));
        }

        Execute(rsp, EncodeVectorOp(VaddFunct, vd: 3, vs: 1, vt: 2, element: 0));

        for (var lane = 0; lane < 8; lane++)
        {
            Assert.Equal((ushort)((lane + 1) * 11), rsp.State.GetVectorElement(3, lane));
        }
    }

    /// <summary>
    /// VMULF is a fractional multiply: 0.5 * 0.5 == 0.25 in Q0.15.
    /// </summary>
    [Fact]
    public void VmulfProducesFractionalProduct()
    {
        var rsp = CreateProcessor();
        rsp.State.SetVectorElement(1, 0, 0x4000);
        rsp.State.SetVectorElement(2, 0, 0x4000);

        Execute(rsp, EncodeVectorOp(VmulfFunct, vd: 3, vs: 1, vt: 2, element: 0));

        Assert.Equal(0x2000, rsp.State.GetVectorElement(3, 0));
    }

    /// <summary>
    /// The accumulator convention must be identical whether or not an element
    /// specifier is present. VMACF previously existed only on the no-specifier
    /// path, and the two paths stored the accumulator 16 bits apart, so a
    /// VMULF followed by a VMACF produced a result 65536x off -- or nothing.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void VmacfAccumulatesOntoVmulfRegardlessOfElementSpecifier(int element)
    {
        var rsp = CreateProcessor();
        for (var lane = 0; lane < 8; lane++)
        {
            rsp.State.SetVectorElement(1, lane, 0x4000);
            rsp.State.SetVectorElement(2, lane, 0x4000);
        }

        Execute(rsp, EncodeVectorOp(VmulfFunct, vd: 3, vs: 1, vt: 2, element: element));
        Assert.Equal(0x2000, rsp.State.GetVectorElement(3, 0));

        Execute(rsp, EncodeVectorOp(VmacfFunct, vd: 3, vs: 1, vt: 2, element: element));
        Assert.Equal(0x4000, rsp.State.GetVectorElement(3, 0));
    }

    /// <summary>
    /// The sub-opcode selects the transfer width. Treating every LWC2 as LQV
    /// meant a two-byte LSV overwrote the whole register.
    /// </summary>
    [Fact]
    public void LoadShortWritesOnlyTwoBytes()
    {
        var rsp = CreateProcessor();
        for (var lane = 0; lane < 8; lane++)
        {
            rsp.State.SetVectorElement(1, lane, 0xAAAA);
        }

        for (var index = 0; index < 16; index++)
        {
            rsp.SpDmem[0x40 + index] = (byte)(0x10 + index);
        }

        rsp.State.SetGpr(5, 0x40);

        // LSV $v1[0], 0($5) -- sub-opcode 0x01, offset scaled by 2.
        Execute(rsp, EncodeVectorMemory(0x32, subOpcode: 0x01, vt: 1, element: 0, offset: 0) | (5u << 21));

        Assert.Equal(0x1011, rsp.State.GetVectorElement(1, 0));
        for (var lane = 1; lane < 8; lane++)
        {
            Assert.Equal(0xAAAA, rsp.State.GetVectorElement(1, lane));
        }
    }

    [Fact]
    public void LoadQuadFillsTheWholeRegister()
    {
        var rsp = CreateProcessor();
        for (var index = 0; index < 16; index++)
        {
            rsp.SpDmem[0x40 + index] = (byte)(0x10 + index);
        }

        rsp.State.SetGpr(5, 0x40);

        // LQV $v1[0], 0($5) -- sub-opcode 0x04.
        Execute(rsp, EncodeVectorMemory(0x32, subOpcode: 0x04, vt: 1, element: 0, offset: 0) | (5u << 21));

        for (var lane = 0; lane < 8; lane++)
        {
            var expected = (ushort)((0x10 + (lane * 2)) << 8 | (0x11 + (lane * 2)));
            Assert.Equal(expected, rsp.State.GetVectorElement(1, lane));
        }
    }

    /// <summary>
    /// The 7-bit offset is scaled by the access size, not used raw.
    /// </summary>
    [Fact]
    public void VectorLoadOffsetIsScaledByAccessSize()
    {
        var rsp = CreateProcessor();
        for (var index = 0; index < 32; index++)
        {
            rsp.SpDmem[0x40 + index] = (byte)index;
        }

        rsp.State.SetGpr(5, 0x40);

        // LQV $v1[0], 1($5) -- offset 1 means one 16-byte quad, i.e. +16.
        Execute(rsp, EncodeVectorMemory(0x32, subOpcode: 0x04, vt: 1, element: 0, offset: 1) | (5u << 21));

        Assert.Equal((ushort)((16 << 8) | 17), rsp.State.GetVectorElement(1, 0));
    }

    [Fact]
    public void VectorStoreQuadRoundTripsThroughDmem()
    {
        var rsp = CreateProcessor();
        for (var lane = 0; lane < 8; lane++)
        {
            rsp.State.SetVectorElement(2, lane, (ushort)(0x1000 + lane));
        }

        rsp.State.SetGpr(5, 0x80);
        Execute(rsp, EncodeVectorMemory(0x3A, subOpcode: 0x04, vt: 2, element: 0, offset: 0) | (5u << 21));

        for (var lane = 0; lane < 8; lane++)
        {
            var stored = BinaryPrimitives.ReadUInt16BigEndian(
                rsp.SpDmem.AsSpan(0x80 + (lane * 2), 2));
            Assert.Equal((ushort)(0x1000 + lane), stored);
        }
    }

    [Fact]
    public void VectorStateAccumulatorsAndRegistersBehaveCorrectly()
    {
        var state = new N64RspState();
        state.SetAccumulator(0, 0x0001_0002_0003L);
        Assert.Equal(0x0001_0002_0003L, state.GetAccumulator(0));
        Assert.Equal(0x0001, state.AccHi[0]);
        Assert.Equal(0x0002, state.AccMid[0]);
        Assert.Equal(0x0003, state.AccLo[0]);

        state.SetVectorElement(1, 4, 0x1234);
        Assert.Equal(0x1234, state.GetVectorElement(1, 4));

        // Element specifier 8 is a scalar broadcast of element 0.
        state.SetVectorElement(2, 0, 0xAAAA);
        state.SetVectorElement(2, 1, 0xBBBB);
        Assert.Equal(0xAAAA, state.GetVectorElementBroadcast(2, lane: 5, elementSpecifier: 8));
    }

    [Fact]
    public void DpcDmaTriggerExecutesRdpCommandBufferInFast3dRenderer()
    {
        var cartridge = N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage());
        var memory = new N64Memory(cartridge);
        var renderer = new Fast3dRenderer(memory);
        var triggered = false;

        memory.OnDpcDmaTriggered = (start, end) =>
        {
            triggered = true;
            renderer.ExecuteRdpCommandBuffer(start, end);
        };

        memory.WriteUInt32(0x04100000, 0x1000);
        BinaryPrimitives.WriteUInt32BigEndian(memory.Rdram.AsSpan(0x1000, 4), 0xC0000000);
        BinaryPrimitives.WriteUInt32BigEndian(memory.Rdram.AsSpan(0x1004, 4), 0x00000000);
        memory.WriteUInt32(0x04100004, 0x1008);

        Assert.True(triggered);
    }
}
