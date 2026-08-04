using System.Runtime.CompilerServices;

namespace PixelDeck.Emulation.N64;

public sealed partial class Vr4300Cpu
{
    private const int Cp0Index = 0;
    private const int Cp0Random = 1;
    private const int Cp0EntryLo0 = 2;
    private const int Cp0EntryLo1 = 3;
    private const int Cp0Context = 4;
    private const int Cp0PageMask = 5;
    private const int Cp0BadVAddr = 8;
    private const int Cp0Wired = 6;
    private const int Cp0Count = 9;
    private const int Cp0EntryHi = 10;
    private const int Cp0Compare = 11;
    private const int Cp0Status = 12;
    private const int Cp0Cause = 13;
    private const int Cp0Epc = 14;
    private const int Cp0PrId = 15;
    private const int Cp0Config = 16;
    private const int Cp0ErrorEpc = 30;

    private readonly N64Memory _memory;
    private readonly ulong[] _registers = new ulong[32];
    private readonly ulong[] _floatingRegisters = new ulong[32];
    private readonly uint[] _coprocessor0 = new uint[32];
    private uint _nextProgramCounter;
    private bool _nextInstructionIsDelaySlot;
    private uint _nextDelaySlotBranchAddress;
    private bool _executingDelaySlot;
    private uint _executingDelaySlotBranchAddress;

    public int CountPerOp { get; set; } = 2;
    private int _countSubCycle;

    public Vr4300Cpu(N64Memory memory, N64Cic cic, N64VideoRegion region)
    {
        _memory = memory;
        Reset(cic, region);
    }

    public ReadOnlySpan<ulong> Registers => _registers;

    public uint ProgramCounter { get; private set; }

    public ulong Hi { get; private set; }

    public ulong Lo { get; private set; }

    public long InstructionsExecuted { get; private set; }

    /// <summary>
    /// Host-side performance diagnostic counting emulated instructions whose
    /// clocks were advanced in bulk while the CPU was in a side-effect-free
    /// idle loop. This is intentionally excluded from save states.
    /// </summary>
    public long IdleInstructionsSkipped { get; private set; }

    public uint LastInstruction { get; private set; }

    public int UnsupportedInstructionCount { get; private set; }

    /// <summary>
    /// Diagnostic exception counters. Interrupts are tracked separately because
    /// a healthy game raises one for virtually every video field; an increasing
    /// non-interrupt count is the useful signal when a title stops progressing.
    /// These host-only counters are deliberately excluded from save states.
    /// </summary>
    public long ExceptionsRaised { get; private set; }

    public long InterruptExceptionsRaised { get; private set; }

    public long NonInterruptExceptionsRaised => ExceptionsRaised - InterruptExceptionsRaised;

    public int LastExceptionCode { get; private set; } = -1;

    public uint LastExceptionAddress { get; private set; }

    public uint ReadCoprocessor0(int index) => _coprocessor0[index & 31];

    internal void SaveState(BinaryWriter writer)
    {
        writer.Write(ProgramCounter);
        writer.Write(_nextProgramCounter);
        writer.Write(_nextInstructionIsDelaySlot);
        writer.Write(_nextDelaySlotBranchAddress);
        writer.Write(_executingDelaySlot);
        writer.Write(_executingDelaySlotBranchAddress);
        writer.Write(Hi);
        writer.Write(Lo);
        writer.Write(InstructionsExecuted);
        writer.Write(LastInstruction);
        writer.Write(UnsupportedInstructionCount);
        foreach (var value in _registers) writer.Write(value);
        foreach (var value in _floatingRegisters) writer.Write(value);
        foreach (var value in _coprocessor0) writer.Write(value);
    }

    internal void LoadState(BinaryReader reader)
    {
        ProgramCounter = reader.ReadUInt32();
        _nextProgramCounter = reader.ReadUInt32();
        _nextInstructionIsDelaySlot = reader.ReadBoolean();
        _nextDelaySlotBranchAddress = reader.ReadUInt32();
        _executingDelaySlot = reader.ReadBoolean();
        _executingDelaySlotBranchAddress = reader.ReadUInt32();
        Hi = reader.ReadUInt64();
        Lo = reader.ReadUInt64();
        InstructionsExecuted = reader.ReadInt64();
        LastInstruction = reader.ReadUInt32();
        UnsupportedInstructionCount = reader.ReadInt32();
        for (var index = 0; index < _registers.Length; index++) _registers[index] = reader.ReadUInt64();
        for (var index = 0; index < _floatingRegisters.Length; index++) _floatingRegisters[index] = reader.ReadUInt64();
        for (var index = 0; index < _coprocessor0.Length; index++) _coprocessor0[index] = reader.ReadUInt32();
        _memory.SetTlbAsid((byte)_coprocessor0[Cp0EntryHi]);
        _registers[0] = 0;
    }

    public void Reset(N64Cic cic, N64VideoRegion region)
    {
        ResetCachedBlocks();
        Array.Clear(_registers);
        Array.Clear(_floatingRegisters);
        Array.Clear(_coprocessor0);
        Hi = 0;
        Lo = 0;
        InstructionsExecuted = 0;
        IdleInstructionsSkipped = 0;
        LastInstruction = 0;
        UnsupportedInstructionCount = 0;
        ExceptionsRaised = 0;
        InterruptExceptionsRaised = 0;
        LastExceptionCode = -1;
        LastExceptionAddress = 0;
        _nextInstructionIsDelaySlot = false;
        _nextDelaySlotBranchAddress = 0;
        _executingDelaySlot = false;
        _executingDelaySlotBranchAddress = 0;

        _registers[6] = 0xFFFFFFFFA4001F0C;
        _registers[7] = 0xFFFFFFFFA4001F08;
        _registers[8] = 0x00000000000000C0;
        _registers[10] = 0x0000000000000040;
        _registers[11] = 0xFFFFFFFFA4000040;
        _registers[20] = region == N64VideoRegion.Ntsc ? 1u : 0u;
        _registers[22] = cic switch
        {
            N64Cic.Cic6103 => 0x78,
            N64Cic.Cic6105 => 0x91,
            N64Cic.Cic6106 => 0x85,
            _ => 0x3F
        };
        _registers[29] = 0xFFFFFFFFA4001FF0;
        _registers[31] = 0xFFFFFFFFA4001550;

        _coprocessor0[Cp0Status] = 0x34000000;
        _coprocessor0[Cp0PrId] = 0x00000B00;
        _coprocessor0[Cp0Config] = 0x7006E463;
        _coprocessor0[Cp0Count] = 0x00005000;
        _coprocessor0[Cp0Random] = 31;
        _memory.SetTlbAsid(0);

        ProgramCounter = 0xA4000040;
        _nextProgramCounter = ProgramCounter + 4;
    }

