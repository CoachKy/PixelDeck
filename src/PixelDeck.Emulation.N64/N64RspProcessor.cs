using System.Buffers.Binary;
using System.Runtime.Intrinsics;

namespace PixelDeck.Emulation.N64;

/// <summary>
/// Execution engine for the N64 RSP (Reality Signal Processor). Implements scalar MIPS opcodes,
/// COP0 SP DMA control, and the complete 32 128-bit SIMD COP2 vector instruction set.
/// </summary>
public sealed class N64RspProcessor : IN64RspBackend
{
    private readonly N64Memory _memory;

    private uint _spMemAddr;
    private uint _spDramAddr;
    private uint _spRdLen;
    private uint _spWrLen;

    private readonly IN64GraphicsBackend? _graphicsBackend;
    private readonly IN64AudioBackend? _audioBackend;

    /// <summary>
    /// Creates a new instance of the RSP processor attached to N64 memory.
    /// </summary>
    public N64RspProcessor(
        N64Memory memory,
        IN64GraphicsBackend? graphicsBackend = null,
        IN64AudioBackend? audioBackend = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _graphicsBackend = graphicsBackend;
        _audioBackend = audioBackend;
        State = new N64RspState();
    }

    /// <summary>
    /// Gets the internal register and execution state of the RSP.
    /// </summary>
    public N64RspState State { get; }

    /// <summary>
    /// Descriptive name of the RSP execution backend.
    /// </summary>
    public string Name => "Pixel64 Low-Level RSP Engine";

    /// <summary>
    /// Total number of RSP instructions executed since initialization or reset.
    /// </summary>
    public long InstructionsExecuted { get; private set; }

    /// <summary>
    /// Total number of RSP tasks processed.
    /// </summary>
    public long TasksProcessed { get; private set; }

    /// <summary>
    /// Executes a single RSP instruction at current PC.
    /// </summary>
    public void Step() => StepInstruction();

    /// <summary>
    /// Gets or sets whether high-level emulation (HLE) graphics/audio task fallback is enabled.
    /// </summary>
    public bool HleFallbackEnabled { get; set; } = true;

    /// <summary>
    /// Executes an RSP task. In HLE fallback mode, graphics/audio tasks are forwarded to backends.
    /// In LLE mode, raw RSP IMEM/DMEM vector execution is performed.
    /// </summary>
    public void ExecuteTask(N64RspTask task)
    {
        TasksProcessed++;
        if (task.Type == 2 && _audioBackend != null)
        {
            _audioBackend.Execute(task);
            State.Halted = true;
            State.Broke = true;
            return;
        }

        if (HleFallbackEnabled && task.Type == 1 && _graphicsBackend != null)
        {
            var profile = N64GraphicsTaskProfile.FromTask(_memory, task);
            if (profile.DetectedMicrocodeName is not ("S2DEX" or "S2DEX2" or "Unknown"))
            {
                _graphicsBackend.Execute(task);
                State.Halted = true;
                State.Broke = true;
                return;
            }
        }

        // LLE RSP execution mode: run instructions starting at IMEM 0x000 until BREAK or max cycle budget
        State.Pc = 0;
        State.Halted = false;
        State.Broke = false;

        const int maxInstructionsPerTask = 500_000;
        var executed = 0;
        while (!State.Halted && executed++ < maxInstructionsPerTask)
        {
            StepInstruction();
        }
    }

    private uint? _branchTarget;

    /// <summary>
    /// Single-steps a single 32-bit instruction at current PC with branch delay slots.
    /// </summary>
    public void StepInstruction()
    {
        if (State.Halted)
        {
            return;
        }

        InstructionsExecuted++;
        var pc = State.Pc & 0x0FFF;
        var instr = BinaryPrimitives.ReadUInt32BigEndian(_memory.SpImem.AsSpan((int)pc, 4));
        var nextPc = (pc + 4) & 0x0FFF;
        State.Pc = nextPc;
        _branchTarget = null;

        ExecuteInstruction(instr, nextPc);

        if (_branchTarget.HasValue)
        {
            var delaySlotInstr = BinaryPrimitives.ReadUInt32BigEndian(_memory.SpImem.AsSpan((int)nextPc, 4));
            State.Pc = _branchTarget.Value & 0x0FFF;
            ExecuteInstruction(delaySlotInstr, (State.Pc + 4) & 0x0FFF);
        }
    }

    /// <summary>
    /// Resets RSP execution state and SP registers.
    /// </summary>
    public void Reset()
    {
        State.Reset();
        _spMemAddr = 0;
        _spDramAddr = 0;
        _spRdLen = 0;
        _spWrLen = 0;
    }

    /// <summary>
    /// Serializes RSP state for save-states.
    /// </summary>
    public void SaveState(BinaryWriter writer) => State.SaveState(writer);

    /// <summary>
    /// Deserializes RSP state from save-states.
    /// </summary>
    public void LoadState(BinaryReader reader) => State.LoadState(reader);

