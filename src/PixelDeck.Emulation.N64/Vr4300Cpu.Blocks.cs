namespace PixelDeck.Emulation.N64;

public sealed partial class Vr4300Cpu
{
    /// <summary>
    /// Accelerates the canonical libultra idle thread without skipping video
    /// fields or changing emulated time. Only an unconditional self-branch
    /// with a literal NOP delay slot qualifies, and the skip ends before the
    /// first device or timer event that could wake the CPU.
    /// </summary>
    internal int TrySkipIdleLoop(int maximumTicks)
    {
        if (maximumTicks < 2 ||
            _nextInstructionIsDelaySlot ||
            _executingDelaySlot ||
            LastInstruction != 0 ||
            ProgramCounter != _executingDelaySlotBranchAddress)
        {
            return 0;
        }

        UpdateInterruptLines();
        if (InterruptsEnabled() ||
            _memory.FetchInstruction(ProgramCounter, out var branch) != N64TlbFault.None ||
            _memory.FetchInstruction(ProgramCounter + sizeof(uint), out var delay) != N64TlbFault.None ||
            delay != 0)
        {
            return 0;
        }

        var isSelfBranch = branch == 0x1000FFFFu;
        if (!isSelfBranch && (branch >> 26) == 0x02)
        {
            var jumpTarget = ((ProgramCounter + sizeof(uint)) & 0xF0000000u) |
                             ((branch & 0x03FFFFFFu) << 2);
            isSelfBranch = jumpTarget == ProgramCounter;
        }

        if (!isSelfBranch)
        {
            return 0;
        }

        var ticks = Math.Min(maximumTicks, _memory.TicksUntilNextCpuEvent(maximumTicks));

        // CP0 Count advances on every emulated CPU instruction. Stop exactly
        // when Compare becomes equal so the timer interrupt is raised before
        // the next attempted idle-loop skip.
        if ((_coprocessor0[Cp0Cause] & (1u << 15)) == 0)
        {
            var timerDistance = unchecked(_coprocessor0[Cp0Compare] - _coprocessor0[Cp0Count]);
            if (timerDistance != 0 && timerDistance <= (uint)ticks)
            {
                ticks = (int)timerDistance;
            }
        }

        // One loop iteration is a branch and its delay slot. Keeping the bulk
        // advance even leaves the architectural PC/delay-slot state exactly
        // where it began.
        ticks &= ~1;
        if (ticks < 2)
        {
            return 0;
        }

        var oldCount = _coprocessor0[Cp0Count];
        _coprocessor0[Cp0Count] = unchecked(oldCount + (uint)ticks);
        var compareDistance = unchecked(_coprocessor0[Cp0Compare] - oldCount);
        if (compareDistance != 0 && compareDistance <= (uint)ticks)
        {
            _coprocessor0[Cp0Cause] |= 1u << 15;
        }

        var wired = _coprocessor0[Cp0Wired] & 31;
        var range = 32 - (int)wired;
        var random = _coprocessor0[Cp0Random] <= 31
            ? _coprocessor0[Cp0Random]
            : 31u;
        if (random < wired)
        {
            random = 31;
        }

        var offset = (int)(random - wired);
        var decrement = ticks % range;
        _coprocessor0[Cp0Random] = wired +
            (uint)((offset - decrement + range) % range);

        _memory.AdvanceCpuTicks(ticks);
        InstructionsExecuted += ticks;
        IdleInstructionsSkipped += ticks;
        _registers[0] = 0;
        return ticks;
    }

    private const int MaximumCachedBlockInstructions = 24;
    private const int MinimumCachedBlockInstructions = 3;
    private const int CachedBlockTableSize = 1 << 16;
    private const int CachedBlockTableMask = CachedBlockTableSize - 1;