    public void Step()
    {
        UpdateInterruptLines();
        if (InterruptsEnabled() && !_nextInstructionIsDelaySlot)
        {
            _hasPrefetchedInstruction = false;
            _executingDelaySlot = false;
            EnterException(0, ProgramCounter);
            InstructionsExecuted++;
            AdvanceClock();
            _registers[0] = 0;
            return;
        }

        var instructionAddress = ProgramCounter;
        _executingDelaySlot = _nextInstructionIsDelaySlot;
        _executingDelaySlotBranchAddress = _nextDelaySlotBranchAddress;
        _nextInstructionIsDelaySlot = false;
        N64TlbFault fetchFault;
        uint instruction;
        if (_hasPrefetchedInstruction && _prefetchedInstructionAddress == instructionAddress)
        {
            _hasPrefetchedInstruction = false;
            instruction = _prefetchedInstruction;
            fetchFault = N64TlbFault.None;
        }
        else
        {
            _hasPrefetchedInstruction = false;
            fetchFault = _memory.FetchInstruction(instructionAddress, out instruction);
        }
        if (fetchFault != N64TlbFault.None)
        {
            EnterTlbException(instructionAddress, isStore: false, fetchFault, instructionAddress);
            InstructionsExecuted++;
            AdvanceClock();
            _registers[0] = 0;
            return;
        }

        LastInstruction = instruction;
        ProgramCounter = _nextProgramCounter;
        _nextProgramCounter += 4;
        InstructionsExecuted++;
        AdvanceClock();

        var opcode = instruction >> 26;
        var rs = (int)((instruction >> 21) & 31);
        var rt = (int)((instruction >> 16) & 31);
        var immediate = (ushort)instruction;
        var signedImmediate = (short)immediate;

        // The N64 OS lazily switches floating-point contexts. A thread whose
        // Status.CU1 bit is clear must trap before *any* COP1 operation,
        // including floating-point loads and stores, so the exception handler
        // can save the previous owner and restore this thread's FPRs. Letting
        // the instruction execute here silently mixes registers across
        // preempted threads.
        if (opcode is 0x11 or 0x31 or 0x35 or 0x39 or 0x3D &&
            !Coprocessor1Usable)
        {
            EnterCoprocessorUnusable(1, instructionAddress);
            _registers[0] = 0;
            _executingDelaySlot = false;
            return;
        }

        switch (opcode)
        {
            case 0x00:
                ExecuteSpecial(
                    instruction,
                    rs,
                    rt,
                    (int)((instruction >> 11) & 31),
                    (int)((instruction >> 6) & 31),
                    instructionAddress);
                break;
            case 0x01:
                ExecuteRegImm(rt, rs, signedImmediate, instructionAddress);
                break;
            case 0x02:
                Branch(
                    (instructionAddress + 4 & 0xF0000000) | ((instruction & 0x03FFFFFF) << 2),
                    instructionAddress);
                break;
            case 0x03:
                WriteRegister(31, SignExtend32(instructionAddress + 8));
                Branch(
                    (instructionAddress + 4 & 0xF0000000) | ((instruction & 0x03FFFFFF) << 2),
                    instructionAddress);
                break;
            case 0x04:
                BranchIf(_registers[rs] == _registers[rt], signedImmediate, instructionAddress);
                break;
            case 0x05:
                BranchIf(_registers[rs] != _registers[rt], signedImmediate, instructionAddress);
                break;
            case 0x06:
                BranchIf((long)_registers[rs] <= 0, signedImmediate, instructionAddress);
                break;
            case 0x07:
                BranchIf((long)_registers[rs] > 0, signedImmediate, instructionAddress);
                break;
            case 0x08:
            case 0x09:
                WriteRegister(rt, SignExtend32(unchecked((uint)((int)_registers[rs] + signedImmediate))));
                break;
            case 0x0A:
                WriteRegister(rt, (long)_registers[rs] < signedImmediate ? 1u : 0u);
                break;
            case 0x0B:
                WriteRegister(rt, _registers[rs] < SignExtend64(signedImmediate) ? 1u : 0u);
                break;
            case 0x0C:
                WriteRegister(rt, _registers[rs] & immediate);
                break;
            case 0x0D:
                WriteRegister(rt, _registers[rs] | immediate);
                break;
            case 0x0E:
                WriteRegister(rt, _registers[rs] ^ immediate);
                break;
            case 0x0F:
                WriteRegister(rt, SignExtend32((uint)immediate << 16));
                break;
            case 0x10:
                ExecuteCoprocessor0(
                    instruction,
                    rs,
                    rt,
                    (int)((instruction >> 11) & 31));
                break;
            case 0x11:
                ExecuteCoprocessor1(
                    instruction,
                    rs,
                    rt,
                    (int)((instruction >> 11) & 31),
                    instructionAddress);
                break;
            case 0x14:
                BranchLikely(_registers[rs] == _registers[rt], signedImmediate, instructionAddress);
                break;
            case 0x15:
                BranchLikely(_registers[rs] != _registers[rt], signedImmediate, instructionAddress);
                break;
            case 0x16:
                BranchLikely((long)_registers[rs] <= 0, signedImmediate, instructionAddress);
                break;
            case 0x17:
                BranchLikely((long)_registers[rs] > 0, signedImmediate, instructionAddress);
                break;
            case 0x18:
            case 0x19:
                WriteRegister(rt, unchecked(_registers[rs] + (ulong)(long)signedImmediate));
                break;
            case 0x1A:
                LoadDoubleLeft(rt, EffectiveAddress(rs, signedImmediate), instructionAddress);
                break;
            case 0x1B:
                LoadDoubleRight(rt, EffectiveAddress(rs, signedImmediate), instructionAddress);
                break;
            case 0x20:
            {
                if (TryGetPhysicalAddress(EffectiveAddress(rs, signedImmediate), false, instructionAddress, out var physical))
                {
                    WriteRegister(rt, SignExtend64((sbyte)_memory.ReadBytePhysical(physical)));
                }

                break;
            }
            case 0x21:
            {
                if (TryGetPhysicalAddress(EffectiveAddress(rs, signedImmediate), false, instructionAddress, out var physical))
                {
                    WriteRegister(rt, SignExtend64((short)_memory.ReadUInt16Physical(physical)));
                }

                break;
            }
            case 0x22:
                LoadWordLeft(rt, EffectiveAddress(rs, signedImmediate), instructionAddress);
                break;
            case 0x23:
            {
                if (TryGetPhysicalAddress(EffectiveAddress(rs, signedImmediate), false, instructionAddress, out var physical))
                {
                    WriteRegister(rt, SignExtend32(_memory.ReadUInt32Physical(physical)));
                }

                break;
            }
            case 0x24:
            {
                if (TryGetPhysicalAddress(EffectiveAddress(rs, signedImmediate), false, instructionAddress, out var physical))
                {
                    WriteRegister(rt, _memory.ReadBytePhysical(physical));
                }

                break;
            }
            case 0x25:
            {
                if (TryGetPhysicalAddress(EffectiveAddress(rs, signedImmediate), false, instructionAddress, out var physical))
                {
                    WriteRegister(rt, _memory.ReadUInt16Physical(physical));
                }

                break;
            }
            case 0x26:
                LoadWordRight(rt, EffectiveAddress(rs, signedImmediate), instructionAddress);
                break;
            case 0x27:
            {
                if (TryGetPhysicalAddress(EffectiveAddress(rs, signedImmediate), false, instructionAddress, out var physical))
                {
                    WriteRegister(rt, _memory.ReadUInt32Physical(physical));
                }

                break;
            }
            case 0x28:
            {
                if (TryGetPhysicalAddress(EffectiveAddress(rs, signedImmediate), true, instructionAddress, out var physical))
                {
                    _memory.WriteBytePhysical(physical, (byte)_registers[rt]);
                }

                break;
            }
            case 0x29:
            {
                if (TryGetPhysicalAddress(EffectiveAddress(rs, signedImmediate), true, instructionAddress, out var physical))
                {
                    _memory.WriteUInt16Physical(physical, (ushort)_registers[rt]);
                }

                break;
            }
            case 0x2A:
                StoreWordLeft(rt, EffectiveAddress(rs, signedImmediate), instructionAddress);
                break;
            case 0x2B:
            {
                if (TryGetPhysicalAddress(EffectiveAddress(rs, signedImmediate), true, instructionAddress, out var physical))
                {
                    _memory.WriteUInt32Physical(physical, (uint)_registers[rt]);
                }

                break;
            }
            case 0x2C:
                StoreDoubleLeft(rt, EffectiveAddress(rs, signedImmediate), instructionAddress);
                break;
            case 0x2D:
                StoreDoubleRight(rt, EffectiveAddress(rs, signedImmediate), instructionAddress);
                break;
            case 0x2E:
                StoreWordRight(rt, EffectiveAddress(rs, signedImmediate), instructionAddress);
                break;
            case 0x2F:
                break; // CACHE is observable only through timing in the current interpreter.
            case 0x30:
            {
                if (TryGetPhysicalAddress(EffectiveAddress(rs, signedImmediate), false, instructionAddress, out var physical))
                {
                    WriteRegister(rt, SignExtend32(_memory.ReadUInt32Physical(physical)));
                }

                break;
            }
            case 0x31:
            {
                if (TryGetPhysicalAddress(EffectiveAddress(rs, signedImmediate), false, instructionAddress, out var physical))
                {
                    SetFprWord(rt, _memory.ReadUInt32Physical(physical));
                }

                break;
            }
            case 0x34:
            {
                if (TryGetPhysicalAddress(EffectiveAddress(rs, signedImmediate), false, instructionAddress, out var physical))
                {
                    WriteRegister(rt, _memory.ReadUInt64Physical(physical));
                }

                break;
            }
            case 0x35:
            {
                if (TryGetPhysicalAddress(EffectiveAddress(rs, signedImmediate), false, instructionAddress, out var physical))
                {
                    SetFprLong(rt, unchecked((long)_memory.ReadUInt64Physical(physical)));
                }

                break;
            }
            case 0x37:
            {
                if (TryGetPhysicalAddress(EffectiveAddress(rs, signedImmediate), false, instructionAddress, out var physical))
                {
                    WriteRegister(rt, _memory.ReadUInt64Physical(physical));
                }

                break;
            }
            case 0x38:
            {
                if (TryGetPhysicalAddress(EffectiveAddress(rs, signedImmediate), true, instructionAddress, out var physical))
                {
                    _memory.WriteUInt32Physical(physical, (uint)_registers[rt]);
                    WriteRegister(rt, 1);
                }

                break;
            }
            case 0x39:
            {
                if (TryGetPhysicalAddress(EffectiveAddress(rs, signedImmediate), true, instructionAddress, out var physical))
                {
                    _memory.WriteUInt32Physical(physical, GetFprWord(rt));
                }

                break;
            }
            case 0x3D:
            {
                if (TryGetPhysicalAddress(EffectiveAddress(rs, signedImmediate), true, instructionAddress, out var physical))
                {
                    _memory.WriteUInt64Physical(physical, unchecked((ulong)GetFprLong(rt)));
                }

                break;
            }
            case 0x3F:
            {
                if (TryGetPhysicalAddress(EffectiveAddress(rs, signedImmediate), true, instructionAddress, out var physical))
                {
                    _memory.WriteUInt64Physical(physical, _registers[rt]);
                }

                break;
            }
            default:
                Unsupported(instructionAddress, instruction);
                break;
        }

        _registers[0] = 0;
        _executingDelaySlot = false;
    }