    private void ExecuteInstruction(uint instr, uint nextPc)
    {
        var opcode = (int)((instr >> 26) & 0x3F);
        var rs = (int)((instr >> 21) & 0x1F);
        var rt = (int)((instr >> 16) & 0x1F);
        var rd = (int)((instr >> 11) & 0x1F);
        var sa = (int)((instr >> 6) & 0x1F);
        var funct = (int)(instr & 0x3F);
        var imm16 = (ushort)(instr & 0xFFFF);
        var simm16 = (short)imm16;
        var target26 = instr & 0x03FFFFFF;

        switch (opcode)
        {
            case 0x00: // SPECIAL
                ExecuteSpecial(rs, rt, rd, sa, funct, nextPc);
                break;
            case 0x01: // REGIMM
                ExecuteRegImm(rs, rt, simm16, nextPc);
                break;
            case 0x02: // J
                _branchTarget = (target26 & 0x0FFF) * 4;
                break;
            case 0x03: // JAL
                State.SetGpr(31, (nextPc + 4) & 0x0FFF);
                _branchTarget = (target26 & 0x0FFF) * 4;
                break;
            case 0x04: // BEQ
                if (State.GetGpr(rs) == State.GetGpr(rt))
                {
                    _branchTarget = (uint)((int)nextPc + (simm16 * 4)) & 0x0FFF;
                }
                break;
            case 0x05: // BNE
                if (State.GetGpr(rs) != State.GetGpr(rt))
                {
                    _branchTarget = (uint)((int)nextPc + (simm16 * 4)) & 0x0FFF;
                }
                break;
            case 0x06: // BLEZ
                if ((int)State.GetGpr(rs) <= 0)
                {
                    _branchTarget = (uint)((int)nextPc + (simm16 * 4)) & 0x0FFF;
                }
                break;
            case 0x07: // BGTZ
                if ((int)State.GetGpr(rs) > 0)
                {
                    _branchTarget = (uint)((int)nextPc + (simm16 * 4)) & 0x0FFF;
                }
                break;
            case 0x08: // ADDI
            case 0x09: // ADDIU
                State.SetGpr(rt, (uint)((int)State.GetGpr(rs) + simm16));
                break;
            case 0x0A: // SLTI
                State.SetGpr(rt, (int)State.GetGpr(rs) < simm16 ? 1u : 0u);
                break;
            case 0x0B: // SLTIU
                State.SetGpr(rt, State.GetGpr(rs) < (uint)simm16 ? 1u : 0u);
                break;
            case 0x0C: // ANDI
                State.SetGpr(rt, State.GetGpr(rs) & imm16);
                break;
            case 0x0D: // ORI
                State.SetGpr(rt, State.GetGpr(rs) | imm16);
                break;
            case 0x0E: // XORI
                State.SetGpr(rt, State.GetGpr(rs) ^ imm16);
                break;
            case 0x0F: // LUI
                State.SetGpr(rt, (uint)imm16 << 16);
                break;
            case 0x10: // COP0 (SP Control)
                if (rs == 0x00) // MFC0
                {
                    State.SetGpr(rt, ReadCop0Register(rd));
                }
                else if (rs == 0x04) // MTC0
                {
                    WriteCop0Register(rd, State.GetGpr(rt));
                }
                break;
            case 0x12: // COP2 (Vector)
                ExecuteCop2(rs, rt, rd, sa, funct);
                break;
            case 0x20: // LB
                State.SetGpr(rt, (uint)(sbyte)ReadDmem8((uint)((int)State.GetGpr(rs) + simm16)));
                break;
            case 0x21: // LH
                State.SetGpr(rt, (uint)(short)ReadDmem16((uint)((int)State.GetGpr(rs) + simm16)));
                break;
            case 0x23: // LW
                State.SetGpr(rt, ReadDmem32((uint)((int)State.GetGpr(rs) + simm16)));
                break;
            case 0x24: // LBU
                State.SetGpr(rt, ReadDmem8((uint)((int)State.GetGpr(rs) + simm16)));
                break;
            case 0x25: // LHU
                State.SetGpr(rt, ReadDmem16((uint)((int)State.GetGpr(rs) + simm16)));
                break;
            case 0x28: // SB
                WriteDmem8((uint)((int)State.GetGpr(rs) + simm16), (byte)State.GetGpr(rt));
                break;
            case 0x29: // SH
                WriteDmem16((uint)((int)State.GetGpr(rs) + simm16), (ushort)State.GetGpr(rt));
                break;
            case 0x2B: // SW
                WriteDmem32((uint)((int)State.GetGpr(rs) + simm16), State.GetGpr(rt));
                break;
            case 0x32: // LWC2 (vector load)
            case 0x3A: // SWC2 (vector store)
                // The whole instruction is forwarded: LWC2/SWC2 pack a
                // sub-opcode, an element and a scaled 7-bit offset into fields
                // the general MIPS decode above does not isolate.
                ExecuteVectorMemory(instr);
                break;
        }
    }

    private void ExecuteSpecial(int rs, int rt, int rd, int sa, int funct, uint nextPc)
    {
        switch (funct)
        {
            case 0x00: // SLL
                State.SetGpr(rd, State.GetGpr(rt) << sa);
                break;
            case 0x02: // SRL
                State.SetGpr(rd, State.GetGpr(rt) >> sa);
                break;
            case 0x03: // SRA
                State.SetGpr(rd, (uint)((int)State.GetGpr(rt) >> sa));
                break;
            case 0x04: // SLLV
                State.SetGpr(rd, State.GetGpr(rt) << (int)(State.GetGpr(rs) & 0x1F));
                break;
            case 0x06: // SRLV
                State.SetGpr(rd, State.GetGpr(rt) >> (int)(State.GetGpr(rs) & 0x1F));
                break;
            case 0x07: // SRAV
                State.SetGpr(rd, (uint)((int)State.GetGpr(rt) >> (int)(State.GetGpr(rs) & 0x1F)));
                break;
            case 0x08: // JR
                _branchTarget = State.GetGpr(rs) & 0x0FFF;
                break;
            case 0x09: // JALR
                State.SetGpr(rd, (nextPc + 4) & 0x0FFF);
                _branchTarget = State.GetGpr(rs) & 0x0FFF;
                break;
            case 0x0D: // BREAK
                State.Broke = true;
                State.Halted = true;
                break;
            case 0x20: // ADD
            case 0x21: // ADDU
                State.SetGpr(rd, State.GetGpr(rs) + State.GetGpr(rt));
                break;
            case 0x22: // SUB
            case 0x23: // SUBU
                State.SetGpr(rd, State.GetGpr(rs) - State.GetGpr(rt));
                break;
            case 0x24: // AND
                State.SetGpr(rd, State.GetGpr(rs) & State.GetGpr(rt));
                break;
            case 0x25: // OR
                State.SetGpr(rd, State.GetGpr(rs) | State.GetGpr(rt));
                break;
            case 0x26: // XOR
                State.SetGpr(rd, State.GetGpr(rs) ^ State.GetGpr(rt));
                break;
            case 0x27: // NOR
                State.SetGpr(rd, ~(State.GetGpr(rs) | State.GetGpr(rt)));
                break;
            case 0x2A: // SLT
                State.SetGpr(rd, (int)State.GetGpr(rs) < (int)State.GetGpr(rt) ? 1u : 0u);
                break;
            case 0x2B: // SLTU
                State.SetGpr(rd, State.GetGpr(rs) < State.GetGpr(rt) ? 1u : 0u);
                break;
        }
    }

