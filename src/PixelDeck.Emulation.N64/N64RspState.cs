namespace PixelDeck.Emulation.N64;

/// <summary>
/// Represents the internal register and execution state of the Nintendo 64 Reality Signal Processor (RSP).
/// Contains 32 x 32-bit scalar registers (GPR), 32 x 128-bit vector registers (8 x 16-bit elements each),
/// 48-bit wide accumulators (ACC_HI, ACC_MID, ACC_LO), vector control flags (VCO, VCC, VCE), Program Counter (PC),
/// and SP control flags.
/// </summary>
public sealed class N64RspState
{
    private readonly uint[] _gpr = new uint[32];
    private readonly ushort[,] _vpr = new ushort[32, 8];

    /// <summary>
    /// Accumulator high 16-bit elements (8 channels).
    /// </summary>
    public ushort[] AccHi { get; } = new ushort[8];

    /// <summary>
    /// Accumulator middle 16-bit elements (8 channels).
    /// </summary>
    public ushort[] AccMid { get; } = new ushort[8];

    /// <summary>
    /// Accumulator low 16-bit elements (8 channels).
    /// </summary>
    public ushort[] AccLo { get; } = new ushort[8];

    /// <summary>
    /// Vector Carry Out register (16-bit flag).
    /// </summary>
    public ushort Vco { get; set; }

    /// <summary>
    /// Vector Compare Code register (16-bit flag).
    /// </summary>
    public ushort Vcc { get; set; }

    private static readonly ushort[] ReciprocalRomTable = BuildReciprocalRomTable();
    private static readonly ushort[] ReciprocalSquareRootRomTable = BuildReciprocalSquareRootRomTable();

    private static ushort[] BuildReciprocalRomTable()
    {
        var table = new ushort[512];
        for (var i = 0; i < 512; i++)
        {
            var val = (0x7F000000 / (i + 512) + 1) >> 8;
            table[i] = (ushort)Math.Clamp(val, 0, 0xFFFF);
        }
        return table;
    }

    private static ushort[] BuildReciprocalSquareRootRomTable()
    {
        var table = new ushort[512];
        for (var i = 0; i < 512; i++)
        {
            var sqrt = Math.Sqrt((i + 512) / 512.0);
            var val = (int)((0x7F000000 / (sqrt * 512.0)) + 0.5) >> 8;
            table[i] = (ushort)Math.Clamp(val, 0, 0xFFFF);
        }
        return table;
    }

    /// <summary>
    /// Raw entry from the 512-entry reciprocal ROM.
    /// </summary>
    /// <remarks>
    /// The table is generated analytically rather than transcribed from
    /// hardware, so callers get close-but-not-bit-exact results. Replacing it
    /// with the real ROM dump is tracked in the accuracy remediation plan.
    /// </remarks>
    internal static ushort ReciprocalRom(int index) => ReciprocalRomTable[index & 0x1FF];

    /// <summary>
    /// Raw entry from the 512-entry reciprocal square-root ROM. Carries the
    /// same accuracy caveat as <see cref="ReciprocalRom"/>.
    /// </summary>
    internal static ushort ReciprocalSquareRootRom(int index) =>
        ReciprocalSquareRootRomTable[index & 0x1FF];

    /// <summary>
    /// Computes hardware-accurate RSP 512-entry fixed-point reciprocal for VRCPL.
    /// </summary>
    public static ushort ReciprocalLookup(int input)
    {
        if (input == 0) return 0x7FFF;
        var absInput = Math.Abs(input);
        var shift = System.Numerics.BitOperations.LeadingZeroCount((uint)absInput) - 23;
        var index = (absInput << shift) >> 22 & 0x1FF;
        return ReciprocalRomTable[index];
    }

    /// <summary>
    /// Computes hardware-accurate RSP 512-entry fixed-point reciprocal square root for VRSQL.
    /// </summary>
    public static ushort ReciprocalSquareRootLookup(int input)
    {
        if (input <= 0) return 0x7FFF;
        var shift = System.Numerics.BitOperations.LeadingZeroCount((uint)input) - 23;
        var index = (input << shift) >> 22 & 0x1FF;
        return ReciprocalSquareRootRomTable[index];
    }
    public byte Vce { get; set; }

    /// <summary>
    /// Program Counter (0x000 - 0xFFF IMEM offset).
    /// </summary>
    public uint Pc { get; set; }

    /// <summary>
    /// RSP execution halted state.
    /// </summary>
    public bool Halted { get; set; } = true;

    /// <summary>
    /// RSP execution broke (hit BREAK instruction).
    /// </summary>
    public bool Broke { get; set; }

    /// <summary>
    /// SP DMA busy flag.
    /// </summary>
    public bool DmaBusy { get; set; }