    private void ExecuteSpecial(uint instruction, int rs, int rt, int rd, int shift, uint instructionAddress)
    {
        switch (instruction & 63)
        {
            case 0x00:
                WriteRegister(rd, SignExtend32((uint)_registers[rt] << shift));
                break;
            case 0x02:
                WriteRegister(rd, SignExtend32((uint)_registers[rt] >> shift));
                break;
            case 0x03:
                WriteRegister(rd, SignExtend32((uint)((int)_registers[rt] >> shift)));
                break;
            case 0x04:
                WriteRegister(rd, SignExtend32((uint)_registers[rt] << (int)(_registers[rs] & 31)));
                break;
            case 0x06:
                WriteRegister(rd, SignExtend32((uint)_registers[rt] >> (int)(_registers[rs] & 31)));
                break;
            case 0x07:
                WriteRegister(rd, SignExtend32((uint)((int)_registers[rt] >> (int)(_registers[rs] & 31))));
                break;
            case 0x08:
                Branch((uint)_registers[rs], instructionAddress);
                break;
            case 0x09:
                WriteRegister(rd == 0 ? 31 : rd, SignExtend32(instructionAddress + 8));
                Branch((uint)_registers[rs], instructionAddress);
                break;
            case 0x0C:
                EnterException(8, instructionAddress);
                break;
            case 0x0D:
                EnterException(9, instructionAddress);
                break;
            case 0x0F:
                break; // SYNC
            case 0x10:
                WriteRegister(rd, Hi);
                break;
            case 0x11:
                Hi = _registers[rs];
                break;
            case 0x12:
                WriteRegister(rd, Lo);
                break;
            case 0x13:
                Lo = _registers[rs];
                break;
            case 0x14:
                WriteRegister(rd, _registers[rt] << (int)(_registers[rs] & 63));
                break;
            case 0x16:
                WriteRegister(rd, _registers[rt] >> (int)(_registers[rs] & 63));
                break;
            case 0x17:
                WriteRegister(rd, (ulong)((long)_registers[rt] >> (int)(_registers[rs] & 63)));
                break;
            case 0x18:
            {
                var product = (long)(int)_registers[rs] * (long)(int)_registers[rt];
                Lo = SignExtend32((uint)product);
                Hi = SignExtend32((uint)(product >> 32));
                break;
            }
            case 0x19:
            {
                var product = (ulong)(uint)_registers[rs] * (uint)_registers[rt];
                Lo = SignExtend32((uint)product);
                Hi = SignExtend32((uint)(product >> 32));
                break;
            }
            case 0x1A:
                DivideWord((int)_registers[rs], (int)_registers[rt]);
                break;
            case 0x1B:
                DivideUnsignedWord((uint)_registers[rs], (uint)_registers[rt]);
                break;
            case 0x1C:
            {
                var product = (Int128)(long)_registers[rs] * (long)_registers[rt];
                Lo = (ulong)product;
                Hi = (ulong)(product >> 64);
                break;
            }
            case 0x1D:
            {
                var product = (UInt128)_registers[rs] * _registers[rt];
                Lo = (ulong)product;
                Hi = (ulong)(product >> 64);
                break;
            }
            case 0x1E:
                DivideDouble((long)_registers[rs], (long)_registers[rt]);
                break;
            case 0x1F:
                DivideUnsignedDouble(_registers[rs], _registers[rt]);
                break;
            case 0x20:
            case 0x21:
                WriteRegister(rd, SignExtend32(unchecked((uint)((int)_registers[rs] + (int)_registers[rt]))));
                break;
            case 0x22:
            case 0x23:
                WriteRegister(rd, SignExtend32(unchecked((uint)((int)_registers[rs] - (int)_registers[rt]))));
                break;
            case 0x24:
                WriteRegister(rd, _registers[rs] & _registers[rt]);
                break;
            case 0x25:
                WriteRegister(rd, _registers[rs] | _registers[rt]);
                break;
            case 0x26:
                WriteRegister(rd, _registers[rs] ^ _registers[rt]);
                break;
            case 0x27:
                WriteRegister(rd, ~(_registers[rs] | _registers[rt]));
                break;
            case 0x2A:
                WriteRegister(rd, (long)_registers[rs] < (long)_registers[rt] ? 1u : 0u);
                break;
            case 0x2B:
                WriteRegister(rd, _registers[rs] < _registers[rt] ? 1u : 0u);
                break;
            case 0x2C:
            case 0x2D:
                WriteRegister(rd, unchecked(_registers[rs] + _registers[rt]));
                break;
            case 0x2E:
            case 0x2F:
                WriteRegister(rd, unchecked(_registers[rs] - _registers[rt]));
                break;
            case 0x30:
                TrapIf((long)_registers[rs] >= (long)_registers[rt], instructionAddress);
                break;
            case 0x31:
                TrapIf(_registers[rs] >= _registers[rt], instructionAddress);
                break;
            case 0x32:
                TrapIf((long)_registers[rs] < (long)_registers[rt], instructionAddress);
                break;
            case 0x33:
                TrapIf(_registers[rs] < _registers[rt], instructionAddress);
                break;
            case 0x34:
                TrapIf(_registers[rs] == _registers[rt], instructionAddress);
                break;
            case 0x36:
                TrapIf(_registers[rs] != _registers[rt], instructionAddress);
                break;
            case 0x38:
                WriteRegister(rd, _registers[rt] << shift);
                break;
            case 0x3A:
                WriteRegister(rd, _registers[rt] >> shift);
                break;
            case 0x3B:
                WriteRegister(rd, (ulong)((long)_registers[rt] >> shift));
                break;
            case 0x3C:
                WriteRegister(rd, _registers[rt] << (shift + 32));
                break;
            case 0x3E:
                WriteRegister(rd, _registers[rt] >> (shift + 32));
                break;
            case 0x3F:
                WriteRegister(rd, (ulong)((long)_registers[rt] >> (shift + 32)));
                break;
            default:
                Unsupported(instructionAddress, instruction);
                break;
        }
    }