    // A direct-mapped table is intentional here. The first implementation used
    // Dictionary lookups and did not remember PCs that could not begin a
    // block. Mario 64 consequently tried to rediscover a block after almost
    // every interpreted instruction; the lookup machinery cost substantially
    // more than the small number of cached operations saved. State 1 is a
    // negative entry and state 2 contains a block.
    private readonly CachedBasicBlock?[] _cachedBlocks = new CachedBasicBlock?[CachedBlockTableSize];
    private readonly uint[] _cachedBlockTags = new uint[CachedBlockTableSize];
    private readonly byte[] _cachedBlockStates = new byte[CachedBlockTableSize];
    private bool _hasPrefetchedInstruction;
    private uint _prefetchedInstructionAddress;
    private uint _prefetchedInstruction;

    public long CachedBlocksExecuted { get; private set; }

    public long CachedInstructionsExecuted { get; private set; }

    /// <summary>
    /// Executes a validated straight-line block of side-effect-free integer
    /// instructions. Control flow, memory, coprocessor, and exception-capable
    /// operations stay in <see cref="Step"/> until their compiled forms can
    /// preserve every architectural exit precisely.
    /// </summary>
    internal int RunCachedBlock(int maximumInstructions)
    {
        if (maximumInstructions <= 0)
        {
            return 0;
        }

        if (_nextInstructionIsDelaySlot || maximumInstructions < MinimumCachedBlockInstructions)
        {
            Step();
            return 1;
        }

        var blockAddress = ProgramCounter;
        var cacheIndex = (int)((blockAddress >> 2) & CachedBlockTableMask);
        var cacheState = _cachedBlockTags[cacheIndex] == blockAddress
            ? _cachedBlockStates[cacheIndex]
            : (byte)0;
        if (cacheState == 1)
        {
            return 0;
        }

        var block = cacheState == 2 ? _cachedBlocks[cacheIndex] : null;
        if (block is not null &&
            !_memory.MatchesRdramInstructions(blockAddress, block.RawInstructions))
        {
            block = null;
            cacheState = 0;
        }

        if (cacheState == 0)
        {
            block = BuildCachedBlock(blockAddress, out var firstInstruction);
            _cachedBlockTags[cacheIndex] = blockAddress;
            _cachedBlockStates[cacheIndex] = block is null ? (byte)1 : (byte)2;
            _cachedBlocks[cacheIndex] = block;
            if (block is null)
            {
                if (firstInstruction.HasValue)
                {
                    _hasPrefetchedInstruction = true;
                    _prefetchedInstructionAddress = blockAddress;
                    _prefetchedInstruction = firstInstruction.Value;
                }

                return 0;
            }
        }

        if (block is null)
        {
            return 0;
        }

        var count = Math.Min(maximumInstructions, block.Instructions.Length);
        var executed = 0;
        for (var index = 0; index < count; index++)
        {
            var instruction = block.Instructions[index];
            if (ProgramCounter != instruction.Address)
            {
                break;
            }

            var continueBlock = ExecuteCachedInstruction(instruction);
            executed++;
            if (!continueBlock)
            {
                break;
            }
        }

        if (executed > 0)
        {
            CachedBlocksExecuted++;
            CachedInstructionsExecuted += executed;
        }

        return executed;
    }

    private CachedBasicBlock? BuildCachedBlock(uint address, out uint? firstInstruction)
    {
        firstInstruction = null;
        if (address - 0x80000000u > 0x3FFFFFFFu ||
            (address & 0x1FFFFFFFu) >= N64Memory.RdramSize)
        {
            return null;
        }

        var instructions = new List<CachedInstruction>(MaximumCachedBlockInstructions);
        var rawInstructions = new List<uint>(MaximumCachedBlockInstructions);
        for (var index = 0; index < MaximumCachedBlockInstructions; index++)
        {
            var instructionAddress = unchecked(address + (uint)(index * sizeof(uint)));
            if ((instructionAddress & 0x1FFFFFFFu) > N64Memory.RdramSize - sizeof(uint) ||
                _memory.FetchInstruction(instructionAddress, out var raw) != N64TlbFault.None)
            {
                break;
            }

            firstInstruction ??= raw;
            if (!TryDecodeCachedInstruction(instructionAddress, raw, out var decoded))
            {
                break;
            }

            instructions.Add(decoded);
            rawInstructions.Add(raw);
            if (decoded.EndsBlock)
            {
                break;
            }
        }

        return instructions.Count >= MinimumCachedBlockInstructions
            ? new CachedBasicBlock(instructions.ToArray(), rawInstructions.ToArray())
            : null;
    }