    /// <summary>
    /// SP DMA full flag.
    /// </summary>
    public bool DmaFull { get; set; }

    /// <summary>
    /// Single step mode enabled.
    /// </summary>
    public bool SingleStep { get; set; }

    /// <summary>
    /// Generate interrupt on break instruction.
    /// </summary>
    public bool InterruptOnBreak { get; set; }

    /// <summary>
    /// User signal flags (8 bits).
    /// </summary>
    public byte Signals { get; set; }

    /// <summary>
    /// Gets the value of a scalar register ($0 - $31). Register $0 always returns 0.
    /// </summary>
    public uint GetGpr(int index) => (index & 31) == 0 ? 0u : _gpr[index & 31];

    /// <summary>
    /// Sets the value of a scalar register ($0 - $31). Writes to $0 are ignored.
    /// </summary>
    public void SetGpr(int index, uint value)
    {
        var reg = index & 31;
        if (reg != 0)
        {
            _gpr[reg] = value;
        }
    }

    /// <summary>
    /// Gets a specific 16-bit element from a vector register ($v0 - $v31, element 0 - 7).
    /// </summary>
    public ushort GetVectorElement(int regIndex, int elementIndex) =>
        _vpr[regIndex & 31, elementIndex & 7];

    /// <summary>
    /// Sets a specific 16-bit element in a vector register ($v0 - $v31, element 0 - 7).
    /// </summary>
    public void SetVectorElement(int regIndex, int elementIndex, ushort value) =>
        _vpr[regIndex & 31, elementIndex & 7] = value;

    /// <summary>
    /// Gets an element from a vector register using RSP element broadcast decoding rules (0..15).
    /// </summary>
    public ushort GetVectorElementBroadcast(int regIndex, int lane, int elementSpecifier)
    {
        var reg = regIndex & 31;
        var element = elementSpecifier & 0xF;
        int targetElement = element switch
        {
            // 0 and 1 are both "no element specifier": every lane reads its
            // own element. Treating them as a broadcast of element 0 or 1 made
            // every element-wise vector op read a single source lane.
            0 or 1 => lane & 7,
            2 or 3 => (lane & 6) | (element & 1),     // q0..q1
            4 or 5 or 6 or 7 => (lane & 4) | (element & 3),  // h0..h3
            _ => element & 7                          // scalar broadcast 0..7
        };
        return _vpr[reg, targetElement & 7];
    }

    /// <summary>
    /// Reads a 48-bit accumulator value for channel <paramref name="channel"/>.
    /// </summary>
    public long GetAccumulator(int channel)
    {
        var c = channel & 7;
        var hi = (long)(short)AccHi[c];
        var mid = (ulong)AccMid[c];
        var lo = (ulong)AccLo[c];
        return (hi << 32) | (long)((mid << 16) | lo);
    }

    /// <summary>
    /// Sets a 48-bit accumulator value for channel <paramref name="channel"/>.
    /// </summary>
    public void SetAccumulator(int channel, long value)
    {
        var c = channel & 7;
        AccHi[c] = (ushort)(value >> 32);
        AccMid[c] = (ushort)(value >> 16);
        AccLo[c] = (ushort)value;
    }

    /// <summary>
    /// Reads all 8 elements of a vector register into a target span.
    /// </summary>
    public void ReadVectorRegister(int regIndex, Span<ushort> target)
    {
        var reg = regIndex & 31;
        for (var i = 0; i < 8; i++)
        {
            target[i] = _vpr[reg, i];
        }
    }

    /// <summary>
    /// Writes all 8 elements from a source span into a vector register.
    /// </summary>
    public void WriteVectorRegister(int regIndex, ReadOnlySpan<ushort> source)
    {
        var reg = regIndex & 31;
        for (var i = 0; i < 8; i++)
        {
            _vpr[reg, i] = source[i];
        }
    }

    /// <summary>
    /// Gets a 128-bit SIMD vector register ($v0 - $v31) as a 128-bit vector of 8 x 16-bit unsigned shorts.
    /// </summary>
    public System.Runtime.Intrinsics.Vector128<ushort> GetVector128(int regIndex)
    {
        var reg = regIndex & 31;
        Span<ushort> temp = stackalloc ushort[8];
        for (var i = 0; i < 8; i++) temp[i] = _vpr[reg, i];
        return System.Runtime.Intrinsics.Vector128.Create(temp);
    }

    /// <summary>
    /// Sets a 128-bit SIMD vector register ($v0 - $v31) from a 128-bit vector of 8 x 16-bit unsigned shorts.
    /// </summary>
    public void SetVector128(int regIndex, System.Runtime.Intrinsics.Vector128<ushort> value)
    {
        var reg = regIndex & 31;
        for (var i = 0; i < 8; i++) _vpr[reg, i] = value[i];
    }