    private void ExecuteRegImm(int kind, int rs, short immediate, uint instructionAddress)
    {
        var signed = (long)_registers[rs];
        switch (kind)
        {
            case 0x00:
                BranchIf(signed < 0, immediate, instructionAddress);
                break;
            case 0x01:
                BranchIf(signed >= 0, immediate, instructionAddress);
                break;
            case 0x02:
                BranchLikely(signed < 0, immediate, instructionAddress);
                break;
            case 0x03:
                BranchLikely(signed >= 0, immediate, instructionAddress);
                break;
            case 0x10:
                WriteRegister(31, SignExtend32(instructionAddress + 8));
                BranchIf(signed < 0, immediate, instructionAddress);
                break;
            case 0x11:
                WriteRegister(31, SignExtend32(instructionAddress + 8));
                BranchIf(signed >= 0, immediate, instructionAddress);
                break;
            case 0x12:
                WriteRegister(31, SignExtend32(instructionAddress + 8));
                BranchLikely(signed < 0, immediate, instructionAddress);
                break;
            case 0x13:
                WriteRegister(31, SignExtend32(instructionAddress + 8));
                BranchLikely(signed >= 0, immediate, instructionAddress);
                break;
            case 0x08:
                TrapIf(signed >= immediate, instructionAddress);
                break;
            case 0x09:
                TrapIf(_registers[rs] >= SignExtend64(immediate), instructionAddress);
                break;
            case 0x0A:
                TrapIf(signed < immediate, instructionAddress);
                break;
            case 0x0B:
                TrapIf(_registers[rs] < SignExtend64(immediate), instructionAddress);
                break;
            case 0x0C:
                TrapIf(_registers[rs] == SignExtend64(immediate), instructionAddress);
                break;
            case 0x0E:
                TrapIf(_registers[rs] != SignExtend64(immediate), instructionAddress);
                break;
            default:
                Unsupported(instructionAddress, LastInstruction);
                break;
        }
    }