    private bool ExecuteCachedInstruction(CachedInstruction instruction)
    {
        UpdateInterruptLines();
        if (InterruptsEnabled())
        {
            _executingDelaySlot = false;
            EnterException(0, ProgramCounter);
            InstructionsExecuted++;
            AdvanceClock();
            _registers[0] = 0;
            return false;
        }

        _executingDelaySlot = false;
        _executingDelaySlotBranchAddress = 0;
        _nextInstructionIsDelaySlot = false;
        LastInstruction = instruction.Raw;
        ProgramCounter = _nextProgramCounter;
        _nextProgramCounter += sizeof(uint);
        InstructionsExecuted++;
        var rdramChanged = AdvanceClock();

        var rs = instruction.Rs;
        var rt = instruction.Rt;
        var rd = instruction.Rd;
        var shift = instruction.Shift;
        switch (instruction.Kind)
        {
            case CachedOperation.Addiu:
                WriteRegister(
                    rt,
                    SignExtend32(unchecked((uint)((int)_registers[rs] + instruction.Immediate))));
                break;
            case CachedOperation.Daddiu:
                WriteRegister(rt, unchecked(_registers[rs] + (ulong)(long)instruction.Immediate));
                break;
            case CachedOperation.Slti:
                WriteRegister(rt, (long)_registers[rs] < instruction.Immediate ? 1u : 0u);
                break;
            case CachedOperation.Sltiu:
                WriteRegister(rt, _registers[rs] < SignExtend64(instruction.Immediate) ? 1u : 0u);
                break;
            case CachedOperation.Andi:
                WriteRegister(rt, _registers[rs] & instruction.UnsignedImmediate);
                break;
            case CachedOperation.Ori:
                WriteRegister(rt, _registers[rs] | instruction.UnsignedImmediate);
                break;
            case CachedOperation.Xori:
                WriteRegister(rt, _registers[rs] ^ instruction.UnsignedImmediate);
                break;
            case CachedOperation.Lui:
                WriteRegister(rt, SignExtend32((uint)instruction.UnsignedImmediate << 16));
                break;
            case CachedOperation.LoadDoubleLeft:
                LoadDoubleLeft(rt, EffectiveAddress(rs, instruction.Immediate), instruction.Address);
                break;
            case CachedOperation.LoadDoubleRight:
                LoadDoubleRight(rt, EffectiveAddress(rs, instruction.Immediate), instruction.Address);
                break;
            case CachedOperation.LoadByte:
                if (TryGetPhysicalAddress(
                        EffectiveAddress(rs, instruction.Immediate),
                        false,
                        instruction.Address,
                        out var loadBytePhysical))
                {
                    WriteRegister(rt, SignExtend64((sbyte)_memory.ReadBytePhysical(loadBytePhysical)));
                }

                break;
            case CachedOperation.LoadHalf:
                if (TryGetPhysicalAddress(
                        EffectiveAddress(rs, instruction.Immediate),
                        false,
                        instruction.Address,
                        out var loadHalfPhysical))
                {
                    WriteRegister(rt, SignExtend64((short)_memory.ReadUInt16Physical(loadHalfPhysical)));
                }

                break;
            case CachedOperation.LoadWordLeft:
                LoadWordLeft(rt, EffectiveAddress(rs, instruction.Immediate), instruction.Address);
                break;
            case CachedOperation.LoadWord:
            case CachedOperation.LoadLinked:
                if (TryGetPhysicalAddress(
                        EffectiveAddress(rs, instruction.Immediate),
                        false,
                        instruction.Address,
                        out var loadWordPhysical))
                {
                    WriteRegister(rt, SignExtend32(_memory.ReadUInt32Physical(loadWordPhysical)));
                }

                break;
            case CachedOperation.LoadByteUnsigned:
                if (TryGetPhysicalAddress(
                        EffectiveAddress(rs, instruction.Immediate),
                        false,
                        instruction.Address,
                        out var loadByteUnsignedPhysical))
                {
                    WriteRegister(rt, _memory.ReadBytePhysical(loadByteUnsignedPhysical));
                }

                break;
            case CachedOperation.LoadHalfUnsigned:
                if (TryGetPhysicalAddress(
                        EffectiveAddress(rs, instruction.Immediate),
                        false,
                        instruction.Address,
                        out var loadHalfUnsignedPhysical))
                {
                    WriteRegister(rt, _memory.ReadUInt16Physical(loadHalfUnsignedPhysical));
                }

                break;
            case CachedOperation.LoadWordRight:
                LoadWordRight(rt, EffectiveAddress(rs, instruction.Immediate), instruction.Address);
                break;
            case CachedOperation.LoadWordUnsigned:
                if (TryGetPhysicalAddress(
                        EffectiveAddress(rs, instruction.Immediate),
                        false,
                        instruction.Address,
                        out var loadWordUnsignedPhysical))
                {
                    WriteRegister(rt, _memory.ReadUInt32Physical(loadWordUnsignedPhysical));
                }

                break;
            case CachedOperation.LoadDouble:
                if (TryGetPhysicalAddress(
                        EffectiveAddress(rs, instruction.Immediate),
                        false,
                        instruction.Address,
                        out var loadDoublePhysical))
                {
                    WriteRegister(rt, _memory.ReadUInt64Physical(loadDoublePhysical));
                }

                break;
            case CachedOperation.StoreByte:
                if (TryGetPhysicalAddress(
                        EffectiveAddress(rs, instruction.Immediate),
                        true,
                        instruction.Address,
                        out var storeBytePhysical))
                {
                    _memory.WriteBytePhysical(storeBytePhysical, (byte)_registers[rt]);
                }

                break;
            case CachedOperation.StoreHalf:
                if (TryGetPhysicalAddress(
                        EffectiveAddress(rs, instruction.Immediate),
                        true,
                        instruction.Address,
                        out var storeHalfPhysical))
                {
                    _memory.WriteUInt16Physical(storeHalfPhysical, (ushort)_registers[rt]);
                }

                break;
            case CachedOperation.StoreWordLeft:
                StoreWordLeft(rt, EffectiveAddress(rs, instruction.Immediate), instruction.Address);
                break;
            case CachedOperation.StoreWord:
                if (TryGetPhysicalAddress(
                        EffectiveAddress(rs, instruction.Immediate),
                        true,
                        instruction.Address,
                        out var storeWordPhysical))
                {
                    _memory.WriteUInt32Physical(storeWordPhysical, (uint)_registers[rt]);
                }

                break;
            case CachedOperation.StoreDoubleLeft:
                StoreDoubleLeft(rt, EffectiveAddress(rs, instruction.Immediate), instruction.Address);
                break;
            case CachedOperation.StoreDoubleRight:
                StoreDoubleRight(rt, EffectiveAddress(rs, instruction.Immediate), instruction.Address);
                break;
            case CachedOperation.StoreWordRight:
                StoreWordRight(rt, EffectiveAddress(rs, instruction.Immediate), instruction.Address);
                break;
            case CachedOperation.Cache:
                break;
            case CachedOperation.StoreConditional:
                if (TryGetPhysicalAddress(
                        EffectiveAddress(rs, instruction.Immediate),
                        true,
                        instruction.Address,
                        out var storeConditionalPhysical))
                {
                    _memory.WriteUInt32Physical(storeConditionalPhysical, (uint)_registers[rt]);
                    WriteRegister(rt, 1);
                }

                break;
            case CachedOperation.StoreDouble:
                if (TryGetPhysicalAddress(
                        EffectiveAddress(rs, instruction.Immediate),
                        true,
                        instruction.Address,
                        out var storeDoublePhysical))
                {
                    _memory.WriteUInt64Physical(storeDoublePhysical, _registers[rt]);
                }

                break;
            case CachedOperation.Sll:
                WriteRegister(rd, SignExtend32((uint)_registers[rt] << shift));
                break;
            case CachedOperation.Srl:
                WriteRegister(rd, SignExtend32((uint)_registers[rt] >> shift));
                break;
            case CachedOperation.Sra:
                WriteRegister(rd, SignExtend32((uint)((int)_registers[rt] >> shift)));
                break;
            case CachedOperation.Sllv:
                WriteRegister(rd, SignExtend32((uint)_registers[rt] << (int)(_registers[rs] & 31)));
                break;
            case CachedOperation.Srlv:
                WriteRegister(rd, SignExtend32((uint)_registers[rt] >> (int)(_registers[rs] & 31)));
                break;
            case CachedOperation.Srav:
                WriteRegister(rd, SignExtend32((uint)((int)_registers[rt] >> (int)(_registers[rs] & 31))));
                break;
            case CachedOperation.Sync:
                break;
            case CachedOperation.Mfhi:
                WriteRegister(rd, Hi);
                break;
            case CachedOperation.Mthi:
                Hi = _registers[rs];
                break;
            case CachedOperation.Mflo:
                WriteRegister(rd, Lo);
                break;
            case CachedOperation.Mtlo:
                Lo = _registers[rs];
                break;
            case CachedOperation.Dsllv:
                WriteRegister(rd, _registers[rt] << (int)(_registers[rs] & 63));
                break;
            case CachedOperation.Dsrlv:
                WriteRegister(rd, _registers[rt] >> (int)(_registers[rs] & 63));
                break;
            case CachedOperation.Dsrav:
                WriteRegister(rd, (ulong)((long)_registers[rt] >> (int)(_registers[rs] & 63)));
                break;
            case CachedOperation.Mult:
            {
                var product = (long)(int)_registers[rs] * (long)(int)_registers[rt];
                Lo = SignExtend32((uint)product);
                Hi = SignExtend32((uint)(product >> 32));
                break;
            }
            case CachedOperation.Multu:
            {
                var product = (ulong)(uint)_registers[rs] * (uint)_registers[rt];
                Lo = SignExtend32((uint)product);
                Hi = SignExtend32((uint)(product >> 32));
                break;
            }
            case CachedOperation.Div:
                DivideWord((int)_registers[rs], (int)_registers[rt]);
                break;
            case CachedOperation.Divu:
                DivideUnsignedWord((uint)_registers[rs], (uint)_registers[rt]);
                break;
            case CachedOperation.Dmult:
            {
                var product = (Int128)(long)_registers[rs] * (long)_registers[rt];
                Lo = (ulong)product;
                Hi = (ulong)(product >> 64);
                break;
            }
            case CachedOperation.Dmultu:
            {
                var product = (UInt128)_registers[rs] * _registers[rt];
                Lo = (ulong)product;
                Hi = (ulong)(product >> 64);
                break;
            }
            case CachedOperation.Ddiv:
                DivideDouble((long)_registers[rs], (long)_registers[rt]);
                break;
            case CachedOperation.Ddivu:
                DivideUnsignedDouble(_registers[rs], _registers[rt]);
                break;
            case CachedOperation.Addu:
                WriteRegister(
                    rd,
                    SignExtend32(unchecked((uint)((int)_registers[rs] + (int)_registers[rt]))));
                break;
            case CachedOperation.Subu:
                WriteRegister(
                    rd,
                    SignExtend32(unchecked((uint)((int)_registers[rs] - (int)_registers[rt]))));
                break;
            case CachedOperation.And:
                WriteRegister(rd, _registers[rs] & _registers[rt]);
                break;
            case CachedOperation.Or:
                WriteRegister(rd, _registers[rs] | _registers[rt]);
                break;
            case CachedOperation.Xor:
                WriteRegister(rd, _registers[rs] ^ _registers[rt]);
                break;
            case CachedOperation.Nor:
                WriteRegister(rd, ~(_registers[rs] | _registers[rt]));
                break;
            case CachedOperation.Slt:
                WriteRegister(rd, (long)_registers[rs] < (long)_registers[rt] ? 1u : 0u);
                break;
            case CachedOperation.Sltu:
                WriteRegister(rd, _registers[rs] < _registers[rt] ? 1u : 0u);
                break;
            case CachedOperation.Daddu:
                WriteRegister(rd, unchecked(_registers[rs] + _registers[rt]));
                break;
            case CachedOperation.Dsubu:
                WriteRegister(rd, unchecked(_registers[rs] - _registers[rt]));
                break;
            case CachedOperation.Dsll:
                WriteRegister(rd, _registers[rt] << shift);
                break;
            case CachedOperation.Dsrl:
                WriteRegister(rd, _registers[rt] >> shift);
                break;
            case CachedOperation.Dsra:
                WriteRegister(rd, (ulong)((long)_registers[rt] >> shift));
                break;
            case CachedOperation.Dsll32:
                WriteRegister(rd, _registers[rt] << (shift + 32));
                break;
            case CachedOperation.Dsrl32:
                WriteRegister(rd, _registers[rt] >> (shift + 32));
                break;
            case CachedOperation.Dsra32:
                WriteRegister(rd, (ulong)((long)_registers[rt] >> (shift + 32)));
                break;
            default:
                throw new InvalidOperationException($"Unknown cached operation {instruction.Kind}.");
        }

        _registers[0] = 0;
        _executingDelaySlot = false;
        return !rdramChanged &&
               !instruction.EndsBlock &&
               ProgramCounter == instruction.Address + sizeof(uint);
    }