    private void ExecuteRegImm(int rs, int rt, short simm16, uint nextPc)
    {
        switch (rt)
        {
            case 0x00: // BLTZ
                if ((int)State.GetGpr(rs) < 0)
                {
                    _branchTarget = (uint)((int)nextPc + (simm16 * 4)) & 0x0FFF;
                }
                break;
            case 0x01: // BGEZ
                if ((int)State.GetGpr(rs) >= 0)
                {
                    _branchTarget = (uint)((int)nextPc + (simm16 * 4)) & 0x0FFF;
                }
                break;
            case 0x10: // BLTZAL
                State.SetGpr(31, (nextPc + 4) & 0x0FFF);
                if ((int)State.GetGpr(rs) < 0)
                {
                    _branchTarget = (uint)((int)nextPc + (simm16 * 4)) & 0x0FFF;
                }
                break;
            case 0x11: // BGEZAL
                State.SetGpr(31, (nextPc + 4) & 0x0FFF);
                if ((int)State.GetGpr(rs) >= 0)
                {
                    _branchTarget = (uint)((int)nextPc + (simm16 * 4)) & 0x0FFF;
                }
                break;
        }
    }

    private uint ReadCop0Register(int reg) => reg switch
    {
        0 => _spMemAddr,
        1 => _spDramAddr,
        2 => _spRdLen,
        3 => _spWrLen,
        4 => State.GetStatusRegister(),
        5 => State.DmaFull ? 1u : 0u,
        6 => State.DmaBusy ? 1u : 0u,
        7 => 0u, // Semaphore
        8 => _memory.ReadIoWord(0x04100000),
        9 => _memory.ReadIoWord(0x04100004),
        10 => _memory.ReadIoWord(0x04100008),
        11 => _memory.ReadIoWord(0x0410000C),
        _ => 0u
    };

    private void WriteCop0Register(int reg, uint value)
    {
        switch (reg)
        {
            case 0:
                _spMemAddr = value & 0x1FFF;
                break;
            case 1:
                _spDramAddr = value & 0x00FFFFFF;
                break;
            case 2:
                _spRdLen = value;
                PerformSpDma(isWriteToSp: true);
                break;
            case 3:
                _spWrLen = value;
                PerformSpDma(isWriteToSp: false);
                break;
            case 4:
                State.WriteStatusRegister(value);
                break;
            case 8:
                _memory.WriteIoWord(0x04100000, value);
                break;
            case 9:
                _memory.WriteIoWord(0x04100004, value);
                break;
            case 11:
                _memory.WriteIoWord(0x0410000C, value);
                break;
        }
    }

    /// <summary>
    /// Executes an SP DMA between RDRAM and DMEM/IMEM.
    /// </summary>
    /// <remarks>
    /// SP_RD_LEN and SP_WR_LEN are not flat lengths. The register packs three
    /// fields: length in bits 11:0, count in bits 19:12, and skip in bits
    /// 31:20. The transfer moves <c>count + 1</c> rows of <c>length + 1</c>
    /// bytes (rounded up to a multiple of 8), advancing the RDRAM address by
    /// <c>skip</c> extra bytes between rows while the SP address stays
    /// contiguous. Microcode uses the strided form to gather non-contiguous
    /// structures, so treating the register as a single length silently
    /// truncates those transfers to their first row.
    /// </remarks>
    private void PerformSpDma(bool isWriteToSp)
    {
        State.DmaBusy = true;

        var register = isWriteToSp ? _spRdLen : _spWrLen;
        var rowLength = (int)((register & 0xFFF) | 7) + 1;
        var rowCount = (int)((register >> 12) & 0xFF) + 1;
        var skip = (int)((register >> 20) & 0xFFF);

        var dramOffset = (int)(_spDramAddr & 0x007FFFF8);
        var spOffset = (int)(_spMemAddr & 0x0FF8);
        var isImem = (_spMemAddr & 0x1000) != 0;
        Span<byte> targetSpSpan = isImem ? _memory.SpImem : _memory.SpDmem;
        var bankSize = targetSpSpan.Length;

        for (var row = 0; row < rowCount; row++)
        {
            for (var offset = 0; offset < rowLength; offset++)
            {
                var dram = dramOffset + offset;
                if ((uint)dram >= (uint)_memory.Rdram.Length)
                {
                    break;
                }

                // The SP side wraps inside its own 4 KiB bank rather than
                // spilling from DMEM into IMEM.
                var sp = (spOffset + offset) % bankSize;
                if (isWriteToSp)
                {
                    targetSpSpan[sp] = _memory.Rdram[dram];
                }
                else
                {
                    _memory.Rdram[dram] = targetSpSpan[sp];
                }
            }

            spOffset = (spOffset + rowLength) % bankSize;
            dramOffset += rowLength + skip;
        }

        // Hardware leaves the address registers pointing past the transfer.
        _spMemAddr = (uint)(spOffset & 0x0FFF) | (isImem ? 0x1000u : 0u);
        _spDramAddr = (uint)dramOffset & 0x00FFFFFF;

        State.DmaBusy = false;
    }

    private void ExecuteCop2(int rs, int rt, int rd, int sa, int funct)
    {
        if ((rs & 0x10) != 0) // Vector Operation (bit 25 == 1)
        {
            var elementSpecifier = rs & 0x0F;
            var vt = rt;
            var vs = rd;
            var vd = sa;
            ExecuteVectorOp(funct, vd: vd, vs: vs, vt: vt, elementSpecifier: elementSpecifier);
        }
        else
        {
            switch (rs)
            {
                case 0x00: // MFC2
                    State.SetGpr(rt, (uint)(short)State.GetVectorElement(rd, sa >> 1));
                    break;
                case 0x04: // MTC2
                    State.SetVectorElement(rd, sa >> 1, (ushort)State.GetGpr(rt));
                    break;
            }
        }
    }