    private void ExecuteCoprocessor0(uint instruction, int operation, int rt, int rd)
    {
        switch (operation)
        {
            case 0x00:
                WriteRegister(rt, SignExtend32(_coprocessor0[rd]));
                break;
            case 0x01:
                WriteRegister(rt, _coprocessor0[rd]);
                break;
            case 0x04:
            case 0x05:
                _coprocessor0[rd] = (uint)_registers[rt];
                if (rd == Cp0Compare)
                {
                    _coprocessor0[Cp0Cause] &= ~(1u << 15);
                }
                else if (rd == Cp0EntryHi)
                {
                    _memory.SetTlbAsid((byte)_coprocessor0[Cp0EntryHi]);
                }
                else if (rd == Cp0Wired)
                {
                    _coprocessor0[Cp0Random] = 31;
                }

                break;
            case 0x10 when (instruction & 0x3F) == 0x01: // TLBR
            {
                var entry = _memory.ReadTlbEntry((int)_coprocessor0[Cp0Index]);
                _coprocessor0[Cp0PageMask] = entry.PageMask;
                _coprocessor0[Cp0EntryHi] = entry.EntryHi;
                _coprocessor0[Cp0EntryLo0] = entry.EntryLo0;
                _coprocessor0[Cp0EntryLo1] = entry.EntryLo1;
                _memory.SetTlbAsid((byte)entry.EntryHi);
                break;
            }
            case 0x10 when (instruction & 0x3F) == 0x02: // TLBWI
                WriteTlbEntry((int)_coprocessor0[Cp0Index]);
                break;
            case 0x10 when (instruction & 0x3F) == 0x06: // TLBWR
                WriteTlbEntry((int)_coprocessor0[Cp0Random]);
                break;
            case 0x10 when (instruction & 0x3F) == 0x08: // TLBP
            {
                var index = _memory.ProbeTlb(_coprocessor0[Cp0EntryHi]);
                _coprocessor0[Cp0Index] =
                    index >= 0 ? (uint)index : 0x80000000;
                break;
            }
            case 0x10 when (instruction & 0x3F) == 0x18:
                _coprocessor0[Cp0Status] &= ~2u;
                ProgramCounter = _coprocessor0[Cp0Epc];
                _nextProgramCounter = ProgramCounter + 4;
                _nextInstructionIsDelaySlot = false;
                break;
            default:
                if (operation == 0x10)
                {
                    return;
                }

                Unsupported(ProgramCounter - 4, instruction);
                break;
        }
    }

    private void WriteTlbEntry(int index)
    {
        _memory.WriteTlbEntry(
            index,
            _coprocessor0[Cp0PageMask],
            _coprocessor0[Cp0EntryHi],
            _coprocessor0[Cp0EntryLo0],
            _coprocessor0[Cp0EntryLo1]);
    }

    private void ExecuteCoprocessor1(uint instruction, int format, int rt, int rd, uint instructionAddress)
    {
        var function = (int)(instruction & 63);
        var fs = (int)((instruction >> 11) & 31);
        var fd = (int)((instruction >> 6) & 31);
        switch (format)
        {
            case 0x00:
                WriteRegister(rt, SignExtend32(GetFprWord(rd)));
                return;
            case 0x01:
                WriteRegister(rt, unchecked((ulong)GetFprLong(rd)));
                return;
            case 0x02:
                WriteRegister(rt, rd == 31 ? _coprocessor0[31] : 0);
                return;
            case 0x04:
                SetFprWord(rd, (uint)_registers[rt]);
                return;
            case 0x05:
                SetFprLong(rd, unchecked((long)_registers[rt]));
                return;
            case 0x06:
                if (rd == 31)
                {
                    _coprocessor0[31] = (uint)_registers[rt];
                }

                return;
            case 0x08:
            {
                var condition = (_coprocessor0[31] & (1u << 23)) != 0;
                var branchOnTrue = (rt & 1) != 0;
                var likely = (rt & 2) != 0;
                if (likely)
                {
                    BranchLikely(condition == branchOnTrue, (short)instruction, instructionAddress);
                }
                else
                {
                    BranchIf(condition == branchOnTrue, (short)instruction, instructionAddress);
                }

                return;
            }
        }

        if (format is not (0x10 or 0x11 or 0x14 or 0x15))
        {
            Unsupported(instructionAddress, instruction);
            return;
        }

        var ft = rt;
        if (format == 0x10)
        {
            var left = BitConverter.Int32BitsToSingle((int)GetFprWord(fs));
            var right = BitConverter.Int32BitsToSingle((int)GetFprWord(ft));
            ExecuteFloatingOperation(
                instructionAddress,
                instruction,
                function,
                fd,
                left,
                right,
                singlePrecision: true);
            return;
        }

        if (format == 0x11)
        {
            var left = BitConverter.Int64BitsToDouble(GetFprLong(fs));
            var right = BitConverter.Int64BitsToDouble(GetFprLong(ft));
            ExecuteFloatingOperation(
                instructionAddress,
                instruction,
                function,
                fd,
                left,
                right,
                singlePrecision: false);
            return;
        }

        var integer = format == 0x14
            ? (long)(int)GetFprWord(fs)
            : GetFprLong(fs);
        switch (function)
        {
            case 0x20:
                SetFprSingle(fd, integer);
                break;
            case 0x21:
                SetFprDouble(fd, integer);
                break;
            default:
                Unsupported(instructionAddress, instruction);
                break;
        }
    }