    private static bool TryDecodeCachedInstruction(
        uint address,
        uint raw,
        out CachedInstruction instruction)
    {
        var opcode = raw >> 26;
        var rs = (byte)((raw >> 21) & 31);
        var rt = (byte)((raw >> 16) & 31);
        var rd = (byte)((raw >> 11) & 31);
        var shift = (byte)((raw >> 6) & 31);
        var immediate = (short)raw;
        var endsBlock = false;
        CachedOperation? operation = opcode switch
        {
            0x08 or 0x09 => CachedOperation.Addiu,
            0x0A => CachedOperation.Slti,
            0x0B => CachedOperation.Sltiu,
            0x0C => CachedOperation.Andi,
            0x0D => CachedOperation.Ori,
            0x0E => CachedOperation.Xori,
            0x0F => CachedOperation.Lui,
            0x18 or 0x19 => CachedOperation.Daddiu,
            0x1A => CachedOperation.LoadDoubleLeft,
            0x1B => CachedOperation.LoadDoubleRight,
            0x20 => CachedOperation.LoadByte,
            0x21 => CachedOperation.LoadHalf,
            0x22 => CachedOperation.LoadWordLeft,
            0x23 => CachedOperation.LoadWord,
            0x24 => CachedOperation.LoadByteUnsigned,
            0x25 => CachedOperation.LoadHalfUnsigned,
            0x26 => CachedOperation.LoadWordRight,
            0x27 => CachedOperation.LoadWordUnsigned,
            0x28 => MarkBlockEnd(CachedOperation.StoreByte, ref endsBlock),
            0x29 => MarkBlockEnd(CachedOperation.StoreHalf, ref endsBlock),
            0x2A => MarkBlockEnd(CachedOperation.StoreWordLeft, ref endsBlock),
            0x2B => MarkBlockEnd(CachedOperation.StoreWord, ref endsBlock),
            0x2C => MarkBlockEnd(CachedOperation.StoreDoubleLeft, ref endsBlock),
            0x2D => MarkBlockEnd(CachedOperation.StoreDoubleRight, ref endsBlock),
            0x2E => MarkBlockEnd(CachedOperation.StoreWordRight, ref endsBlock),
            0x2F => CachedOperation.Cache,
            0x30 => CachedOperation.LoadLinked,
            0x34 or 0x37 => CachedOperation.LoadDouble,
            0x38 => MarkBlockEnd(CachedOperation.StoreConditional, ref endsBlock),
            0x3F => MarkBlockEnd(CachedOperation.StoreDouble, ref endsBlock),
            0x00 => (raw & 63) switch
            {
                0x00 => CachedOperation.Sll,
                0x02 => CachedOperation.Srl,
                0x03 => CachedOperation.Sra,
                0x04 => CachedOperation.Sllv,
                0x06 => CachedOperation.Srlv,
                0x07 => CachedOperation.Srav,
                0x0F => CachedOperation.Sync,
                0x10 => CachedOperation.Mfhi,
                0x11 => CachedOperation.Mthi,
                0x12 => CachedOperation.Mflo,
                0x13 => CachedOperation.Mtlo,
                0x14 => CachedOperation.Dsllv,
                0x16 => CachedOperation.Dsrlv,
                0x17 => CachedOperation.Dsrav,
                0x18 => CachedOperation.Mult,
                0x19 => CachedOperation.Multu,
                0x1A => CachedOperation.Div,
                0x1B => CachedOperation.Divu,
                0x1C => CachedOperation.Dmult,
                0x1D => CachedOperation.Dmultu,
                0x1E => CachedOperation.Ddiv,
                0x1F => CachedOperation.Ddivu,
                0x20 or 0x21 => CachedOperation.Addu,
                0x22 or 0x23 => CachedOperation.Subu,
                0x24 => CachedOperation.And,
                0x25 => CachedOperation.Or,
                0x26 => CachedOperation.Xor,
                0x27 => CachedOperation.Nor,
                0x2A => CachedOperation.Slt,
                0x2B => CachedOperation.Sltu,
                0x2C or 0x2D => CachedOperation.Daddu,
                0x2E or 0x2F => CachedOperation.Dsubu,
                0x38 => CachedOperation.Dsll,
                0x3A => CachedOperation.Dsrl,
                0x3B => CachedOperation.Dsra,
                0x3C => CachedOperation.Dsll32,
                0x3E => CachedOperation.Dsrl32,
                0x3F => CachedOperation.Dsra32,
                _ => null
            },
            _ => null
        };

        if (!operation.HasValue)
        {
            instruction = default;
            return false;
        }

        instruction = new CachedInstruction(
            address,
            raw,
            operation.Value,
            rs,
            rt,
            rd,
            shift,
            immediate,
            (ushort)raw,
            endsBlock);
        return true;
    }