    // ---------------------------------------------------------------------
    // Vector unit
    //
    // The COP2 vector opcode map below is the hardware funct encoding. An
    // earlier revision assigned mnemonics in a compacted sequential order
    // (VADD at 0x08, VAND at 0x19), which meant no real microcode could
    // execute. Do not renumber these without a primary reference.
    //
    //   0x00 VMULF  0x08 VMACF  0x10 VADD   0x20 VLT    0x28 VAND   0x30 VRCP
    //   0x01 VMULU  0x09 VMACU  0x11 VSUB   0x21 VEQ    0x29 VNAND  0x31 VRCPL
    //   0x02 VRNDP  0x0A VRNDN  0x13 VABS   0x22 VNE    0x2A VOR    0x32 VRCPH
    //   0x03 VMULQ  0x0B VMACQ  0x14 VADDC  0x23 VGE    0x2B VNOR   0x33 VMOV
    //   0x04 VMUDL  0x0C VMADL  0x15 VSUBC  0x24 VCL    0x2C VXOR   0x34 VRSQ
    //   0x05 VMUDM  0x0D VMADM  0x1D VSAR   0x25 VCH    0x2D VNXOR  0x35 VRSQL
    //   0x06 VMUDN  0x0E VMADN              0x26 VCR                0x36 VRSQH
    //   0x07 VMUDH  0x0F VMADH              0x27 VMRG               0x37 VNOP
    //
    // Control registers are bit-per-lane with lane N in bit N. VCO packs
    // carry/sign in the low byte and "not equal" in the high byte; VCC packs
    // the compare result low and the clip-high result high; VCE is one bit per
    // lane.
    // ---------------------------------------------------------------------

    private const int VectorOpVmulf = 0x00;
    private const int VectorOpVmulu = 0x01;
    private const int VectorOpVmudl = 0x04;
    private const int VectorOpVmudm = 0x05;
    private const int VectorOpVmudn = 0x06;
    private const int VectorOpVmudh = 0x07;
    private const int VectorOpVmacf = 0x08;
    private const int VectorOpVmacu = 0x09;
    private const int VectorOpVmadl = 0x0C;
    private const int VectorOpVmadm = 0x0D;
    private const int VectorOpVmadn = 0x0E;
    private const int VectorOpVmadh = 0x0F;
    private const int VectorOpVadd = 0x10;
    private const int VectorOpVsub = 0x11;
    private const int VectorOpVabs = 0x13;
    private const int VectorOpVaddc = 0x14;
    private const int VectorOpVsubc = 0x15;
    private const int VectorOpVsar = 0x1D;
    private const int VectorOpVlt = 0x20;
    private const int VectorOpVeq = 0x21;
    private const int VectorOpVne = 0x22;
    private const int VectorOpVge = 0x23;
    private const int VectorOpVcl = 0x24;
    private const int VectorOpVch = 0x25;
    private const int VectorOpVcr = 0x26;
    private const int VectorOpVmrg = 0x27;
    private const int VectorOpVand = 0x28;
    private const int VectorOpVnand = 0x29;
    private const int VectorOpVor = 0x2A;
    private const int VectorOpVnor = 0x2B;
    private const int VectorOpVxor = 0x2C;
    private const int VectorOpVnxor = 0x2D;
    private const int VectorOpVrcp = 0x30;
    private const int VectorOpVrcpl = 0x31;
    private const int VectorOpVrcph = 0x32;
    private const int VectorOpVmov = 0x33;
    private const int VectorOpVrsq = 0x34;
    private const int VectorOpVrsql = 0x35;
    private const int VectorOpVrsqh = 0x36;
    private const int VectorOpVnop = 0x37;

    /// <summary>Pending high half supplied by VRCPH/VRSQH for the next VRCPL/VRSQL.</summary>
    private int _divideIn;

    /// <summary>High half of the last reciprocal result, read back by VRCPH/VRSQH.</summary>
    private int _divideOut;

    private bool _divideInLoaded;

    /// <summary>
    /// Signed clamp of the accumulator's upper 32 bits into 16 bits. Used by
    /// the "high" multiply forms (VMULF, VMACF, VMUDM, VMADM, VMUDH, VMADH).
    /// </summary>
    private static ushort ClampSigned(long accumulator)
    {
        var value = accumulator >> 16;
        if (value < short.MinValue) return unchecked((ushort)short.MinValue);
        if (value > short.MaxValue) return unchecked((ushort)short.MaxValue);
        return unchecked((ushort)(short)value);
    }

    /// <summary>
    /// Unsigned clamp of the accumulator's upper 32 bits, used by VMULU/VMACU.
    /// </summary>
    private static ushort ClampUnsigned(long accumulator)
    {
        var value = accumulator >> 16;
        if (value < 0) return 0;
        if (value > ushort.MaxValue) return ushort.MaxValue;
        return (ushort)value;
    }

    /// <summary>
    /// Clamp used by the "low" multiply forms (VMUDL, VMUDN, VMADL, VMADN).
    /// The low accumulator slice survives only while the upper slices agree
    /// with the sign of the middle slice; otherwise the result saturates.
    /// </summary>
    private static ushort ClampLow(long accumulator)
    {
        var high = (short)(accumulator >> 32);
        var middle = (short)(accumulator >> 16);
        var low = (ushort)accumulator;
        if (high < 0)
        {
            return high == -1 && middle < 0 ? low : (ushort)0;
        }

        return high == 0 && middle >= 0 ? low : (ushort)0xFFFF;
    }

    private static void AssignBit(ref ushort register, int bit, bool value)
    {
        if (value)
        {
            register |= (ushort)(1 << bit);
        }
        else
        {
            register &= (ushort)~(1 << bit);
        }
    }

    private static void AssignBit(ref byte register, int bit, bool value)
    {
        if (value)
        {
            register |= (byte)(1 << bit);
        }
        else
        {
            register &= (byte)~(1 << bit);
        }
    }