    private void ExecuteFloatingOperation(
        uint instructionAddress,
        uint instruction,
        int function,
        int destination,
        double left,
        double right,
        bool singlePrecision)
    {
        switch (function)
        {
            case 0x00:
                SetFloatingResult(destination, left + right, singlePrecision);
                break;
            case 0x01:
                SetFloatingResult(destination, left - right, singlePrecision);
                break;
            case 0x02:
                SetFloatingResult(destination, left * right, singlePrecision);
                break;
            case 0x03:
                SetFloatingResult(destination, left / right, singlePrecision);
                break;
            case 0x04:
                SetFloatingResult(destination, Math.Sqrt(left), singlePrecision);
                break;
            case 0x05:
                SetFloatingResult(destination, Math.Abs(left), singlePrecision);
                break;
            case 0x06:
                SetFloatingResult(destination, left, singlePrecision);
                break;
            case 0x07:
                SetFloatingResult(destination, -left, singlePrecision);
                break;
            case 0x08:
                SetFprLong(destination, RoundToInteger(left, FloatingRoundingMode.Nearest));
                break;
            case 0x09:
                SetFprLong(destination, RoundToInteger(left, FloatingRoundingMode.TowardZero));
                break;
            case 0x0A:
                SetFprLong(destination, RoundToInteger(left, FloatingRoundingMode.TowardPositiveInfinity));
                break;
            case 0x0B:
                SetFprLong(destination, RoundToInteger(left, FloatingRoundingMode.TowardNegativeInfinity));
                break;
            case 0x0C:
                SetFprWord(destination, unchecked((uint)(int)RoundToInteger(left, FloatingRoundingMode.Nearest)));
                break;
            case 0x0D:
                SetFprWord(destination, unchecked((uint)(int)RoundToInteger(left, FloatingRoundingMode.TowardZero)));
                break;
            case 0x0E:
                SetFprWord(
                    destination,
                    unchecked((uint)(int)RoundToInteger(left, FloatingRoundingMode.TowardPositiveInfinity)));
                break;
            case 0x0F:
                SetFprWord(
                    destination,
                    unchecked((uint)(int)RoundToInteger(left, FloatingRoundingMode.TowardNegativeInfinity)));
                break;
            case 0x20:
                SetFprSingle(destination, left);
                break;
            case 0x21:
                SetFprDouble(destination, left);
                break;
            case 0x24:
                SetFprWord(
                    destination,
                    unchecked((uint)(int)RoundToInteger(left, GetFloatingRoundingMode())));
                break;
            case 0x25:
                SetFprLong(destination, RoundToInteger(left, GetFloatingRoundingMode()));
                break;
            case >= 0x30:
            {
                var unordered = double.IsNaN(left) || double.IsNaN(right);
                var less = !unordered && left < right;
                var equal = !unordered && left == right;
                var result =
                    ((function & 1) != 0 && unordered) ||
                    ((function & 4) != 0 && less) ||
                    ((function & 2) != 0 && equal);
                _coprocessor0[31] = result
                    ? _coprocessor0[31] | (1u << 23)
                    : _coprocessor0[31] & ~(1u << 23);
                break;
            }
            default:
                Unsupported(instructionAddress, instruction);
                break;
        }
    }

    private void SetFloatingResult(int register, double value, bool singlePrecision)
    {
        if (singlePrecision)
        {
            SetFprSingle(register, value);
        }
        else
        {
            SetFprDouble(register, value);
        }
    }

    private void SetFprLong(int register, long value)
    {
        var bits = unchecked((ulong)value);
        _floatingRegisters[FullWidthFloatingRegisters ? register : register & ~1] = bits;
    }

    private long GetFprLong(int register)
        => unchecked((long)_floatingRegisters[
            FullWidthFloatingRegisters ? register : register & ~1]);

    private uint GetFprWord(int register)
    {
        if (FullWidthFloatingRegisters)
        {
            return (uint)_floatingRegisters[register];
        }

        var bits = _floatingRegisters[register & ~1];
        return (uint)(bits >> ((register & 1) * 32));
    }

    private bool FullWidthFloatingRegisters =>
        (_coprocessor0[Cp0Status] & 0x04000000) != 0;

    private FloatingRoundingMode GetFloatingRoundingMode() =>
        (FloatingRoundingMode)(_coprocessor0[31] & 3);