    private static CachedOperation MarkBlockEnd(
        CachedOperation operation,
        ref bool endsBlock)
    {
        endsBlock = true;
        return operation;
    }

    private void ResetCachedBlocks()
    {
        Array.Clear(_cachedBlocks);
        Array.Clear(_cachedBlockTags);
        Array.Clear(_cachedBlockStates);
        _hasPrefetchedInstruction = false;
        CachedBlocksExecuted = 0;
        CachedInstructionsExecuted = 0;
    }

    private sealed record CachedBasicBlock(
        CachedInstruction[] Instructions,
        uint[] RawInstructions);

    private readonly record struct CachedInstruction(
        uint Address,
        uint Raw,
        CachedOperation Kind,
        byte Rs,
        byte Rt,
        byte Rd,
        byte Shift,
        short Immediate,
        ushort UnsignedImmediate,
        bool EndsBlock);

    private enum CachedOperation : byte
    {
        Addiu,
        Daddiu,
        Slti,
        Sltiu,
        Andi,
        Ori,
        Xori,
        Lui,
        LoadDoubleLeft,
        LoadDoubleRight,
        LoadByte,
        LoadHalf,
        LoadWordLeft,
        LoadWord,
        LoadByteUnsigned,
        LoadHalfUnsigned,
        LoadWordRight,
        LoadWordUnsigned,
        LoadDouble,
        StoreByte,
        StoreHalf,
        StoreWordLeft,
        StoreWord,
        StoreDoubleLeft,
        StoreDoubleRight,
        StoreWordRight,
        Cache,
        LoadLinked,
        StoreConditional,
        StoreDouble,
        Sll,
        Srl,
        Sra,
        Sllv,
        Srlv,
        Srav,
        Sync,
        Mfhi,
        Mthi,
        Mflo,
        Mtlo,
        Dsllv,
        Dsrlv,
        Dsrav,
        Mult,
        Multu,
        Div,
        Divu,
        Dmult,
        Dmultu,
        Ddiv,
        Ddivu,
        Addu,
        Subu,
        And,
        Or,
        Xor,
        Nor,
        Slt,
        Sltu,
        Daddu,
        Dsubu,
        Dsll,
        Dsrl,
        Dsra,
        Dsll32,
        Dsrl32,
        Dsra32
    }
}
