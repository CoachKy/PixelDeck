using System.Buffers.Binary;
using System.Numerics;

namespace PixelDeck.Emulation.GameCube;

/// <summary>What stopped a run, or why a single step went no further.</summary>
public enum GekkoOutcome
{
    /// <summary>The requested instructions all executed.</summary>
    Completed,

    /// <summary>An instruction PixelCube does not implement yet.</summary>
    Unimplemented,

    /// <summary>Instruction fetch left mapped memory.</summary>
    FetchFault,

    /// <summary>
    /// A branch targeted an address no code can live at — almost always a
    /// register holding zero. Stopping here names the branch; carrying on
    /// would name whatever byte pattern failed to decode afterwards.
    /// </summary>
    WildBranch,

    /// <summary>Execution was stopped by <see cref="GekkoCpu.Halt"/>.</summary>
    Halted
}

/// <summary>
/// What to do when execution reaches an instruction that is not implemented.
/// </summary>
public enum GekkoUnimplementedPolicy
{
    /// <summary>
    /// Stop. The default, because an instruction's effect cannot be faked:
    /// carrying on past one means every later trace line describes a machine
    /// running on invented state.
    /// </summary>
    Stop,

    /// <summary>
    /// Treat it as a no-op and keep going. The state after this point is
    /// fiction and must not be trusted, but one run then produces the whole
    /// ranked list of what a stretch of code touches instead of only its first
    /// obstacle. Use it to plan, never to verify.
    /// </summary>
    Survey
}

/// <summary>The result of running the interpreter for a while.</summary>
public readonly record struct GekkoRunResult(
    GekkoOutcome Outcome,
    long InstructionsExecuted,
    uint Pc,
    uint Instruction)
{
    public bool IsClean => Outcome == GekkoOutcome.Completed;
}

/// <summary>
/// The GameCube's IBM Gekko: a PowerPC 750CX with paired-single floating point
/// and a locked L2 cache.
/// </summary>
/// <remarks>
/// <para>
/// This implements the integer, branch and load/store core — enough to carry a
/// retail <c>__start</c> a long way — and reports anything else through
/// <see cref="GameCubeTraceChannel.Unimplemented"/> rather than guessing.
/// Floating point, paired singles and the quantised load/store unit are not
/// here yet.
/// </para>
/// <para>
/// Address translation is not modelled. The BAT layout every retail title
/// installs maps both 0x8000_0000 and 0xC000_0000 onto physical zero, so
/// masking the top two bits gives the same answer as walking the BATs would,
/// and <c>mtspr</c> to a BAT register is recorded so that a title doing
/// something unusual shows up rather than failing quietly. This is a
/// deliberate shortcut with a stated condition, not an omission.
/// </para>
/// </remarks>
public sealed partial class GekkoCpu
{
    public const int GeneralRegisterCount = 32;
    public const int SpecialRegisterCount = 1024;

    private const uint XerSummaryOverflow = 0x8000_0000;
    private const uint XerOverflow = 0x4000_0000;
    private const uint XerCarry = 0x2000_0000;

    // Special purpose registers PixelCube names rather than numbers.
    private const int SprXer = 1;
    private const int SprLr = 8;
    private const int SprCtr = 9;
    private const int SprSrr0 = 26;
    private const int SprSrr1 = 27;
    private const int SprTbl = 268;
    private const int SprTbu = 269;
    private const int SprWriteTbl = 284;
    private const int SprWriteTbu = 285;
    private const int SprPvr = 287;

    /// <summary>The processor version Gekko reports. Retail 750CXe stepping.</summary>
    public const uint ProcessorVersion = 0x0008_3214;

    /// <summary>Where <c>sc</c> transfers control.</summary>
    private const uint SystemCallVector = 0x8000_0C00;

    private readonly GameCubeMemory _memory;
    private readonly GameCubeTraceLog _trace;
    private readonly uint[] _gpr = new uint[GeneralRegisterCount];
    private readonly uint[] _spr = new uint[SpecialRegisterCount];

    /// <summary>
    /// Cached keys for the repeated-occurrence tally. Building
    /// <c>"gekko/opcode/31/1010"</c> on every hit would reintroduce the
    /// per-instruction allocation that suppression exists to avoid.
    /// </summary>
    private readonly Dictionary<uint, string> _unimplementedKeys = [];

    private bool _halted;

