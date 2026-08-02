namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Gekko's floating point unit: thirty-two double-precision registers, the
/// status and control register, and the arithmetic that runs on them.
/// </summary>
/// <remarks>
/// <para>
/// Registers hold raw 64-bit patterns rather than <see cref="double"/> values.
/// The two are not interchangeable: <c>stfd</c> of a value that arrived
/// through <c>lfs</c> must write back the exact bits, NaN payloads survive
/// moves, and <c>stfiwx</c> stores the low word of a pattern that is not a
/// number at all. Storing doubles would quietly launder all of that.
/// </para>
/// <para>
/// The single-precision forms compute in double and round the result through
/// <see cref="float"/>, which is what the hardware does — the register file is
/// double throughout, and only the rounding differs.
/// </para>
/// <para>
/// Paired singles are not here. They need the graphics quantisation registers
/// and a second slot per register, and nothing in a boot sequence has asked
/// for them yet; when something does, the trace will name it.
/// </para>
/// </remarks>
public sealed partial class GekkoCpu
{
    /// <summary>Non-IEEE mode: flush denormals. Every GameCube title sets it.</summary>
    public const uint FpscrNonIeeeMode = 1u << 2;

    /// <summary>Floating point enabled. Cleared until the game asks for it.</summary>
    private const uint MsrFloatingPointAvailable = 1u << 13;

    private readonly ulong[] _fpr = new ulong[GeneralRegisterCount];

    /// <summary>The floating point status and control register.</summary>
    public uint Fpscr { get; set; }

    public Span<ulong> Fpr => _fpr;

    public double GetFloat(int register) => BitConverter.UInt64BitsToDouble(_fpr[register]);

    public void SetFloat(int register, double value) =>
        _fpr[register] = BitConverter.DoubleToUInt64Bits(value);

    /// <summary>
    /// Primary opcodes 48 to 55: the floating point loads and stores with an
    /// immediate displacement.
    /// </summary>
    private bool ExecuteFloatMemory(uint primary, int d, int a, uint offset)
    {
        var isUpdate = (primary & 1) != 0;
        if (isUpdate && a == 0)
        {
            return false;
        }

        var address = a == 0 ? offset : _gpr[a] + offset;

        switch (primary)
        {
            case 48 or 49: // lfs, lfsu
                // A single-precision load fills both slots of the register,
                // which is what lets paired arithmetic treat it as a scalar.
                SetBothSlots(d, BitConverter.UInt32BitsToSingle(_memory.ReadUInt32(address)));
                break;
            case 50 or 51: // lfd, lfdu
                _fpr[d] = _memory.ReadUInt64(address);
                break;
            case 52 or 53: // stfs, stfsu
                _memory.WriteUInt32(address, BitConverter.SingleToUInt32Bits((float)GetFloat(d)));
                break;
            default: // stfd, stfdu
                _memory.WriteUInt64(address, _fpr[d]);
                break;
        }

        if (isUpdate)
        {
            _gpr[a] = address;
        }

        return true;
    }

    /// <summary>The indexed floating point loads and stores in opcode 31.</summary>
    private bool ExecuteFloatIndexed(uint extended, int d, int a, int b)
    {
        var isUpdate = extended is 567 or 631 or 695 or 759;
        if (isUpdate && a == 0)
        {
            return false;
        }

        var address = a == 0 ? _gpr[b] : _gpr[a] + _gpr[b];

        switch (extended)
        {
            case 535 or 567: // lfsx, lfsux
                SetBothSlots(d, BitConverter.UInt32BitsToSingle(_memory.ReadUInt32(address)));
                break;
            case 599 or 631: // lfdx, lfdux
                _fpr[d] = _memory.ReadUInt64(address);
                break;
            case 663 or 695: // stfsx, stfsux
                _memory.WriteUInt32(address, BitConverter.SingleToUInt32Bits((float)GetFloat(d)));
                break;
            case 727 or 759: // stfdx, stfdux
                _memory.WriteUInt64(address, _fpr[d]);
                break;
            case 983: // stfiwx — stores the low word of the pattern, unconverted
                _memory.WriteUInt32(address, (uint)_fpr[d]);
                break;
            default:
                return false;
        }

        if (isUpdate)
        {
            _gpr[a] = address;
        }

        return true;
    }

    /// <summary>
    /// Primary opcode 59: the single-precision arithmetic. Identical to opcode
    /// 63's arithmetic except that every result is rounded to single.
    /// </summary>
    private bool ExecuteSingleFloat(uint instruction, int d, int a, int b, bool rc)
    {
        var c = (int)((instruction >> 6) & 0x1F);
        double result;

        switch ((instruction >> 1) & 0x1F)
        {
            case 18: result = GetFloat(a) / GetFloat(b); break;                    // fdivs
            case 20: result = GetFloat(a) - GetFloat(b); break;                    // fsubs
            case 21: result = GetFloat(a) + GetFloat(b); break;                    // fadds
            case 24: result = 1.0 / GetFloat(b); break;                            // fres
            case 25: result = GetFloat(a) * GetFloat(c); break;                    // fmuls
            case 28: result = (GetFloat(a) * GetFloat(c)) - GetFloat(b); break;    // fmsubs
            case 29: result = (GetFloat(a) * GetFloat(c)) + GetFloat(b); break;    // fmadds
            case 30: result = -((GetFloat(a) * GetFloat(c)) - GetFloat(b)); break; // fnmsubs
            case 31: result = -((GetFloat(a) * GetFloat(c)) + GetFloat(b)); break; // fnmadds
            default: return false;
        }

        return WriteFloat(d, (float)result, rc);
    }

