namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Gekko's paired singles: the one part of this CPU that is not a stock
/// PowerPC 750.
/// </summary>
/// <remarks>
/// <para>
/// Every floating point register has a second slot, and a set of instructions
/// operate on both at once. Alongside them is a quantised load/store unit that
/// converts to and from 8- and 16-bit integers on the way to memory, scaled by
/// one of eight graphics quantisation registers. It is how a GameCube moves
/// vertices about, which is why the boot sequence reaches one the moment it
/// starts doing real work.
/// </para>
/// <para>
/// The quantised forms are the interesting half. A single instruction names a
/// GQR, and that register decides both the element type and a power-of-two
/// scale — so the same <c>psq_l</c> reads two floats, two bytes, or two scaled
/// shorts depending on state set somewhere else entirely.
/// </para>
/// </remarks>
public sealed partial class GekkoCpu
{
    /// <summary>The first graphics quantisation register, GQR0.</summary>
    private const int SprGqr0 = 912;

    private readonly ulong[] _fpr1 = new ulong[GeneralRegisterCount];

    /// <summary>The second slot of each floating point register.</summary>
    public Span<ulong> Fpr1 => _fpr1;

    public double GetPairedSingle(int register) =>
        BitConverter.UInt64BitsToDouble(_fpr1[register]);

    public void SetPairedSingle(int register, double value) =>
        _fpr1[register] = BitConverter.DoubleToUInt64Bits(value);

    /// <summary>Sets both slots, as a scalar single-precision load does.</summary>
    private void SetBothSlots(int register, double value)
    {
        SetFloat(register, value);
        SetPairedSingle(register, value);
    }

    /// <summary>
    /// The quantised loads and stores with an immediate displacement:
    /// primary opcodes 56, 57, 60 and 61.
    /// </summary>
    private bool ExecuteQuantisedMemory(uint instruction, uint primary, int d, int a)
    {
        var isUpdate = primary is 57 or 61;
        if (isUpdate && a == 0)
        {
            return false;
        }

        // A signed twelve-bit displacement, not the usual sixteen.
        var displacement = (int)(instruction & 0xFFF);
        if ((displacement & 0x800) != 0)
        {
            displacement -= 0x1000;
        }

        var single = (instruction & 0x8000) != 0;
        var register = (int)((instruction >> 12) & 7);
        var address = (a == 0 ? 0u : _gpr[a]) + (uint)displacement;

        if (primary is 56 or 57)
        {
            LoadQuantised(d, address, register, single);
        }
        else
        {
            StoreQuantised(d, address, register, single);
        }

        if (isUpdate)
        {
            _gpr[a] = address;
        }

        return true;
    }