    private static long RoundToInteger(double value, FloatingRoundingMode mode)
    {
        if (double.IsNaN(value) || value >= long.MaxValue || value <= long.MinValue)
        {
            return long.MinValue;
        }

        return checked((long)(mode switch
        {
            FloatingRoundingMode.TowardZero => Math.Truncate(value),
            FloatingRoundingMode.TowardPositiveInfinity => Math.Ceiling(value),
            FloatingRoundingMode.TowardNegativeInfinity => Math.Floor(value),
            _ => Math.Round(value, MidpointRounding.ToEven)
        }));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BranchIf(bool condition, short immediate, uint instructionAddress)
    {
        MarkDelaySlot(instructionAddress);
        if (condition)
        {
            _nextProgramCounter =
                unchecked(instructionAddress + 4 + ((uint)(int)immediate << 2));
        }
    }

    private void BranchLikely(bool condition, short immediate, uint instructionAddress)
    {
        if (condition)
        {
            Branch(
                unchecked(instructionAddress + 4 + ((uint)(int)immediate << 2)),
                instructionAddress);
            return;
        }

        ProgramCounter = _nextProgramCounter;
        _nextProgramCounter += 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Branch(uint target, uint instructionAddress)
    {
        MarkDelaySlot(instructionAddress);
        _nextProgramCounter = target;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkDelaySlot(uint instructionAddress)
    {
        _nextInstructionIsDelaySlot = true;
        _nextDelaySlotBranchAddress = instructionAddress;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint EffectiveAddress(int register, short immediate) =>
        unchecked((uint)(_registers[register] + (ulong)(long)immediate));

    // The unaligned load/store pairs touch bytes within a single aligned
    // word or doubleword, so one translation covers every byte: TLB pages
    // are at least 4 KiB, and an aligned unit never straddles a page.

    private void LoadWordLeft(int register, uint address, uint instructionAddress)
    {
        if (!TryGetPhysicalAddress(address, isStore: false, instructionAddress, out var physical))
        {
            return;
        }

        var value = (uint)_registers[register];
        var offset = (int)(address & 3);
        for (var index = 0; index < 4 - offset; index++)
        {
            var shift = (3 - index) * 8;
            value = (value & ~(0xFFu << shift)) |
                    ((uint)_memory.ReadBytePhysical(physical + (uint)index) << shift);
        }

        WriteRegister(register, SignExtend32(value));
    }

    private void LoadWordRight(int register, uint address, uint instructionAddress)
    {
        if (!TryGetPhysicalAddress(address, isStore: false, instructionAddress, out var physical))
        {
            return;
        }

        var value = (uint)_registers[register];
        var offset = (int)(address & 3);
        var aligned = physical & ~3u;
        for (var index = 0; index <= offset; index++)
        {
            var targetByte = 3 - offset + index;
            var shift = (3 - targetByte) * 8;
            value = (value & ~(0xFFu << shift)) |
                    ((uint)_memory.ReadBytePhysical(aligned + (uint)index) << shift);
        }

        WriteRegister(register, SignExtend32(value));
    }

    private void StoreWordLeft(int register, uint address, uint instructionAddress)
    {
        if (!TryGetPhysicalAddress(address, isStore: true, instructionAddress, out var physical))
        {
            return;
        }

        var value = (uint)_registers[register];
        var offset = (int)(address & 3);
        for (var index = 0; index < 4 - offset; index++)
        {
            _memory.WriteBytePhysical(
                physical + (uint)index,
                (byte)(value >> ((3 - index) * 8)));
        }
    }

    private void StoreWordRight(int register, uint address, uint instructionAddress)
    {
        if (!TryGetPhysicalAddress(address, isStore: true, instructionAddress, out var physical))
        {
            return;
        }

        var value = (uint)_registers[register];
        var offset = (int)(address & 3);
        var aligned = physical & ~3u;
        for (var index = 0; index <= offset; index++)
        {
            var sourceByte = 3 - offset + index;
            _memory.WriteBytePhysical(
                aligned + (uint)index,
                (byte)(value >> ((3 - sourceByte) * 8)));
        }
    }

    private void LoadDoubleLeft(int register, uint address, uint instructionAddress)
    {
        if (!TryGetPhysicalAddress(address, isStore: false, instructionAddress, out var physical))
        {
            return;
        }

        var value = _registers[register];
        var offset = (int)(address & 7);
        for (var index = 0; index < 8 - offset; index++)
        {
            var shift = (7 - index) * 8;
            value = (value & ~(0xFFul << shift)) |
                    ((ulong)_memory.ReadBytePhysical(physical + (uint)index) << shift);
        }

        WriteRegister(register, value);
    }

    private void LoadDoubleRight(int register, uint address, uint instructionAddress)
    {
        if (!TryGetPhysicalAddress(address, isStore: false, instructionAddress, out var physical))
        {
            return;
        }

        var value = _registers[register];
        var offset = (int)(address & 7);
        var aligned = physical & ~7u;
        for (var index = 0; index <= offset; index++)
        {
            var targetByte = 7 - offset + index;
            var shift = (7 - targetByte) * 8;
            value = (value & ~(0xFFul << shift)) |
                    ((ulong)_memory.ReadBytePhysical(aligned + (uint)index) << shift);
        }

        WriteRegister(register, value);
    }

    private void StoreDoubleLeft(int register, uint address, uint instructionAddress)
    {
        if (!TryGetPhysicalAddress(address, isStore: true, instructionAddress, out var physical))
        {
            return;
        }

        var value = _registers[register];
        var offset = (int)(address & 7);
        for (var index = 0; index < 8 - offset; index++)
        {
            _memory.WriteBytePhysical(
                physical + (uint)index,
                (byte)(value >> ((7 - index) * 8)));
        }
    }

    private void StoreDoubleRight(int register, uint address, uint instructionAddress)
    {
        if (!TryGetPhysicalAddress(address, isStore: true, instructionAddress, out var physical))
        {
            return;
        }

        var value = _registers[register];
        var offset = (int)(address & 7);
        var aligned = physical & ~7u;
        for (var index = 0; index <= offset; index++)
        {
            var sourceByte = 7 - offset + index;
            _memory.WriteBytePhysical(
                aligned + (uint)index,
                (byte)(value >> ((7 - sourceByte) * 8)));
        }
    }

    private void DivideWord(int dividend, int divisor)
    {
        if (divisor == 0)
        {
            Lo = SignExtend32(dividend >= 0 ? uint.MaxValue : 1);
            Hi = SignExtend32((uint)dividend);
            return;
        }

        if (dividend == int.MinValue && divisor == -1)
        {
            Lo = SignExtend32(0x80000000);
            Hi = 0;
            return;
        }

        Lo = SignExtend32((uint)(dividend / divisor));
        Hi = SignExtend32((uint)(dividend % divisor));
    }

    private void DivideUnsignedWord(uint dividend, uint divisor)
    {
        Lo = SignExtend32(divisor == 0 ? uint.MaxValue : dividend / divisor);
        Hi = SignExtend32(divisor == 0 ? dividend : dividend % divisor);
    }

    private void DivideDouble(long dividend, long divisor)
    {
        if (divisor == 0)
        {
            Lo = dividend >= 0 ? ulong.MaxValue : 1;
            Hi = (ulong)dividend;
            return;
        }

        if (dividend == long.MinValue && divisor == -1)
        {
            Lo = 0x8000000000000000;
            Hi = 0;
            return;
        }

        Lo = (ulong)(dividend / divisor);
        Hi = (ulong)(dividend % divisor);
    }

    private void DivideUnsignedDouble(ulong dividend, ulong divisor)
    {
        Lo = divisor == 0 ? ulong.MaxValue : dividend / divisor;
        Hi = divisor == 0 ? dividend : dividend % divisor;
    }

    private void TrapIf(bool condition, uint instructionAddress)
    {
        if (condition)
        {
            EnterException(13, instructionAddress);
        }
    }

    private void EnterException(int code, uint instructionAddress) =>
        EnterExceptionAt(code, instructionAddress, 0x80000180);

    private void EnterExceptionAt(int code, uint instructionAddress, uint vector)
    {
        ExceptionsRaised++;
        if (code == 0)
        {
            InterruptExceptionsRaised++;
        }

        LastExceptionCode = code;
        LastExceptionAddress = instructionAddress;
        _coprocessor0[Cp0Cause] =
            (_coprocessor0[Cp0Cause] & ~(0x30000000u | 0x7Cu)) | ((uint)code << 2);
        if (_executingDelaySlot)
        {
            _coprocessor0[Cp0Cause] |= 1u << 31;
            _coprocessor0[Cp0Epc] = _executingDelaySlotBranchAddress;
        }
        else
        {
            _coprocessor0[Cp0Cause] &= ~(1u << 31);
            _coprocessor0[Cp0Epc] = instructionAddress;
        }

        _coprocessor0[Cp0Status] |= 2;
        ProgramCounter = vector;
        _nextProgramCounter = ProgramCounter + 4;
        _nextInstructionIsDelaySlot = false;
    }

    private void EnterCoprocessorUnusable(int coprocessor, uint instructionAddress)
    {
        EnterException(11, instructionAddress);
        _coprocessor0[Cp0Cause] |= ((uint)coprocessor & 3u) << 28;
    }

    /// <summary>
    /// Raises a TLB exception: BadVAddr, Context.BadVPN2, and EntryHi.VPN2
    /// describe the faulting page so the OS handler can install a mapping
    /// and retry. First-level refill misses vector to 0x80000000; a matched
    /// entry whose valid bit is clear (TLB Invalid) and any nested miss use
    /// the general vector at 0x80000180.
    /// </summary>
    private void EnterTlbException(
        uint badVirtualAddress,
        bool isStore,
        N64TlbFault fault,
        uint instructionAddress)
    {
        _coprocessor0[Cp0BadVAddr] = badVirtualAddress;
        _coprocessor0[Cp0Context] =
            (_coprocessor0[Cp0Context] & 0xFF800000) | ((badVirtualAddress >> 13) << 4);
        _coprocessor0[Cp0EntryHi] =
            (badVirtualAddress & 0xFFFFE000) | (_coprocessor0[Cp0EntryHi] & 0xFF);
        var useGeneralVector =
            fault == N64TlbFault.Invalid || (_coprocessor0[Cp0Status] & 2) != 0;
        EnterExceptionAt(
            isStore ? 3 : 2,
            instructionAddress,
            useGeneralVector ? 0x80000180u : 0x80000000u);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetPhysicalAddress(
        uint virtualAddress,
        bool isStore,
        uint instructionAddress,
        out uint physicalAddress)
    {
        if (virtualAddress - 0x80000000u <= 0x3FFFFFFFu)
        {
            physicalAddress = virtualAddress & 0x1FFFFFFFu;
            return true;
        }

        return TryGetMappedPhysicalAddress(
            virtualAddress,
            isStore,
            instructionAddress,
            out physicalAddress);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryGetMappedPhysicalAddress(
        uint virtualAddress,
        bool isStore,
        uint instructionAddress,
        out uint physicalAddress)
    {
        var fault = _memory.TranslateCpuAddress(virtualAddress, out physicalAddress);
        if (fault == N64TlbFault.None)
        {
            return true;
        }

        EnterTlbException(virtualAddress, isStore, fault, instructionAddress);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateInterruptLines()
    {
        if (_memory.CpuInterruptPending)
        {
            _coprocessor0[Cp0Cause] |= 1u << 10;
        }
        else
        {
            _coprocessor0[Cp0Cause] &= ~(1u << 10);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AdvanceClock()
    {
        _countSubCycle++;
        if (_countSubCycle >= CountPerOp)
        {
            _countSubCycle = 0;
            _coprocessor0[Cp0Count]++;
            if (_coprocessor0[Cp0Count] == _coprocessor0[Cp0Compare])
            {
                _coprocessor0[Cp0Cause] |= 1u << 15;
            }
        }

        // Random cycles down through [Wired, 31] so TLBWR spreads refills
        // across the unwired entries instead of thrashing a single slot.
        var random = _coprocessor0[Cp0Random];
        _coprocessor0[Cp0Random] = random <= (_coprocessor0[Cp0Wired] & 31)
            ? 31u
            : random - 1;

        return _memory.AdvanceCpuTick();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool InterruptsEnabled()
    {
        var status = _coprocessor0[Cp0Status];
        var cause = _coprocessor0[Cp0Cause];
        return (status & 1) != 0 &&
               (status & 2) == 0 &&
               (status & cause & 0xFF00) != 0;
    }

    private bool Coprocessor1Usable =>
        (_coprocessor0[Cp0Status] & 0x20000000u) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteRegister(int register, ulong value)
    {
        if (register != 0)
        {
            _registers[register] = value;
        }
    }

    private void SetFprWord(int register, uint value)
    {
        if (FullWidthFloatingRegisters)
        {
            _floatingRegisters[register] =
                (_floatingRegisters[register] & 0xFFFFFFFF00000000) | value;
            return;
        }

        var physicalRegister = register & ~1;
        var shift = (register & 1) * 32;
        var mask = 0xFFFFFFFFul << shift;
        _floatingRegisters[physicalRegister] =
            (_floatingRegisters[physicalRegister] & ~mask) | ((ulong)value << shift);
    }

    private void SetFprSingle(int register, double value) =>
        SetFprWord(register, (uint)BitConverter.SingleToInt32Bits((float)value));

    private void SetFprDouble(int register, double value) =>
        SetFprLong(register, BitConverter.DoubleToInt64Bits(value));

    private void Unsupported(uint address, uint instruction)
    {
        UnsupportedInstructionCount++;
        throw new NotSupportedException(
            $"Pixel64 does not implement R4300i instruction 0x{instruction:X8} at 0x{address:X8}.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SignExtend32(uint value) => (ulong)(long)(int)value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SignExtend64(short value) => (ulong)(long)value;

    private enum FloatingRoundingMode : uint
    {
        Nearest = 0,
        TowardZero = 1,
        TowardPositiveInfinity = 2,
        TowardNegativeInfinity = 3
    }
}