    /// <summary>
    /// Primary opcode 63: double-precision arithmetic, the moves and
    /// comparisons, and everything that touches the status register.
    /// </summary>
    private bool ExecuteDoubleFloat(uint instruction, int d, int a, int b, bool rc)
    {
        var c = (int)((instruction >> 6) & 0x1F);
        var shortExtended = (instruction >> 1) & 0x1F;

        // The multiply-add forms are encoded in five bits and have to be
        // recognised before the ten-bit table, which would otherwise alias them.
        switch (shortExtended)
        {
            case 18: return WriteFloat(d, GetFloat(a) / GetFloat(b), rc);
            case 20: return WriteFloat(d, GetFloat(a) - GetFloat(b), rc);
            case 21: return WriteFloat(d, GetFloat(a) + GetFloat(b), rc);
            case 25: return WriteFloat(d, GetFloat(a) * GetFloat(c), rc);
            case 26: return WriteFloat(d, 1.0 / Math.Sqrt(GetFloat(b)), rc); // frsqrte
            case 23: // fsel — the branchless conditional every SDK uses
                return WriteFloat(d, GetFloat(a) >= 0.0 ? GetFloat(c) : GetFloat(b), rc);
            case 28: return WriteFloat(d, (GetFloat(a) * GetFloat(c)) - GetFloat(b), rc);
            case 29: return WriteFloat(d, (GetFloat(a) * GetFloat(c)) + GetFloat(b), rc);
            case 30: return WriteFloat(d, -((GetFloat(a) * GetFloat(c)) - GetFloat(b)), rc);
            case 31: return WriteFloat(d, -((GetFloat(a) * GetFloat(c)) + GetFloat(b)), rc);
        }

        var extended = (instruction >> 1) & 0x3FF;
        switch (extended)
        {
            case 0: // fcmpu
            case 32: // fcmpo
                CompareFloat((int)((instruction >> 23) & 7), GetFloat(a), GetFloat(b));
                return true;

            case 12: // frsp — round to single, keep the double representation
                return WriteFloat(d, (float)GetFloat(b), rc);

            case 14: // fctiw
            case 15: // fctiwz
            {
                var value = GetFloat(b);
                var converted = extended == 15
                    ? (int)Math.Truncate(ClampToInt(value))
                    : (int)Math.Round(ClampToInt(value), MidpointRounding.ToEven);

                // The high word is architecturally undefined; hardware leaves
                // this pattern, and code that stores it with stfiwx only ever
                // reads the low word back.
                _fpr[d] = 0xFFF8_0000_0000_0000 | (uint)converted;
                if (rc)
                {
                    UpdateCr1();
                }

                return true;
            }

            case 40: return WriteFloatBits(d, _fpr[b] ^ 0x8000_0000_0000_0000, rc);  // fneg
            case 72: return WriteFloatBits(d, _fpr[b], rc);                          // fmr
            case 136: return WriteFloatBits(d, _fpr[b] | 0x8000_0000_0000_0000, rc); // fnabs
            case 264: return WriteFloatBits(d, _fpr[b] & 0x7FFF_FFFF_FFFF_FFFF, rc); // fabs

            case 38: // mtfsb1
                Fpscr |= FpscrBit(d);
                return true;

            case 70: // mtfsb0
                Fpscr &= ~FpscrBit(d);
                return true;

            case 64: // mcrfs
                SetCrField(
                    (int)((instruction >> 23) & 7),
                    (Fpscr >> (28 - ((int)((instruction >> 18) & 7) * 4))) & 0xF);
                return true;

            case 134: // mtfsfi
            {
                var field = (int)((instruction >> 23) & 7);
                var shift = 28 - (field * 4);
                Fpscr = (Fpscr & ~(0xFu << shift)) | (((instruction >> 12) & 0xF) << shift);
                return true;
            }

            case 583: // mffs
                _fpr[d] = 0xFFF8_0000_0000_0000 | Fpscr;
                return true;

            case 711: // mtfsf
            {
                var fields = (instruction >> 17) & 0xFF;
                var mask = 0u;
                for (var field = 0; field < 8; field++)
                {
                    if ((fields & (0x80 >> field)) != 0)
                    {
                        mask |= 0xFu << (28 - (field * 4));
                    }
                }

                Fpscr = (Fpscr & ~mask) | ((uint)_fpr[b] & mask);
                return true;
            }

            default:
                return false;
        }
    }

    private bool WriteFloat(int register, double value, bool rc)
    {
        SetFloat(register, value);
        if (rc)
        {
            UpdateCr1();
        }

        return true;
    }

    private bool WriteFloatBits(int register, ulong bits, bool rc)
    {
        _fpr[register] = bits;
        if (rc)
        {
            UpdateCr1();
        }

        return true;
    }

    private void CompareFloat(int field, double left, double right)
    {
        var flags =
            double.IsNaN(left) || double.IsNaN(right) ? 1u :
            left < right ? 8u :
            left > right ? 4u :
            2u;
        SetCrField(field, flags);
    }

    /// <summary>
    /// The record bit on a floating point instruction copies the exception
    /// summary into CR1 rather than comparing the result.
    /// </summary>
    private void UpdateCr1() => SetCrField(1, (Fpscr >> 28) & 0xF);

    /// <summary>
    /// Turns a PowerPC bit number, counted from the most significant end, into
    /// a mask. Getting this backwards is the classic way to set the wrong
    /// FPSCR bit and never notice.
    /// </summary>
    private static uint FpscrBit(int bit) => 1u << (31 - bit);

    /// <summary>
    /// Keeps a conversion inside the range a 32-bit integer can hold. Out of
    /// range results are architecturally undefined and saturate on hardware.
    /// </summary>
    private static double ClampToInt(double value) =>
        double.IsNaN(value) ? 0 : Math.Clamp(value, int.MinValue, int.MaxValue);
}