    /// <summary>
    /// Primary opcode 4: the paired arithmetic, and the indexed forms of the
    /// quantised load and store.
    /// </summary>
    private bool ExecutePairedSingle(uint instruction, int d, int a, int b, bool rc)
    {
        var c = (int)((instruction >> 6) & 0x1F);

        // The indexed quantised forms are identified by six bits, because the
        // GQR index and the single-value flag occupy the rest.
        switch ((instruction >> 1) & 0x3F)
        {
            case 6 or 7 or 38 or 39: // psq_lx, psq_lux, psq_stx, psq_stux
            {
                var isUpdate = ((instruction >> 1) & 0x3F) is 7 or 39;
                if (isUpdate && a == 0)
                {
                    return false;
                }

                var single = (instruction & 0x400) != 0;
                var register = (int)((instruction >> 7) & 7);
                var address = (a == 0 ? 0u : _gpr[a]) + _gpr[b];

                if (((instruction >> 1) & 0x3F) is 6 or 7)
                {
                    LoadQuantised(d, address, register, single);
                }
                else
                {
                    StoreQuantised(d, address, register, single);
                }

                if (isUpdate)
                {
                    _gpr[a] = address;
                }

                return true;
            }
        }

        // The multiply-add shaped operations, identified by five bits.
        switch ((instruction >> 1) & 0x1F)
        {
            case 10: // ps_sum0
                return WritePair(d, GetFloat(a) + GetPairedSingle(b), GetPairedSingle(c), rc);
            case 11: // ps_sum1
                return WritePair(d, GetFloat(c), GetFloat(a) + GetPairedSingle(b), rc);
            case 12: // ps_muls0
                return WritePair(d, GetFloat(a) * GetFloat(c), GetPairedSingle(a) * GetFloat(c), rc);
            case 13: // ps_muls1
                return WritePair(
                    d,
                    GetFloat(a) * GetPairedSingle(c),
                    GetPairedSingle(a) * GetPairedSingle(c),
                    rc);
            case 14: // ps_madds0
                return WritePair(
                    d,
                    (GetFloat(a) * GetFloat(c)) + GetFloat(b),
                    (GetPairedSingle(a) * GetFloat(c)) + GetPairedSingle(b),
                    rc);
            case 15: // ps_madds1
                return WritePair(
                    d,
                    (GetFloat(a) * GetPairedSingle(c)) + GetFloat(b),
                    (GetPairedSingle(a) * GetPairedSingle(c)) + GetPairedSingle(b),
                    rc);
            case 18: // ps_div
                return WritePair(
                    d,
                    GetFloat(a) / GetFloat(b),
                    GetPairedSingle(a) / GetPairedSingle(b),
                    rc);
            case 20: // ps_sub
                return WritePair(
                    d,
                    GetFloat(a) - GetFloat(b),
                    GetPairedSingle(a) - GetPairedSingle(b),
                    rc);
            case 21: // ps_add
                return WritePair(
                    d,
                    GetFloat(a) + GetFloat(b),
                    GetPairedSingle(a) + GetPairedSingle(b),
                    rc);
            case 23: // ps_sel
                return WritePair(
                    d,
                    GetFloat(a) >= 0.0 ? GetFloat(c) : GetFloat(b),
                    GetPairedSingle(a) >= 0.0 ? GetPairedSingle(c) : GetPairedSingle(b),
                    rc);
            case 24: // ps_res
                return WritePair(d, 1.0 / GetFloat(b), 1.0 / GetPairedSingle(b), rc);
            case 25: // ps_mul
                return WritePair(
                    d,
                    GetFloat(a) * GetFloat(c),
                    GetPairedSingle(a) * GetPairedSingle(c),
                    rc);
            case 26: // ps_rsqrte
                return WritePair(
                    d,
                    1.0 / Math.Sqrt(GetFloat(b)),
                    1.0 / Math.Sqrt(GetPairedSingle(b)),
                    rc);
            case 28: // ps_msub
                return WritePair(
                    d,
                    (GetFloat(a) * GetFloat(c)) - GetFloat(b),
                    (GetPairedSingle(a) * GetPairedSingle(c)) - GetPairedSingle(b),
                    rc);
            case 29: // ps_madd
                return WritePair(
                    d,
                    (GetFloat(a) * GetFloat(c)) + GetFloat(b),
                    (GetPairedSingle(a) * GetPairedSingle(c)) + GetPairedSingle(b),
                    rc);
            case 30: // ps_nmsub
                return WritePair(
                    d,
                    -((GetFloat(a) * GetFloat(c)) - GetFloat(b)),
                    -((GetPairedSingle(a) * GetPairedSingle(c)) - GetPairedSingle(b)),
                    rc);
            case 31: // ps_nmadd
                return WritePair(
                    d,
                    -((GetFloat(a) * GetFloat(c)) + GetFloat(b)),
                    -((GetPairedSingle(a) * GetPairedSingle(c)) + GetPairedSingle(b)),
                    rc);
        }

        // Everything else is identified by ten bits.
        switch ((instruction >> 1) & 0x3FF)
        {
            case 0: // ps_cmpu0
                CompareFloat((int)((instruction >> 23) & 7), GetFloat(a), GetFloat(b));
                return true;
            case 32: // ps_cmpo0
                CompareFloat((int)((instruction >> 23) & 7), GetFloat(a), GetFloat(b));
                return true;
            case 64: // ps_cmpu1
            case 96: // ps_cmpo1
                CompareFloat(
                    (int)((instruction >> 23) & 7),
                    GetPairedSingle(a),
                    GetPairedSingle(b));
                return true;

            case 40: // ps_neg
                return WritePairBits(
                    d,
                    _fpr[b] ^ 0x8000_0000_0000_0000,
                    _fpr1[b] ^ 0x8000_0000_0000_0000,
                    rc);
            case 72: // ps_mr
                return WritePairBits(d, _fpr[b], _fpr1[b], rc);
            case 136: // ps_nabs
                return WritePairBits(
                    d,
                    _fpr[b] | 0x8000_0000_0000_0000,
                    _fpr1[b] | 0x8000_0000_0000_0000,
                    rc);
            case 264: // ps_abs
                return WritePairBits(
                    d,
                    _fpr[b] & 0x7FFF_FFFF_FFFF_FFFF,
                    _fpr1[b] & 0x7FFF_FFFF_FFFF_FFFF,
                    rc);

            // The four ways of interleaving two registers' slots.
            case 528: return WritePairBits(d, _fpr[a], _fpr[b], rc);   // ps_merge00
            case 560: return WritePairBits(d, _fpr[a], _fpr1[b], rc);  // ps_merge01
            case 592: return WritePairBits(d, _fpr1[a], _fpr[b], rc);  // ps_merge10
            case 624: return WritePairBits(d, _fpr1[a], _fpr1[b], rc); // ps_merge11

            case 1014: // dcbz_l — zeroes a locked-cache line
            {
                var line = (a == 0 ? _gpr[b] : _gpr[a] + _gpr[b]) & ~31u;
                for (var offset = 0u; offset < 32; offset += 4)
                {
                    _memory.WriteUInt32(line + offset, 0);
                }

                return true;
            }

            default:
                return false;
        }
    }