    private void ExecuteVectorOp(int funct, int vd, int vs, int vt, int elementSpecifier)
    {
        if (funct == VectorOpVnop)
        {
            return;
        }

        if (funct is >= VectorOpVrcp and <= VectorOpVrsqh)
        {
            ExecuteVectorSingleLaneOp(funct, vd, vs, vt, elementSpecifier);
            return;
        }

        var vco = State.Vco;
        var vcc = State.Vcc;
        var vce = State.Vce;
        Span<ushort> result = stackalloc ushort[8];

        for (var lane = 0; lane < 8; lane++)
        {
            var a = (short)State.GetVectorElement(vs, lane);
            var b = (short)State.GetVectorElementBroadcast(vt, lane, elementSpecifier);
            var accumulator = State.GetAccumulator(lane);

            switch (funct)
            {
                case VectorOpVmulf:
                    accumulator = ((long)a * b * 2) + 0x8000;
                    State.SetAccumulator(lane, accumulator);
                    result[lane] = ClampSigned(accumulator);
                    break;
                case VectorOpVmulu:
                    accumulator = ((long)a * b * 2) + 0x8000;
                    State.SetAccumulator(lane, accumulator);
                    result[lane] = ClampUnsigned(accumulator);
                    break;
                case VectorOpVmudl:
                    accumulator = ((long)(ushort)a * (ushort)b) >> 16;
                    State.SetAccumulator(lane, accumulator);
                    result[lane] = ClampLow(accumulator);
                    break;
                case VectorOpVmudm:
                    accumulator = (long)a * (ushort)b;
                    State.SetAccumulator(lane, accumulator);
                    result[lane] = ClampSigned(accumulator);
                    break;
                case VectorOpVmudn:
                    accumulator = (long)(ushort)a * b;
                    State.SetAccumulator(lane, accumulator);
                    result[lane] = ClampLow(accumulator);
                    break;
                case VectorOpVmudh:
                    accumulator = ((long)a * b) << 16;
                    State.SetAccumulator(lane, accumulator);
                    result[lane] = ClampSigned(accumulator);
                    break;
                case VectorOpVmacf:
                    accumulator += (long)a * b * 2;
                    State.SetAccumulator(lane, accumulator);
                    result[lane] = ClampSigned(accumulator);
                    break;
                case VectorOpVmacu:
                    accumulator += (long)a * b * 2;
                    State.SetAccumulator(lane, accumulator);
                    result[lane] = ClampUnsigned(accumulator);
                    break;
                case VectorOpVmadl:
                    accumulator += ((long)(ushort)a * (ushort)b) >> 16;
                    State.SetAccumulator(lane, accumulator);
                    result[lane] = ClampLow(accumulator);
                    break;
                case VectorOpVmadm:
                    accumulator += (long)a * (ushort)b;
                    State.SetAccumulator(lane, accumulator);
                    result[lane] = ClampSigned(accumulator);
                    break;
                case VectorOpVmadn:
                    accumulator += (long)(ushort)a * b;
                    State.SetAccumulator(lane, accumulator);
                    result[lane] = ClampLow(accumulator);
                    break;
                case VectorOpVmadh:
                    accumulator += ((long)a * b) << 16;
                    State.SetAccumulator(lane, accumulator);
                    result[lane] = ClampSigned(accumulator);
                    break;
                case VectorOpVadd:
                {
                    // VADD consumes the carry left by a preceding VADDC and
                    // then clears VCO; leaving it set makes the next add
                    // carry a stale bit.
                    var carry = (vco & (1 << lane)) != 0 ? 1 : 0;
                    var sum = a + b + carry;
                    State.AccLo[lane] = unchecked((ushort)sum);
                    result[lane] = unchecked((ushort)(short)Math.Clamp(sum, short.MinValue, short.MaxValue));
                    AssignBit(ref vco, lane, false);
                    AssignBit(ref vco, lane + 8, false);
                    break;
                }
                case VectorOpVsub:
                {
                    var borrow = (vco & (1 << lane)) != 0 ? 1 : 0;
                    var difference = a - b - borrow;
                    State.AccLo[lane] = unchecked((ushort)difference);
                    result[lane] = unchecked((ushort)(short)Math.Clamp(difference, short.MinValue, short.MaxValue));
                    AssignBit(ref vco, lane, false);
                    AssignBit(ref vco, lane + 8, false);
                    break;
                }
                case VectorOpVabs:
                {
                    int value;
                    if (a < 0)
                    {
                        value = b == short.MinValue ? short.MaxValue : -b;
                    }
                    else if (a == 0)
                    {
                        value = 0;
                    }
                    else
                    {
                        value = b;
                    }

                    State.AccLo[lane] = unchecked((ushort)(a < 0 && b == short.MinValue ? -b : value));
                    result[lane] = unchecked((ushort)value);
                    break;
                }
                case VectorOpVaddc:
                {
                    var sum = (ushort)a + (ushort)b;
                    State.AccLo[lane] = unchecked((ushort)sum);
                    result[lane] = unchecked((ushort)sum);
                    AssignBit(ref vco, lane, sum > 0xFFFF);
                    AssignBit(ref vco, lane + 8, false);
                    break;
                }
                case VectorOpVsubc:
                {
                    var difference = (ushort)a - (ushort)b;
                    State.AccLo[lane] = unchecked((ushort)difference);
                    result[lane] = unchecked((ushort)difference);
                    AssignBit(ref vco, lane, difference < 0);
                    AssignBit(ref vco, lane + 8, (ushort)difference != 0);
                    break;
                }
                case VectorOpVsar:
                    // Hardware decodes only e=8/9/10 as ACC_HI/ACC_MID/ACC_LO;
                    // every other specifier reads as zero.
                    result[lane] = elementSpecifier switch
                    {
                        8 => State.AccHi[lane],
                        9 => State.AccMid[lane],
                        10 => State.AccLo[lane],
                        _ => 0
                    };
                    break;
                case VectorOpVlt:
                case VectorOpVeq:
                case VectorOpVne:
                case VectorOpVge:
                {
                    var notEqual = (vco & (1 << (lane + 8))) != 0;
                    var carry = (vco & (1 << lane)) != 0;
                    var condition = funct switch
                    {
                        VectorOpVlt => a < b || (a == b && notEqual && carry),
                        VectorOpVeq => a == b && !notEqual,
                        VectorOpVne => a != b || notEqual,
                        _ => a > b || (a == b && !(notEqual && carry))
                    };

                    var selected = unchecked((ushort)(condition ? a : b));
                    State.AccLo[lane] = selected;
                    result[lane] = selected;
                    AssignBit(ref vcc, lane, condition);
                    AssignBit(ref vcc, lane + 8, false);
                    AssignBit(ref vco, lane, false);
                    AssignBit(ref vco, lane + 8, false);
                    break;
                }
                case VectorOpVch:
                {
                    ushort selected;
                    if (((short)(a ^ b)) < 0)
                    {
                        var sum = (short)(a + b);
                        var equalsMinusOne = sum == -1;
                        AssignBit(ref vcc, lane, sum <= 0);
                        AssignBit(ref vcc, lane + 8, b < 0);
                        AssignBit(ref vce, lane, equalsMinusOne);
                        AssignBit(ref vco, lane + 8, sum != 0 && !equalsMinusOne);
                        AssignBit(ref vco, lane, true);
                        selected = sum <= 0 ? unchecked((ushort)-b) : unchecked((ushort)a);
                    }
                    else
                    {
                        var difference = (short)(a - b);
                        AssignBit(ref vcc, lane, b < 0);
                        AssignBit(ref vcc, lane + 8, difference >= 0);
                        AssignBit(ref vce, lane, false);
                        AssignBit(ref vco, lane + 8, difference != 0);
                        AssignBit(ref vco, lane, false);
                        selected = difference >= 0 ? unchecked((ushort)b) : unchecked((ushort)a);
                    }

                    State.AccLo[lane] = selected;
                    result[lane] = selected;
                    break;
                }
                case VectorOpVcr:
                {
                    ushort selected;
                    if (((short)(a ^ b)) < 0)
                    {
                        var lessOrEqual = a + b + 1 <= 0;
                        AssignBit(ref vcc, lane, lessOrEqual);
                        AssignBit(ref vcc, lane + 8, b < 0);
                        selected = lessOrEqual ? unchecked((ushort)~b) : unchecked((ushort)a);
                    }
                    else
                    {
                        var greaterOrEqual = a - b >= 0;
                        AssignBit(ref vcc, lane, b < 0);
                        AssignBit(ref vcc, lane + 8, greaterOrEqual);
                        selected = greaterOrEqual ? unchecked((ushort)b) : unchecked((ushort)a);
                    }

                    AssignBit(ref vco, lane, false);
                    AssignBit(ref vco, lane + 8, false);
                    AssignBit(ref vce, lane, false);
                    State.AccLo[lane] = selected;
                    result[lane] = selected;
                    break;
                }
                case VectorOpVcl:
                {
                    var carry = (vco & (1 << lane)) != 0;
                    var notEqual = (vco & (1 << (lane + 8))) != 0;
                    var compareExtension = (vce & (1 << lane)) != 0;
                    ushort selected;
                    if (carry)
                    {
                        if (!notEqual)
                        {
                            var sum = (ushort)a + (ushort)b;
                            var wrapped = (ushort)sum == 0;
                            var carriedOut = sum > 0xFFFF;
                            AssignBit(
                                ref vcc,
                                lane,
                                compareExtension ? wrapped || !carriedOut : wrapped && !carriedOut);
                        }

                        selected = (vcc & (1 << lane)) != 0
                            ? unchecked((ushort)-b)
                            : unchecked((ushort)a);
                    }
                    else
                    {
                        if (!notEqual)
                        {
                            AssignBit(ref vcc, lane + 8, (ushort)a - (ushort)b >= 0);
                        }

                        selected = (vcc & (1 << (lane + 8))) != 0
                            ? unchecked((ushort)b)
                            : unchecked((ushort)a);
                    }

                    AssignBit(ref vco, lane, false);
                    AssignBit(ref vco, lane + 8, false);
                    AssignBit(ref vce, lane, false);
                    State.AccLo[lane] = selected;
                    result[lane] = selected;
                    break;
                }
                case VectorOpVmrg:
                {
                    var selected = (vcc & (1 << lane)) != 0
                        ? unchecked((ushort)a)
                        : unchecked((ushort)b);
                    State.AccLo[lane] = selected;
                    result[lane] = selected;
                    AssignBit(ref vco, lane, false);
                    AssignBit(ref vco, lane + 8, false);
                    break;
                }
                case VectorOpVand:
                case VectorOpVnand:
                case VectorOpVor:
                case VectorOpVnor:
                case VectorOpVxor:
                case VectorOpVnxor:
                {
                    var left = (ushort)a;
                    var right = (ushort)b;
                    var value = funct switch
                    {
                        VectorOpVand => (ushort)(left & right),
                        VectorOpVnand => (ushort)~(left & right),
                        VectorOpVor => (ushort)(left | right),
                        VectorOpVnor => (ushort)~(left | right),
                        VectorOpVxor => (ushort)(left ^ right),
                        _ => (ushort)~(left ^ right)
                    };

                    State.AccLo[lane] = value;
                    result[lane] = value;
                    break;
                }
                default:
                    // VRNDP/VRNDN/VMULQ/VMACQ are unimplemented. They are not
                    // emitted by any retail microcode; leaving the destination
                    // untouched is preferable to writing a wrong value.
                    UnimplementedVectorOps++;
                    result[lane] = State.GetVectorElement(vd, lane);
                    break;
            }
        }

        State.WriteVectorRegister(vd, result);
        State.Vco = vco;
        State.Vcc = vcc;
        State.Vce = vce;
    }