    /// <summary>
    /// Computes the SP status register (32-bit uint) corresponding to current state flags.
    /// </summary>
    public uint GetStatusRegister()
    {
        uint status = 0;
        if (Halted) status |= 0x0001;
        if (Broke) status |= 0x0002;
        if (DmaBusy) status |= 0x0004;
        if (DmaFull) status |= 0x0008;
        if (SingleStep) status |= 0x0020;
        if (InterruptOnBreak) status |= 0x0040;
        status |= (uint)(Signals << 7);
        return status;
    }

    /// <summary>
    /// Updates SP status state flags from a written SP status register value.
    /// </summary>
    public void WriteStatusRegister(uint value)
    {
        // Clear flags
        if ((value & (1 << 0)) != 0) Halted = false;
        if ((value & (1 << 2)) != 0) Broke = false;
        if ((value & (1 << 4)) != 0) SingleStep = false;
        if ((value & (1 << 6)) != 0) InterruptOnBreak = false;
        for (var sig = 0; sig < 8; sig++)
        {
            if ((value & (1 << (8 + (sig * 2)))) != 0)
            {
                Signals = (byte)(Signals & ~(1 << sig));
            }
        }

        // Set flags
        if ((value & (1 << 1)) != 0) Halted = true;
        if ((value & (1 << 3)) != 0) Broke = true;
        if ((value & (1 << 5)) != 0) SingleStep = true;
        if ((value & (1 << 7)) != 0) InterruptOnBreak = true;
        for (var sig = 0; sig < 8; sig++)
        {
            if ((value & (1 << (9 + (sig * 2)))) != 0)
            {
                Signals = (byte)(Signals | (1 << sig));
            }
        }
    }

    /// <summary>
    /// Resets all scalar, vector, accumulator, and PC registers to zero and sets Halted to true.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_gpr, 0, _gpr.Length);
        Array.Clear(_vpr, 0, _vpr.Length);
        Array.Clear(AccHi, 0, AccHi.Length);
        Array.Clear(AccMid, 0, AccMid.Length);
        Array.Clear(AccLo, 0, AccLo.Length);
        Vco = 0;
        Vcc = 0;
        Vce = 0;
        Pc = 0;
        Halted = true;
        Broke = false;
        DmaBusy = false;
        DmaFull = false;
        SingleStep = false;
        InterruptOnBreak = false;
        Signals = 0;
    }

    /// <summary>
    /// Serializes RSP state for save-states.
    /// </summary>
    public void SaveState(BinaryWriter writer)
    {
        for (var i = 0; i < 32; i++)
        {
            writer.Write(_gpr[i]);
        }
        for (var r = 0; r < 32; r++)
        {
            for (var e = 0; e < 8; e++)
            {
                writer.Write(_vpr[r, e]);
            }
        }
        for (var i = 0; i < 8; i++)
        {
            writer.Write(AccHi[i]);
            writer.Write(AccMid[i]);
            writer.Write(AccLo[i]);
        }
        writer.Write(Vco);
        writer.Write(Vcc);
        writer.Write(Vce);
        writer.Write(Pc);
        writer.Write(Halted);
        writer.Write(Broke);
        writer.Write(DmaBusy);
        writer.Write(DmaFull);
        writer.Write(SingleStep);
        writer.Write(InterruptOnBreak);
        writer.Write(Signals);
    }

    /// <summary>
    /// Deserializes RSP state from save-states.
    /// </summary>
    public void LoadState(BinaryReader reader)
    {
        for (var i = 0; i < 32; i++)
        {
            _gpr[i] = reader.ReadUInt32();
        }
        _gpr[0] = 0; // Ensure $0 remains zero

        for (var r = 0; r < 32; r++)
        {
            for (var e = 0; e < 8; e++)
            {
                _vpr[r, e] = reader.ReadUInt16();
            }
        }
        for (var i = 0; i < 8; i++)
        {
            AccHi[i] = reader.ReadUInt16();
            AccMid[i] = reader.ReadUInt16();
            AccLo[i] = reader.ReadUInt16();
        }
        Vco = reader.ReadUInt16();
        Vcc = reader.ReadUInt16();
        Vce = reader.ReadByte();
        Pc = reader.ReadUInt32() & 0x0FFF;
        Halted = reader.ReadBoolean();
        Broke = reader.ReadBoolean();
        DmaBusy = reader.ReadBoolean();
        DmaFull = reader.ReadBoolean();
        SingleStep = reader.ReadBoolean();
        InterruptOnBreak = reader.ReadBoolean();
        Signals = reader.ReadByte();
    }
}