    private bool WritePair(int register, double first, double second, bool rc)
    {
        // Both slots are single precision, whatever the arithmetic was done in.
        SetFloat(register, (float)first);
        SetPairedSingle(register, (float)second);
        if (rc)
        {
            UpdateCr1();
        }

        return true;
    }

    private bool WritePairBits(int register, ulong first, ulong second, bool rc)
    {
        _fpr[register] = first;
        _fpr1[register] = second;
        if (rc)
        {
            UpdateCr1();
        }

        return true;
    }

    private void LoadQuantised(int destination, uint address, int quantisation, bool single)
    {
        var control = ReadGqr(quantisation);
        var type = (int)(control >> 16) & 7;
        var scale = SignedScale((int)(control >> 24) & 0x3F);
        var size = QuantisedElementSize(type);

        SetFloat(destination, Dequantise(address, type, scale));

        // A single-value load leaves 1.0 in the second slot, which is what
        // makes the pair usable as a scalar in the paired arithmetic.
        SetPairedSingle(
            destination,
            single ? 1.0 : Dequantise(address + (uint)size, type, scale));
    }

    private void StoreQuantised(int source, uint address, int quantisation, bool single)
    {
        var control = ReadGqr(quantisation);
        var type = (int)control & 7;
        var scale = SignedScale((int)(control >> 8) & 0x3F);
        var size = QuantisedElementSize(type);

        Quantise(address, GetFloat(source), type, scale);
        if (!single)
        {
            Quantise(address + (uint)size, GetPairedSingle(source), type, scale);
        }
    }

    private uint ReadGqr(int index) => _spr[SprGqr0 + (index & 7)];

    /// <summary>The scale field is six bits, two's complement.</summary>
    private static int SignedScale(int scale) => scale > 31 ? scale - 64 : scale;

    private static int QuantisedElementSize(int type) => type switch
    {
        4 or 6 => 1,
        5 or 7 => 2,
        _ => 4
    };

    private double Dequantise(uint address, int type, int scale) => type switch
    {
        4 => Math.ScaleB(_memory.ReadByte(address), -scale),
        5 => Math.ScaleB(_memory.ReadUInt16(address), -scale),
        6 => Math.ScaleB((sbyte)_memory.ReadByte(address), -scale),
        7 => Math.ScaleB((short)_memory.ReadUInt16(address), -scale),
        _ => BitConverter.UInt32BitsToSingle(_memory.ReadUInt32(address))
    };

    private void Quantise(uint address, double value, int type, int scale)
    {
        if (type is not (4 or 5 or 6 or 7))
        {
            _memory.WriteUInt32(address, BitConverter.SingleToUInt32Bits((float)value));
            return;
        }

        // Scaled, then clamped to the destination's range rather than allowed
        // to wrap. Hardware saturates here, and a wrapped coordinate is the
        // kind of wrong that looks like a rendering bug much later.
        var scaled = Math.ScaleB(value, scale);
        switch (type)
        {
            case 4:
                _memory.WriteByte(address, (byte)Math.Clamp(scaled, byte.MinValue, byte.MaxValue));
                break;
            case 5:
                _memory.WriteUInt16(
                    address,
                    (ushort)Math.Clamp(scaled, ushort.MinValue, ushort.MaxValue));
                break;
            case 6:
                _memory.WriteByte(
                    address,
                    (byte)(sbyte)Math.Clamp(scaled, sbyte.MinValue, sbyte.MaxValue));
                break;
            default:
                _memory.WriteUInt16(
                    address,
                    (ushort)(short)Math.Clamp(scaled, short.MinValue, short.MaxValue));
                break;
        }
    }
}