    public GekkoCpu(GameCubeMemory memory, GameCubeTraceLog trace)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(trace);
        _memory = memory;
        _trace = trace;
        _spr[SprPvr] = ProcessorVersion;
    }

    public Span<uint> Gpr => _gpr;

    public Span<uint> Spr => _spr;

    public uint Pc { get; set; }

    public uint Lr
    {
        get => _spr[SprLr];
        set => _spr[SprLr] = value;
    }

    public uint Ctr
    {
        get => _spr[SprCtr];
        set => _spr[SprCtr] = value;
    }

    public uint Xer
    {
        get => _spr[SprXer];
        set => _spr[SprXer] = value;
    }

    /// <summary>The eight four-bit condition register fields, CR0 highest.</summary>
    public uint Cr { get; set; }

    public uint Msr { get; set; }

    /// <summary>The time base, incremented once per instruction for now.</summary>
    public ulong TimeBase { get; set; }

    public long InstructionsExecuted { get; private set; }

    public GekkoUnimplementedPolicy UnimplementedPolicy { get; set; } = GekkoUnimplementedPolicy.Stop;

    /// <summary>The instruction that stopped the last run, if one did.</summary>
    public uint LastInstruction { get; private set; }

    /// <summary>Stops a run in progress, and any run started afterwards.</summary>
    public void Halt() => _halted = true;

    public void Resume() => _halted = false;

    /// <summary>
    /// Runs up to <paramref name="maximumInstructions"/> instructions, stopping
    /// early on anything unimplemented or on a fetch outside memory.
    /// </summary>
    public GekkoRunResult Run(long maximumInstructions)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumInstructions);

        var executed = 0L;
        while (executed < maximumInstructions)
        {
            if (_halted)
            {
                return new GekkoRunResult(GekkoOutcome.Halted, executed, Pc, LastInstruction);
            }

            var outcome = Step();
            if (outcome == GekkoOutcome.Completed)
            {
                executed++;
                continue;
            }

            return new GekkoRunResult(outcome, executed, Pc, LastInstruction);
        }

        return new GekkoRunResult(GekkoOutcome.Completed, executed, Pc, LastInstruction);
    }

    /// <summary>Fetches, decodes and executes one instruction.</summary>
    public GekkoOutcome Step()
    {
        if (TryDeliverInterrupt())
        {
            return GekkoOutcome.Completed;
        }

        if (!_memory.TryReadInstruction(Pc, out var instruction))
        {
            _trace.Write(
                GameCubeTraceChannel.Cpu,
                GameCubeTraceLevel.Error,
                $"instruction fetch left mapped memory at 0x{Pc:X8} " +
                $"({GameCubeMemory.DescribeRegion(Pc)})");
            return GekkoOutcome.FetchFault;
        }

        LastInstruction = instruction;
        _memory.InstructionAddress = Pc;

        // Formatted only when the channel is open, so a disassembling trace
        // costs nothing at all when it is not wanted.
        _trace.Write(
            GameCubeTraceChannel.Cpu,
            GameCubeTraceLevel.Verbose,
            $"{Pc:X8}  {instruction:X8}  {GekkoDisassembler.Describe(instruction, Pc)}");

        var sequential = Pc + 4;
        var nextPc = sequential;
        var executed = Execute(instruction, ref nextPc);
        if (!executed)
        {
            return ReportUnimplemented(instruction);
        }

        if (nextPc != sequential)
        {
            RecordTransfer(Pc, nextPc, instruction);
            if (StopOnWildBranch && !IsPlausibleCodeAddress(nextPc))
            {
                // Pc is left at the branch, not at the target, so the reported
                // state is the state at the obstacle.
                return ReportWildBranch(instruction, nextPc);
            }
        }

        Pc = nextPc;
        AdvanceTime();
        InstructionsExecuted++;
        return GekkoOutcome.Completed;
    }

    /// <summary>
    /// Executes one instruction. Returns false when the opcode is not
    /// implemented, leaving every register untouched so the reported state is
    /// the state at the obstacle.
    /// </summary>
    private bool Execute(uint instruction, ref uint nextPc)
    {
        var primary = instruction >> 26;
        var d = (int)((instruction >> 21) & 0x1F);
        var a = (int)((instruction >> 16) & 0x1F);
        var b = (int)((instruction >> 11) & 0x1F);
        var simm = (uint)(short)(instruction & 0xFFFF);
        var uimm = instruction & 0xFFFF;
        var rc = (instruction & 1) != 0;

        switch (primary)
        {
            case 7: // mulli
                _gpr[d] = (uint)((int)_gpr[a] * (int)simm);
                return true;

            case 8: // subfic
                _gpr[d] = Add(~_gpr[a], simm, 1, setCarry: true, setOverflow: false);
                return true;

            case 10: // cmpli
                CompareUnsigned((int)((instruction >> 23) & 7), _gpr[a], uimm);
                return true;

            case 11: // cmpi
                CompareSigned((int)((instruction >> 23) & 7), (int)_gpr[a], (int)simm);
                return true;

            case 12: // addic
                _gpr[d] = Add(_gpr[a], simm, 0, setCarry: true, setOverflow: false);
                return true;

            case 13: // addic.
                _gpr[d] = Add(_gpr[a], simm, 0, setCarry: true, setOverflow: false);
                UpdateCr0(_gpr[d]);
                return true;

            case 14: // addi  (li when rA is r0)
                _gpr[d] = a == 0 ? simm : _gpr[a] + simm;
                return true;

            case 15: // addis (lis when rA is r0)
                _gpr[d] = (a == 0 ? 0u : _gpr[a]) + (uimm << 16);
                return true;

            case 16: // bc
                ExecuteConditionalBranch(instruction, ref nextPc);
                return true;

            case 18: // b
            {
                var target = SignExtend26(instruction & 0x03FF_FFFC);
                var absolute = (instruction & 2) != 0;
                if ((instruction & 1) != 0)
                {
                    Lr = Pc + 4;
                }

                nextPc = absolute ? target : Pc + target;
                return true;
            }

            case 19:
                return ExecuteBranchGroup(instruction, ref nextPc);

            case 20: // rlwimi
            {
                var shift = (int)((instruction >> 11) & 0x1F);
                var mask = MakeMask((int)((instruction >> 6) & 0x1F), (int)((instruction >> 1) & 0x1F));
                _gpr[a] = (BitOperations.RotateLeft(_gpr[d], shift) & mask) | (_gpr[a] & ~mask);
                if (rc)
                {
                    UpdateCr0(_gpr[a]);
                }

                return true;
            }

            case 21: // rlwinm
            {
                var shift = (int)((instruction >> 11) & 0x1F);
                var mask = MakeMask((int)((instruction >> 6) & 0x1F), (int)((instruction >> 1) & 0x1F));
                _gpr[a] = BitOperations.RotateLeft(_gpr[d], shift) & mask;
                if (rc)
                {
                    UpdateCr0(_gpr[a]);
                }

                return true;
            }

            case 23: // rlwnm
            {
                var shift = (int)(_gpr[b] & 0x1F);
                var mask = MakeMask((int)((instruction >> 6) & 0x1F), (int)((instruction >> 1) & 0x1F));
                _gpr[a] = BitOperations.RotateLeft(_gpr[d], shift) & mask;
                if (rc)
                {
                    UpdateCr0(_gpr[a]);
                }

                return true;
            }

            case 24: // ori (nop when every field is zero)
                _gpr[a] = _gpr[d] | uimm;
                return true;

            case 25: // oris
                _gpr[a] = _gpr[d] | (uimm << 16);
                return true;

            case 26: // xori
                _gpr[a] = _gpr[d] ^ uimm;
                return true;

            case 27: // xoris
                _gpr[a] = _gpr[d] ^ (uimm << 16);
                return true;

            case 28: // andi.
                _gpr[a] = _gpr[d] & uimm;
                UpdateCr0(_gpr[a]);
                return true;

            case 29: // andis.
                _gpr[a] = _gpr[d] & (uimm << 16);
                UpdateCr0(_gpr[a]);
                return true;

            case 31:
                return ExecuteIntegerGroup(instruction, d, a, b, rc);

            case 32: // lwz
                _gpr[d] = _memory.ReadUInt32(EffectiveAddress(a, simm));
                return true;

            case 33: // lwzu
                return LoadWithUpdate(a, d, simm, 4, signed: false);

            case 34: // lbz
                _gpr[d] = _memory.ReadByte(EffectiveAddress(a, simm));
                return true;

            case 35: // lbzu
                return LoadWithUpdate(a, d, simm, 1, signed: false);

            case 36: // stw
                _memory.WriteUInt32(EffectiveAddress(a, simm), _gpr[d]);
                return true;

            case 37: // stwu
                return StoreWithUpdate(a, d, simm, 4);

            case 38: // stb
                _memory.WriteByte(EffectiveAddress(a, simm), (byte)_gpr[d]);
                return true;

            case 39: // stbu
                return StoreWithUpdate(a, d, simm, 1);

            case 40: // lhz
                _gpr[d] = _memory.ReadUInt16(EffectiveAddress(a, simm));
                return true;

            case 41: // lhzu
                return LoadWithUpdate(a, d, simm, 2, signed: false);

            case 42: // lha
                _gpr[d] = (uint)(short)_memory.ReadUInt16(EffectiveAddress(a, simm));
                return true;

            case 43: // lhau
                return LoadWithUpdate(a, d, simm, 2, signed: true);

            case 44: // sth
                _memory.WriteUInt16(EffectiveAddress(a, simm), (ushort)_gpr[d]);
                return true;

            case 45: // sthu
                return StoreWithUpdate(a, d, simm, 2);

            case 46: // lmw
            {
                var address = EffectiveAddress(a, simm);
                for (var register = d; register < GeneralRegisterCount; register++)
                {
                    _gpr[register] = _memory.ReadUInt32(address);
                    address += 4;
                }

                return true;
            }

            case 47: // stmw
            {
                var address = EffectiveAddress(a, simm);
                for (var register = d; register < GeneralRegisterCount; register++)
                {
                    _memory.WriteUInt32(address, _gpr[register]);
                    address += 4;
                }

                return true;
            }

            // Every floating point form checks the unit is switched on first.
            // The exception is synchronous: when it is taken the instruction
            // does not run, and rfi will bring control back to retry it.
            case 4:
                return TryTakeFloatingPointUnavailable(ref nextPc) ||
                    ExecutePairedSingle(instruction, d, a, b, rc);

            case >= 48 and <= 55: // lfs/lfsu/lfd/lfdu/stfs/stfsu/stfd/stfdu
                return TryTakeFloatingPointUnavailable(ref nextPc) ||
                    ExecuteFloatMemory(primary, d, a, simm);

            case 56 or 57 or 60 or 61: // psq_l/psq_lu/psq_st/psq_stu
                return TryTakeFloatingPointUnavailable(ref nextPc) ||
                    ExecuteQuantisedMemory(instruction, primary, d, a);

            case 59:
                return TryTakeFloatingPointUnavailable(ref nextPc) ||
                    ExecuteSingleFloat(instruction, d, a, b, rc);

            case 63:
                return TryTakeFloatingPointUnavailable(ref nextPc) ||
                    ExecuteDoubleFloat(instruction, d, a, b, rc);

            case 17: // sc — enter the system call handler
                _spr[SprSrr0] = Pc + 4;
                _spr[SprSrr1] = Msr;
                nextPc = SystemCallVector;
                return true;

            default:
                return false;
        }
    }

    /// <summary>Primary opcode 19: branches to LR/CTR, condition logic, rfi.</summary>
    private bool ExecuteBranchGroup(uint instruction, ref uint nextPc)
    {
        var extended = (instruction >> 1) & 0x3FF;
        switch (extended)
        {
            case 16: // bclr
            case 528: // bcctr
            {
                var bo = (instruction >> 21) & 0x1F;
                var bi = (instruction >> 16) & 0x1F;

                // bcctr must not decrement CTR: it is the register it branches
                // through. Only bclr honours the counter form.
                var taken = extended == 16
                    ? TestBranchCondition(bo, bi)
                    : TestConditionOnly(bo, bi);
                var target = extended == 16 ? Lr & ~3u : Ctr & ~3u;
                if ((instruction & 1) != 0)
                {
                    Lr = Pc + 4;
                }

                if (taken)
                {
                    nextPc = target;
                }

                return true;
            }

            case 0: // mcrf
            {
                var destination = (int)((instruction >> 23) & 7);
                var source = (int)((instruction >> 18) & 7);
                SetCrField(destination, GetCrField(source));
                return true;
            }

            case 50: // rfi
                // Returning from an exception restores the saved MSR and PC.
                // Nothing raises exceptions yet, so this only ever runs from
                // the default handlers the boot state installs.
                Msr = _spr[SprSrr1];
                nextPc = _spr[SprSrr0] & ~3u;
                return true;

            case 150: // isync
                return true;

            case 33: // crnor
            case 129: // crandc
            case 193: // crxor
            case 225: // crnand
            case 257: // crand
            case 289: // creqv
            case 417: // crorc
            case 449: // cror
            {
                var destination = (int)((instruction >> 21) & 0x1F);
                var left = GetCrBit((instruction >> 16) & 0x1F);
                var right = GetCrBit((instruction >> 11) & 0x1F);
                var result = extended switch
                {
                    33 => ~(left | right),
                    129 => left & ~right,
                    193 => left ^ right,
                    225 => ~(left & right),
                    257 => left & right,
                    289 => ~(left ^ right),
                    417 => left | ~right,
                    _ => left | right
                };

                SetCrBit(destination, (result & 1) != 0);
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Primary opcode 31: the integer register-to-register operations, the
    /// indexed loads and stores, the special-register moves, and the cache
    /// control instructions.
    /// </summary>
    private bool ExecuteIntegerGroup(uint instruction, int d, int a, int b, bool rc)
    {
        var extended = (instruction >> 1) & 0x3FF;
        var oe = (instruction & 0x400) != 0;

        switch (extended)
        {
            // --- comparison -------------------------------------------------
            case 0: // cmp
                CompareSigned((int)((instruction >> 23) & 7), (int)_gpr[a], (int)_gpr[b]);
                return true;
            case 32: // cmpl
                CompareUnsigned((int)((instruction >> 23) & 7), _gpr[a], _gpr[b]);
                return true;

            // --- addition and subtraction -----------------------------------
            case 266: // add
                return WriteResult(d, Add(_gpr[a], _gpr[b], 0, false, oe), rc);
            case 10: // addc
                return WriteResult(d, Add(_gpr[a], _gpr[b], 0, true, oe), rc);
            case 138: // adde
                return WriteResult(d, Add(_gpr[a], _gpr[b], CarryIn, true, oe), rc);
            case 202: // addze
                return WriteResult(d, Add(_gpr[a], 0, CarryIn, true, oe), rc);
            case 234: // addme
                return WriteResult(d, Add(_gpr[a], 0xFFFF_FFFF, CarryIn, true, oe), rc);
            case 40: // subf
                return WriteResult(d, Add(~_gpr[a], _gpr[b], 1, false, oe), rc);
            case 8: // subfc
                return WriteResult(d, Add(~_gpr[a], _gpr[b], 1, true, oe), rc);
            case 136: // subfe
                return WriteResult(d, Add(~_gpr[a], _gpr[b], CarryIn, true, oe), rc);
            case 200: // subfze
                return WriteResult(d, Add(~_gpr[a], 0, CarryIn, true, oe), rc);
            case 232: // subfme
                return WriteResult(d, Add(~_gpr[a], 0xFFFF_FFFF, CarryIn, true, oe), rc);
            case 104: // neg
                return WriteResult(d, Add(~_gpr[a], 0, 1, false, oe), rc);

            // --- multiplication and division --------------------------------
            case 235: // mullw
            {
                var product = (long)(int)_gpr[a] * (int)_gpr[b];
                SetOverflow(oe, product != (int)product);
                return WriteResult(d, (uint)product, rc);
            }

            case 75: // mulhw
                return WriteResult(d, (uint)(((long)(int)_gpr[a] * (int)_gpr[b]) >> 32), rc);
            case 11: // mulhwu
                return WriteResult(d, (uint)(((ulong)_gpr[a] * _gpr[b]) >> 32), rc);

            case 491: // divw
            {
                var dividend = (int)_gpr[a];
                var divisor = (int)_gpr[b];
                if (divisor == 0 || (dividend == int.MinValue && divisor == -1))
                {
                    // Architecturally undefined. Zero matches what the
                    // hardware leaves behind and keeps the run deterministic.
                    SetOverflow(oe, true);
                    return WriteResult(d, 0, rc);
                }

                SetOverflow(oe, false);
                return WriteResult(d, (uint)(dividend / divisor), rc);
            }

            case 459: // divwu
            {
                var divisor = _gpr[b];
                if (divisor == 0)
                {
                    SetOverflow(oe, true);
                    return WriteResult(d, 0, rc);
                }

                SetOverflow(oe, false);
                return WriteResult(d, _gpr[a] / divisor, rc);
            }

            // --- logic ------------------------------------------------------
            case 28: return WriteResult(a, _gpr[d] & _gpr[b], rc);
            case 60: return WriteResult(a, _gpr[d] & ~_gpr[b], rc);   // andc
            case 444: return WriteResult(a, _gpr[d] | _gpr[b], rc);   // or (mr)
            case 412: return WriteResult(a, _gpr[d] | ~_gpr[b], rc);  // orc
            case 316: return WriteResult(a, _gpr[d] ^ _gpr[b], rc);   // xor
            case 476: return WriteResult(a, ~(_gpr[d] & _gpr[b]), rc); // nand
            case 124: return WriteResult(a, ~(_gpr[d] | _gpr[b]), rc); // nor
            case 284: return WriteResult(a, ~(_gpr[d] ^ _gpr[b]), rc); // eqv
            case 954: return WriteResult(a, (uint)(sbyte)_gpr[d], rc); // extsb
            case 922: return WriteResult(a, (uint)(short)_gpr[d], rc); // extsh
            case 26: return WriteResult(a, (uint)BitOperations.LeadingZeroCount(_gpr[d]), rc); // cntlzw

            // --- shifts -----------------------------------------------------
            case 24: // slw
            {
                var shift = _gpr[b] & 0x3F;
                return WriteResult(a, shift > 31 ? 0 : _gpr[d] << (int)shift, rc);
            }

            case 536: // srw
            {
                var shift = _gpr[b] & 0x3F;
                return WriteResult(a, shift > 31 ? 0 : _gpr[d] >> (int)shift, rc);
            }

            case 792: // sraw
            {
                var shift = (int)(_gpr[b] & 0x3F);
                var source = (int)_gpr[d];
                if (shift > 31)
                {
                    SetCarry(source < 0);
                    return WriteResult(a, (uint)(source >> 31), rc);
                }

                SetCarry(source < 0 && (_gpr[d] & ((1u << shift) - 1)) != 0);
                return WriteResult(a, (uint)(source >> shift), rc);
            }

            case 824: // srawi
            {
                var shift = b;
                var source = (int)_gpr[d];
                SetCarry(source < 0 && shift > 0 && (_gpr[d] & ((1u << shift) - 1)) != 0);
                return WriteResult(a, (uint)(source >> shift), rc);
            }

            // --- indexed loads and stores -----------------------------------
            case 23: _gpr[d] = _memory.ReadUInt32(IndexedAddress(a, b)); return true;   // lwzx
            case 87: _gpr[d] = _memory.ReadByte(IndexedAddress(a, b)); return true;     // lbzx
            case 279: _gpr[d] = _memory.ReadUInt16(IndexedAddress(a, b)); return true;  // lhzx
            case 343: _gpr[d] = (uint)(short)_memory.ReadUInt16(IndexedAddress(a, b)); return true; // lhax
            case 151: _memory.WriteUInt32(IndexedAddress(a, b), _gpr[d]); return true;  // stwx
            case 215: _memory.WriteByte(IndexedAddress(a, b), (byte)_gpr[d]); return true; // stbx
            case 407: _memory.WriteUInt16(IndexedAddress(a, b), (ushort)_gpr[d]); return true; // sthx

            case 55: return IndexedLoadWithUpdate(a, b, d, 4, signed: false);   // lwzux
            case 119: return IndexedLoadWithUpdate(a, b, d, 1, signed: false);  // lbzux
            case 311: return IndexedLoadWithUpdate(a, b, d, 2, signed: false);  // lhzux
            case 375: return IndexedLoadWithUpdate(a, b, d, 2, signed: true);   // lhaux
            case 183: return IndexedStoreWithUpdate(a, b, d, 4);                // stwux
            case 247: return IndexedStoreWithUpdate(a, b, d, 1);                // stbux
            case 439: return IndexedStoreWithUpdate(a, b, d, 2);                // sthux

            // --- byte-reversed and string forms -----------------------------
            case 535 or 567 or 599 or 631 or 663 or 695 or 727 or 759 or 983:
                return ExecuteFloatIndexed(extended, d, a, b);

            case 534: // lwbrx
                _gpr[d] = BinaryPrimitives.ReverseEndianness(_memory.ReadUInt32(IndexedAddress(a, b)));
                return true;
            case 662: // stwbrx
                _memory.WriteUInt32(
                    IndexedAddress(a, b),
                    BinaryPrimitives.ReverseEndianness(_gpr[d]));
                return true;

            // --- special register access ------------------------------------
            case 339: // mfspr
                _gpr[d] = ReadSpecialRegister(DecodeSprField(instruction));
                return true;
            case 467: // mtspr
                WriteSpecialRegister(DecodeSprField(instruction), _gpr[d]);
                return true;
            case 371: // mftb
            {
                var register = DecodeSprField(instruction);
                _gpr[d] = register == SprTbu ? (uint)(TimeBase >> 32) : (uint)TimeBase;
                return true;
            }

            case 19: // mfcr
                _gpr[d] = Cr;
                return true;

            case 144: // mtcrf
            {
                var fields = (instruction >> 12) & 0xFF;
                var mask = 0u;
                for (var field = 0; field < 8; field++)
                {
                    if ((fields & (0x80 >> field)) != 0)
                    {
                        mask |= 0xFu << (28 - (field * 4));
                    }
                }

                Cr = (Cr & ~mask) | (_gpr[d] & mask);
                return true;
            }

            case 83: // mfmsr
                _gpr[d] = Msr;
                return true;
            case 146: // mtmsr
                Msr = _gpr[d];
                return true;

            // --- cache, synchronisation and TLB -----------------------------
            // Nothing here has an observable effect on a machine with no cache
            // model and no address translation. They are accepted rather than
            // reported so the Unimplemented channel keeps meaning "this would
            // change the answer".
            case 54:   // dcbst
            case 86:   // dcbf
            case 246:  // dcbtst
            case 278:  // dcbt
            case 470:  // dcbi
            case 598:  // sync
            case 854:  // eieio
            case 982:  // icbi
            case 306:  // tlbie
            case 566:  // tlbsync
                return true;

            case 1014: // dcbz — the one cache instruction that writes memory
            {
                var line = IndexedAddress(a, b) & ~31u;
                for (var offset = 0u; offset < 32; offset += 4)
                {
                    _memory.WriteUInt32(line + offset, 0);
                }

                return true;
            }

            // --- segment registers ------------------------------------------
            // Recorded rather than executed: with translation unmodelled, a
            // title that actually depends on segments has to be visible.
            case 210: // mtsr
            case 242: // mtsrin
            case 595: // mfsr
            case 659: // mfsrin
                _trace.WriteOnce(
                    GameCubeTraceChannel.Unimplemented,
                    GameCubeTraceLevel.Warning,
                    "gekko/segment-register",
                    $"segment register access at 0x{Pc:X8} ignored; " +
                    "PixelCube does not model address translation");
                return true;

            default:
                return false;
        }
    }

    // ---------------------------------------------------------------- helpers

    private uint CarryIn => (Xer & XerCarry) != 0 ? 1u : 0u;

    private uint EffectiveAddress(int a, uint offset) =>
        a == 0 ? offset : _gpr[a] + offset;

    private uint IndexedAddress(int a, int b) =>
        a == 0 ? _gpr[b] : _gpr[a] + _gpr[b];

    private bool LoadWithUpdate(int a, int d, uint offset, int size, bool signed)
    {
        // rA == 0 or rA == rD is invalid for the update forms; the hardware
        // leaves the result undefined, so refusing is more useful than
        // inventing an answer.
        if (a == 0 || a == d)
        {
            return false;
        }

        var address = _gpr[a] + offset;
        _gpr[d] = ReadSized(address, size, signed);
        _gpr[a] = address;
        return true;
    }

    private bool StoreWithUpdate(int a, int s, uint offset, int size)
    {
        if (a == 0)
        {
            return false;
        }

        var address = _gpr[a] + offset;
        WriteSized(address, size, _gpr[s]);
        _gpr[a] = address;
        return true;
    }

    private bool IndexedLoadWithUpdate(int a, int b, int d, int size, bool signed)
    {
        if (a == 0 || a == d)
        {
            return false;
        }

        var address = _gpr[a] + _gpr[b];
        _gpr[d] = ReadSized(address, size, signed);
        _gpr[a] = address;
        return true;
    }

    private bool IndexedStoreWithUpdate(int a, int b, int s, int size)
    {
        if (a == 0)
        {
            return false;
        }

        var address = _gpr[a] + _gpr[b];
        WriteSized(address, size, _gpr[s]);
        _gpr[a] = address;
        return true;
    }

    private uint ReadSized(uint address, int size, bool signed) => size switch
    {
        1 => _memory.ReadByte(address),
        2 => signed ? (uint)(short)_memory.ReadUInt16(address) : _memory.ReadUInt16(address),
        _ => _memory.ReadUInt32(address)
    };

    private void WriteSized(uint address, int size, uint value)
    {
        switch (size)
        {
            case 1:
                _memory.WriteByte(address, (byte)value);
                break;
            case 2:
                _memory.WriteUInt16(address, (ushort)value);
                break;
            default:
                _memory.WriteUInt32(address, value);
                break;
        }
    }

    private void ExecuteConditionalBranch(uint instruction, ref uint nextPc)
    {
        var bo = (instruction >> 21) & 0x1F;
        var bi = (instruction >> 16) & 0x1F;
        var target = (uint)(short)(instruction & 0xFFFC);
        var absolute = (instruction & 2) != 0;
        if ((instruction & 1) != 0)
        {
            Lr = Pc + 4;
        }

        if (TestBranchCondition(bo, bi))
        {
            nextPc = absolute ? target : Pc + target;
        }
    }

    /// <summary>
    /// The full BO test: decrement CTR when asked, then check the condition
    /// bit unless BO says to ignore it.
    /// </summary>
    private bool TestBranchCondition(uint bo, uint bi)
    {
        var counterOk = true;
        if ((bo & 0x04) == 0)
        {
            Ctr--;
            counterOk = (bo & 0x02) != 0 ? Ctr == 0 : Ctr != 0;
        }

        return counterOk && TestConditionOnly(bo, bi);
    }

    private bool TestConditionOnly(uint bo, uint bi) =>
        (bo & 0x10) != 0 || GetCrBit(bi) == ((bo >> 3) & 1);

    private uint GetCrBit(uint bit) => (Cr >> (31 - (int)bit)) & 1;

    private void SetCrBit(int bit, bool value)
    {
        var mask = 1u << (31 - bit);
        Cr = value ? Cr | mask : Cr & ~mask;
    }

    private uint GetCrField(int field) => (Cr >> (28 - (field * 4))) & 0xF;

    private void SetCrField(int field, uint value)
    {
        var shift = 28 - (field * 4);
        Cr = (Cr & ~(0xFu << shift)) | ((value & 0xF) << shift);
    }

    private void UpdateCr0(uint value) => SetCrField(0, ComparisonFlags(
        (int)value < 0,
        (int)value > 0));

    private void CompareSigned(int field, int left, int right) =>
        SetCrField(field, ComparisonFlags(left < right, left > right));

    private void CompareUnsigned(int field, uint left, uint right) =>
        SetCrField(field, ComparisonFlags(left < right, left > right));

    private uint ComparisonFlags(bool less, bool greater)
    {
        var flags = less ? 8u : greater ? 4u : 2u;
        return (Xer & XerSummaryOverflow) != 0 ? flags | 1u : flags;
    }

    /// <summary>
    /// Stores a result and applies the record bit. Returns true so the
    /// dispatch switch can tail-call it, which keeps each opcode one line.
    /// </summary>
    private bool WriteResult(int destination, uint value, bool rc)
    {
        _gpr[destination] = value;
        if (rc)
        {
            UpdateCr0(value);
        }

        return true;
    }

    private uint Add(uint left, uint right, uint carryIn, bool setCarry, bool setOverflow)
    {
        var sum = (ulong)left + right + carryIn;
        var result = (uint)sum;
        if (setCarry)
        {
            SetCarry(sum > uint.MaxValue);
        }

        if (setOverflow)
        {
            // Signed overflow: both operands share a sign the result does not.
            var overflow = ((left ^ result) & (right ^ result) & 0x8000_0000) != 0;
            SetOverflow(true, overflow);
        }

        return result;
    }

    private void SetCarry(bool carry) =>
        Xer = carry ? Xer | XerCarry : Xer & ~XerCarry;

    private void SetOverflow(bool enabled, bool overflow)
    {
        if (!enabled)
        {
            return;
        }

        // SO is sticky: once set it stays until software clears XER.
        Xer = overflow
            ? Xer | XerOverflow | XerSummaryOverflow
            : Xer & ~XerOverflow;
    }

    private static uint DecodeSprField(uint instruction)
    {
        // The ten-bit SPR field is stored with its halves swapped.
        var field = (instruction >> 11) & 0x3FF;
        return ((field & 0x1F) << 5) | ((field >> 5) & 0x1F);
    }

    private uint ReadSpecialRegister(uint register) => register switch
    {
        SprTbl or SprWriteTbl => (uint)TimeBase,
        SprTbu or SprWriteTbu => (uint)(TimeBase >> 32),
        < SpecialRegisterCount => _spr[register],
        _ => 0
    };

    private void WriteSpecialRegister(uint register, uint value)
    {
        switch (register)
        {
            case SprWriteTbl:
                TimeBase = (TimeBase & 0xFFFF_FFFF_0000_0000) | value;
                return;
            case SprWriteTbu:
                TimeBase = (TimeBase & 0xFFFF_FFFF) | ((ulong)value << 32);
                return;
            case SprDecrementer:
                // Through the property, which cancels an untaken request.
                Decrementer = value;
                return;
        }

        if (register >= SpecialRegisterCount)
        {
            return;
        }

        _spr[register] = value;

        // BAT writes are the one thing that would invalidate the flat address
        // model, so they are recorded rather than silently accepted.
        if (register is >= 528 and <= 543)
        {
            _trace.WriteOnce(
                GameCubeTraceChannel.Memory,
                GameCubeTraceLevel.Information,
                "gekko/bat-write",
                $"BAT register {register} programmed at 0x{Pc:X8}; PixelCube uses a " +
                "flat 0x8000_0000/0xC000_0000 mapping and does not walk the BATs");
        }
    }

    private static uint SignExtend26(uint value) =>
        (value & 0x0200_0000) != 0 ? value | 0xFC00_0000 : value;

    /// <summary>
    /// Builds the 32-bit mask a rotate-and-mask instruction applies, from its
    /// inclusive begin and end bit numbers. A wrapped range (end before begin)
    /// selects the complement.
    /// </summary>
    private static uint MakeMask(int begin, int end)
    {
        var mask = (0xFFFF_FFFFu >> begin) ^ (end >= 31 ? 0 : 0xFFFF_FFFFu >> (end + 1));
        return end < begin ? ~mask : mask;
    }

    private GekkoOutcome ReportUnimplemented(uint instruction)
    {
        var primary = instruction >> 26;
        var extended = primary is 19 or 31 or 59 or 63 ? (instruction >> 1) & 0x3FF : 0;
        var keyId = (primary << 16) | extended;
        if (!_unimplementedKeys.TryGetValue(keyId, out var key))
        {
            key = $"gekko/opcode/{primary}/{extended}";
            _unimplementedKeys[keyId] = key;
        }

        _trace.WriteOnce(
            GameCubeTraceChannel.Unimplemented,
            GameCubeTraceLevel.Warning,
            key,
            $"unimplemented instruction {instruction:X8} at 0x{Pc:X8}: " +
            $"{GekkoDisassembler.Describe(instruction, Pc)}");

        if (UnimplementedPolicy == GekkoUnimplementedPolicy.Stop)
        {
            return GekkoOutcome.Unimplemented;
        }

        // Survey mode: skip it and carry on, so one run produces the whole
        // list. Everything after this point is running on invented state.
        Pc += 4;
        AdvanceTime();
        InstructionsExecuted++;
        return GekkoOutcome.Completed;
    }
}