    /// <summary>
    /// Count of executed COP2 opcodes with no implementation. A non-zero value
    /// means a task encountered something this core cannot model, and is worth
    /// reporting rather than silently rendering a wrong frame.
    /// </summary>
    public long UnimplementedVectorOps { get; private set; }

    /// <summary>
    /// VRCP/VRSQ family. These are single-lane: the element specifier selects
    /// the source element of vt, and the vs field selects the destination
    /// element of vd. The accumulator low slice still receives the broadcast
    /// source across all eight lanes.
    /// </summary>
    private void ExecuteVectorSingleLaneOp(int funct, int vd, int vs, int vt, int elementSpecifier)
    {
        var sourceElement = elementSpecifier & 7;
        var destinationElement = vs & 7;
        var input = (short)State.GetVectorElementBroadcast(vt, sourceElement, elementSpecifier);

        for (var lane = 0; lane < 8; lane++)
        {
            State.AccLo[lane] = State.GetVectorElementBroadcast(vt, lane, elementSpecifier);
        }

        switch (funct)
        {
            case VectorOpVrcp:
            case VectorOpVrsq:
            {
                var value = funct == VectorOpVrcp ? Reciprocal(input) : ReciprocalSquareRoot(input);
                _divideOut = value >> 16;
                _divideInLoaded = false;
                State.SetVectorElement(vd, destinationElement, (ushort)value);
                break;
            }
            case VectorOpVrcpl:
            case VectorOpVrsql:
            {
                var combined = _divideInLoaded
                    ? (_divideIn << 16) | (ushort)input
                    : input;
                var value = funct == VectorOpVrcpl
                    ? Reciprocal(combined)
                    : ReciprocalSquareRoot(combined);
                _divideOut = value >> 16;
                _divideIn = 0;
                _divideInLoaded = false;
                State.SetVectorElement(vd, destinationElement, (ushort)value);
                break;
            }
            case VectorOpVrcph:
            case VectorOpVrsqh:
                _divideIn = (ushort)input;
                _divideInLoaded = true;
                State.SetVectorElement(vd, destinationElement, (ushort)_divideOut);
                break;
            case VectorOpVmov:
                State.SetVectorElement(
                    vd,
                    destinationElement,
                    State.GetVectorElementBroadcast(vt, destinationElement, elementSpecifier));
                break;
        }
    }

    /// <summary>
    /// Reciprocal using the RSP's 512-entry ROM lookup followed by a
    /// normalizing shift.
    /// </summary>
    /// <remarks>
    /// The ROM contents in <see cref="N64RspState"/> are generated
    /// analytically rather than transcribed from hardware, so results are
    /// close but not guaranteed bit-exact. Replacing the table with the real
    /// ROM dump is a follow-up.
    /// </remarks>
    private static int Reciprocal(int input)
    {
        var mask = input >> 31;
        var data = input ^ mask;
        if (input > short.MinValue)
        {
            data -= mask;
        }

        if (data == 0)
        {
            return int.MaxValue;
        }

        if (input == short.MinValue)
        {
            return unchecked((int)0xFFFF0000);
        }

        var shift = System.Numerics.BitOperations.LeadingZeroCount((uint)data);
        var index = (int)((((uint)data << shift) >> 22) & 0x1FF);
        var value = (N64RspState.ReciprocalRom(index) | 0x10000) << 14;
        return (value >> (31 - shift)) ^ mask;
    }

    private static int ReciprocalSquareRoot(int input)
    {
        var mask = input >> 31;
        var data = input ^ mask;
        if (input > short.MinValue)
        {
            data -= mask;
        }

        if (data == 0)
        {
            return int.MaxValue;
        }

        if (input == short.MinValue)
        {
            return unchecked((int)0xFFFF0000);
        }

        var shift = System.Numerics.BitOperations.LeadingZeroCount((uint)data);
        var index = (int)((((uint)data << shift) >> 22) & 0x1FE) | (shift & 1);
        var value = (N64RspState.ReciprocalSquareRootRom(index) | 0x10000) << 14;
        return (value >> ((31 - shift) >> 1)) ^ mask;
    }

    /// <summary>
    /// LWC2 / SWC2. The offset is a 7-bit signed value scaled by the access
    /// size, the element comes from bits 10:7, and the transfer shape is
    /// selected by the sub-opcode in bits 15:11 -- treating every one of these
    /// as LQV/SQV silently corrupts the vertex loads F3DEX2 depends on.
    /// </summary>
    private void ExecuteVectorMemory(uint instruction)
    {
        var isLoad = (instruction >> 26) == 0x32;
        var baseRegister = (int)((instruction >> 21) & 0x1F);
        var vt = (int)((instruction >> 16) & 0x1F);
        var subOpcode = (int)((instruction >> 11) & 0x1F);
        var element = (int)((instruction >> 7) & 0x0F);
        var offset = (int)(instruction & 0x7F);
        if ((offset & 0x40) != 0)
        {
            offset -= 0x80;
        }

        var scale = subOpcode switch
        {
            0x00 => 1,   // LBV / SBV
            0x01 => 2,   // LSV / SSV
            0x02 => 4,   // LLV / SLV
            0x03 => 8,   // LDV / SDV
            _ => 16      // quad, packed and transpose forms
        };

        var address = (uint)((int)State.GetGpr(baseRegister) + (offset * scale));

        switch (subOpcode)
        {
            case 0x00: // LBV / SBV
            case 0x01: // LSV / SSV
            case 0x02: // LLV / SLV
            case 0x03: // LDV / SDV
            {
                var width = 1 << subOpcode;
                for (var index = 0; index < width; index++)
                {
                    var lane = element + index;
                    if (lane >= 16)
                    {
                        break;
                    }

                    if (isLoad)
                    {
                        WriteVectorByte(vt, lane, ReadDmem8(address + (uint)index));
                    }
                    else
                    {
                        WriteDmem8(address + (uint)index, ReadVectorByte(vt, lane));
                    }
                }

                break;
            }
            case 0x04: // LQV / SQV -- to the end of the current 16-byte row
            {
                var end = (int)((address & ~0xFu) + 16 - address);
                for (var index = 0; index < end; index++)
                {
                    var lane = element + index;
                    if (lane >= 16)
                    {
                        break;
                    }

                    if (isLoad)
                    {
                        WriteVectorByte(vt, lane, ReadDmem8(address + (uint)index));
                    }
                    else
                    {
                        WriteDmem8(address + (uint)index, ReadVectorByte(vt, lane));
                    }
                }

                break;
            }
            case 0x05: // LRV / SRV -- the leading part of the current 16-byte row
            {
                // LQV takes the bytes from the access address to the end of
                // the row; LRV takes the ones before it, landing them at the
                // matching tail of the register.
                var count = (int)(address & 0xF);
                var rowStart = address & ~0xFu;
                for (var index = 0; index < count; index++)
                {
                    var lane = element + (16 - count) + index;
                    if (lane >= 16)
                    {
                        break;
                    }

                    if (isLoad)
                    {
                        WriteVectorByte(vt, lane, ReadDmem8(rowStart + (uint)index));
                    }
                    else
                    {
                        WriteDmem8(rowStart + (uint)index, ReadVectorByte(vt, lane));
                    }
                }

                break;
            }
            case 0x06: // LPV / SPV
            case 0x07: // LUV / SUV
            {
                var shift = subOpcode == 0x06 ? 8 : 7;
                for (var lane = 0; lane < 8; lane++)
                {
                    var byteAddress = address + (uint)((element + lane) & 0xF);
                    if (isLoad)
                    {
                        State.SetVectorElement(vt, lane, (ushort)(ReadDmem8(byteAddress) << shift));
                    }
                    else
                    {
                        WriteDmem8(byteAddress, (byte)(State.GetVectorElement(vt, lane) >> shift));
                    }
                }

                break;
            }
            case 0x08: // LHV / SHV
            {
                for (var lane = 0; lane < 8; lane++)
                {
                    var byteAddress = address + (uint)((element + (lane * 2)) & 0xF);
                    if (isLoad)
                    {
                        State.SetVectorElement(vt, lane, (ushort)(ReadDmem8(byteAddress) << 7));
                    }
                    else
                    {
                        WriteDmem8(byteAddress, (byte)(State.GetVectorElement(vt, lane) >> 7));
                    }
                }

                break;
            }
            case 0x09: // LFV / SFV
            {
                for (var lane = 0; lane < 4; lane++)
                {
                    var byteAddress = address + (uint)((element + (lane * 4)) & 0xF);
                    if (isLoad)
                    {
                        State.SetVectorElement(vt, lane, (ushort)(ReadDmem8(byteAddress) << 7));
                    }
                    else
                    {
                        WriteDmem8(byteAddress, (byte)(State.GetVectorElement(vt, lane) >> 7));
                    }
                }

                break;
            }
            case 0x0A: // LWV / SWV
            {
                for (var index = 0; index < 16; index++)
                {
                    var lane = (element + index) & 0xF;
                    if (isLoad)
                    {
                        WriteVectorByte(vt, lane, ReadDmem8(address + (uint)index));
                    }
                    else
                    {
                        WriteDmem8(address + (uint)index, ReadVectorByte(vt, lane));
                    }
                }

                break;
            }
            // LTV / STV distribute eight halfwords across the eight registers
            // of an aligned bank, rotating which register receives each one.
            // This is the shape F3DEX2 uses to load vertices.
            //
            // UNVERIFIED: the rotation direction and the exact interaction
            // between the element specifier and the row offset have not been
            // confirmed against hardware. n64-systemtest's RSP group is the
            // intended gate for this (see docs/PIXEL64-ACCURACY-REMEDIATION.md
            // phase 2.3); treat a disagreement here as this code being wrong.
            case 0x0B:
            {
                var registerBase = vt & ~7;
                var rowStart = address & ~0x7u;
                for (var index = 0; index < 8; index++)
                {
                    var register = registerBase + (((element >> 1) + index) & 7);
                    var byteAddress = rowStart + (uint)(((index * 2) + (element & ~1)) & 0xF);
                    if (isLoad)
                    {
                        var high = ReadDmem8(byteAddress);
                        var low = ReadDmem8(byteAddress + 1);
                        State.SetVectorElement(register, index, (ushort)((high << 8) | low));
                    }
                    else
                    {
                        var value = State.GetVectorElement(register, index);
                        WriteDmem8(byteAddress, (byte)(value >> 8));
                        WriteDmem8(byteAddress + 1, (byte)value);
                    }
                }

                break;
            }
        }
    }

    private byte ReadVectorByte(int register, int lane)
    {
        var element = State.GetVectorElement(register, (lane >> 1) & 7);
        return (lane & 1) == 0 ? (byte)(element >> 8) : (byte)element;
    }

    private void WriteVectorByte(int register, int lane, byte value)
    {
        var index = (lane >> 1) & 7;
        var element = State.GetVectorElement(register, index);
        element = (lane & 1) == 0
            ? (ushort)((element & 0x00FF) | (value << 8))
            : (ushort)((element & 0xFF00) | value);
        State.SetVectorElement(register, index, element);
    }

    private byte ReadDmem8(uint address)
    {
        var offset = (int)(address & 0x0FFF);
        return _memory.SpDmem[offset];
    }

    private ushort ReadDmem16(uint address)
    {
        var offset = (int)(address & 0x0FFF);
        return BinaryPrimitives.ReadUInt16BigEndian(_memory.SpDmem.AsSpan(offset, 2));
    }

    private uint ReadDmem32(uint address)
    {
        var offset = (int)(address & 0x0FFF);
        return BinaryPrimitives.ReadUInt32BigEndian(_memory.SpDmem.AsSpan(offset, 4));
    }

    private void WriteDmem8(uint address, byte value)
    {
        var offset = (int)(address & 0x0FFF);
        _memory.SpDmem[offset] = value;
    }

    private void WriteDmem16(uint address, ushort value)
    {
        var offset = (int)(address & 0x0FFF);
        BinaryPrimitives.WriteUInt16BigEndian(_memory.SpDmem.AsSpan(offset, 2), value);
    }

    private void WriteDmem32(uint address, uint value)
    {
        var offset = (int)(address & 0x0FFF);
        BinaryPrimitives.WriteUInt32BigEndian(_memory.SpDmem.AsSpan(offset, 4), value);
    }
}
