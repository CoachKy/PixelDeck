namespace PixelDeck.Emulation.Snes;

internal sealed class Cpu65816
{
    private const byte Carry = 0x01;
    private const byte Zero = 0x02;
    private const byte IrqDisable = 0x04;
    private const byte Decimal = 0x08;
    private const byte IndexWidth = 0x10;
    private const byte AccumulatorWidth = 0x20;
    private const byte Overflow = 0x40;
    private const byte Negative = 0x80;

    private readonly SnesBus _bus;
    private ushort _a;
    private ushort _x;
    private ushort _y;
    private ushort _stackPointer = 0x01FF;
    private ushort _directPage;
    private ushort _programCounter;
    private byte _programBank;
    private byte _dataBank;
    private byte _status = IrqDisable | AccumulatorWidth | IndexWidth;
    private bool _emulation = true;
    private bool _waiting;
    private bool _stopped;
    private ushort _resetVector;
    private bool _hasLeftResetVector;

    public Cpu65816(SnesBus bus)
    {
        _bus = bus;
    }

    public long TotalCycles { get; private set; }

    public uint ProgramAddress => ((uint)_programBank << 16) | _programCounter;

    public ushort Accumulator => _a;

    public ushort X => _x;

    public ushort Y => _y;

    public ushort DirectPage => _directPage;

    public byte DataBank => _dataBank;

    public byte Status => _status;

    public long NmiCount { get; private set; }

    public long IrqCount { get; private set; }

    public long BrkCount { get; private set; }

    public long CopCount { get; private set; }

    public long ResetVectorReentryCount { get; private set; }

    public uint FirstBrkAddress { get; private set; } = uint.MaxValue;

    public uint LastBrkAddress { get; private set; } = uint.MaxValue;

    public void Reset()
    {
        _a = 0;
        _x = 0;
        _y = 0;
        _stackPointer = 0x01FF;
        _directPage = 0;
        _programBank = 0;
        _dataBank = 0;
        _status = IrqDisable | AccumulatorWidth | IndexWidth;
        _emulation = true;
        _waiting = false;
        _stopped = false;
        _resetVector = ReadWord(0x00FFFC);
        _programCounter = _resetVector;
        _hasLeftResetVector = false;
        TotalCycles = 0;
        NmiCount = 0;
        IrqCount = 0;
        BrkCount = 0;
        CopCount = 0;
        ResetVectorReentryCount = 0;
        FirstBrkAddress = uint.MaxValue;
        LastBrkAddress = uint.MaxValue;
    }

    public int Step()
    {
        if (_bus.ConsumeNmi())
        {
            NmiCount++;
            _waiting = false;
            ServiceInterrupt(_emulation ? (ushort)0xFFFA : (ushort)0xFFEA, software: false);
            return Finish(7);
        }

        if (_bus.IrqPending && !GetFlag(IrqDisable))
        {
            IrqCount++;
            _waiting = false;
            ServiceInterrupt(_emulation ? (ushort)0xFFFE : (ushort)0xFFEE, software: false);
            return Finish(7);
        }

        if (_stopped || _waiting)
        {
            return Finish(1);
        }

        if (_programBank == 0 && _programCounter == _resetVector)
        {
            if (_hasLeftResetVector)
            {
                ResetVectorReentryCount++;
            }
        }
        else
        {
            _hasLeftResetVector = true;
        }

        var opcode = FetchByte();
        var low = opcode & 0x1F;
        if (IsAccumulatorGroupAddress(low) && (opcode & 0xE0) is 0x00 or 0x20 or 0x40 or 0x60 or 0x80 or 0xA0 or 0xC0 or 0xE0)
        {
            if ((opcode & 0xE0) != 0x80 || low != 0x09)
            {
                ExecuteAccumulatorGroup(opcode, low);
                return Finish(4);
            }
        }

        switch (opcode)
        {
            case 0x00: // BRK
                LastBrkAddress = ((uint)_programBank << 16) | (ushort)(_programCounter - 1);
                if (FirstBrkAddress == uint.MaxValue)
                {
                    FirstBrkAddress = LastBrkAddress;
                }

                BrkCount++;
                FetchByte();
                ServiceInterrupt(_emulation ? (ushort)0xFFFE : (ushort)0xFFE6, software: true);
                break;
            case 0x02: // COP
                CopCount++;
                FetchByte();
                ServiceInterrupt(_emulation ? (ushort)0xFFF4 : (ushort)0xFFE4, software: true);
                break;
            case 0x04: TestAndSetBits(AddressDirect()); break;
            case 0x06: ShiftMemory(AddressDirect(), ShiftKind.Asl); break;
            case 0x08: PushByte(_status); break;
            case 0x0A: ShiftAccumulator(ShiftKind.Asl); break;
            case 0x0B: PushWord(_directPage); break;
            case 0x0C: TestAndSetBits(AddressAbsolute()); break;
            case 0x0E: ShiftMemory(AddressAbsolute(), ShiftKind.Asl); break;
            case 0x10: Branch(!GetFlag(Negative)); break;
            case 0x14: TestAndResetBits(AddressDirect()); break;
            case 0x16: ShiftMemory(AddressDirectX(), ShiftKind.Asl); break;
            case 0x18: SetFlag(Carry, false); break;
            case 0x1A: IncrementAccumulator(1); break;
            case 0x1B: SetStackPointer(_a); break;
            case 0x1C: TestAndResetBits(AddressAbsolute()); break;
            case 0x1E: ShiftMemory(AddressAbsoluteX(), ShiftKind.Asl); break;

            case 0x20:
            {
                var target = FetchWord();
                PushWord((ushort)(_programCounter - 1));
                _programCounter = target;
                break;
            }
            case 0x22:
            {
                var target = FetchLong();
                PushByte(_programBank);
                PushWord((ushort)(_programCounter - 1));
                _programBank = (byte)(target >> 16);
                _programCounter = (ushort)target;
                break;
            }
            case 0x24: Bit(ReadValue(AddressDirect(), AccumulatorIs8Bit), immediate: false); break;
            case 0x26: ShiftMemory(AddressDirect(), ShiftKind.Rol); break;
            case 0x28: SetStatus(PopByte()); break;
            case 0x2A: ShiftAccumulator(ShiftKind.Rol); break;
            case 0x2B:
                _directPage = PopWord();
                SetNegativeZero(_directPage, is8Bit: false);
                break;
            case 0x2C: Bit(ReadValue(AddressAbsolute(), AccumulatorIs8Bit), immediate: false); break;
            case 0x2E: ShiftMemory(AddressAbsolute(), ShiftKind.Rol); break;
            case 0x30: Branch(GetFlag(Negative)); break;
            case 0x34: Bit(ReadValue(AddressDirectX(), AccumulatorIs8Bit), immediate: false); break;
            case 0x36: ShiftMemory(AddressDirectX(), ShiftKind.Rol); break;
            case 0x38: SetFlag(Carry, true); break;
            case 0x3A: IncrementAccumulator(-1); break;
            case 0x3B:
                _a = _stackPointer;
                SetNegativeZero(_a, is8Bit: false);
                break;
            case 0x3C: Bit(ReadValue(AddressAbsoluteX(), AccumulatorIs8Bit), immediate: false); break;
            case 0x3E: ShiftMemory(AddressAbsoluteX(), ShiftKind.Rol); break;

            case 0x40:
                SetStatus(PopByte());
                _programCounter = PopWord();
                if (!_emulation) _programBank = PopByte();
                break;
            case 0x42: FetchByte(); break; // WDM
            case 0x44: BlockMove(increment: false); break;
            case 0x46: ShiftMemory(AddressDirect(), ShiftKind.Lsr); break;
            case 0x48: PushAccumulator(); break;
            case 0x4A: ShiftAccumulator(ShiftKind.Lsr); break;
            case 0x4B: PushByte(_programBank); break;
            case 0x4C: _programCounter = FetchWord(); break;
            case 0x4E: ShiftMemory(AddressAbsolute(), ShiftKind.Lsr); break;
            case 0x50: Branch(!GetFlag(Overflow)); break;
            case 0x54: BlockMove(increment: true); break;
            case 0x56: ShiftMemory(AddressDirectX(), ShiftKind.Lsr); break;
            case 0x58: SetFlag(IrqDisable, false); break;
            case 0x5A: PushIndex(_y); break;
            case 0x5B:
                _directPage = _a;
                SetNegativeZero(_directPage, is8Bit: false);
                break;
            case 0x5C:
            {
                var target = FetchLong();
                _programBank = (byte)(target >> 16);
                _programCounter = (ushort)target;
                break;
            }
            case 0x5E: ShiftMemory(AddressAbsoluteX(), ShiftKind.Lsr); break;

            case 0x60:
                _programCounter = (ushort)(PopWord() + 1);
                break;
            case 0x62:
            {
                var displacement = (short)FetchWord();
                PushWord((ushort)(_programCounter + displacement));
                break;
            }
            case 0x64: WriteValue(AddressDirect(), 0, AccumulatorIs8Bit); break;
            case 0x66: ShiftMemory(AddressDirect(), ShiftKind.Ror); break;
            case 0x68: PullAccumulator(); break;
            case 0x6A: ShiftAccumulator(ShiftKind.Ror); break;
            case 0x6B:
                _programCounter = (ushort)(PopWord() + 1);
                _programBank = PopByte();
                break;
            case 0x6C:
            {
                var pointer = FetchWord();
                _programCounter = ReadWord(pointer);
                break;
            }
            case 0x6E: ShiftMemory(AddressAbsolute(), ShiftKind.Ror); break;
            case 0x70: Branch(GetFlag(Overflow)); break;
            case 0x74: WriteValue(AddressDirectX(), 0, AccumulatorIs8Bit); break;
            case 0x76: ShiftMemory(AddressDirectX(), ShiftKind.Ror); break;
            case 0x78: SetFlag(IrqDisable, true); break;
            case 0x7A:
                _y = PopIndex();
                SetNegativeZero(_y, IndexIs8Bit);
                break;
            case 0x7B:
                _a = _directPage;
                SetNegativeZero(_a, is8Bit: false);
                break;
            case 0x7C:
            {
                var pointer = (ushort)(FetchWord() + _x);
                _programCounter = ReadWord(((uint)_programBank << 16) | pointer);
                break;
            }
            case 0x7E: ShiftMemory(AddressAbsoluteX(), ShiftKind.Ror); break;

            case 0x80: Branch(always: true); break;
            case 0x82:
            {
                var displacement = (short)FetchWord();
                _programCounter = (ushort)(_programCounter + displacement);
                break;
            }
            case 0x84: WriteValue(AddressDirect(), _y, IndexIs8Bit); break;
            case 0x86: WriteValue(AddressDirect(), _x, IndexIs8Bit); break;
            case 0x88:
                _y = MaskIndex(_y - 1);
                SetNegativeZero(_y, IndexIs8Bit);
                break;
            case 0x89:
                Bit(FetchValue(AccumulatorIs8Bit), immediate: true);
                break;
            case 0x8A: TransferToAccumulator(_x); break;
            case 0x8B: PushByte(_dataBank); break;
            case 0x8C: WriteValue(AddressAbsolute(), _y, IndexIs8Bit); break;
            case 0x8E: WriteValue(AddressAbsolute(), _x, IndexIs8Bit); break;
            case 0x90: Branch(!GetFlag(Carry)); break;
            case 0x94: WriteValue(AddressDirectX(), _y, IndexIs8Bit); break;
            case 0x96: WriteValue(AddressDirectY(), _x, IndexIs8Bit); break;
            case 0x98: TransferToAccumulator(_y); break;
            case 0x9A: SetStackPointer(_x); break;
            case 0x9B:
                _y = MaskIndex(_x);
                SetNegativeZero(_y, IndexIs8Bit);
                break;
            case 0x9C: WriteValue(AddressAbsolute(), 0, AccumulatorIs8Bit); break;
            case 0x9E: WriteValue(AddressAbsoluteX(), 0, AccumulatorIs8Bit); break;

            case 0xA0: LoadIndex(ref _y, FetchValue(IndexIs8Bit)); break;
            case 0xA2: LoadIndex(ref _x, FetchValue(IndexIs8Bit)); break;
            case 0xA4: LoadIndex(ref _y, ReadValue(AddressDirect(), IndexIs8Bit)); break;
            case 0xA6: LoadIndex(ref _x, ReadValue(AddressDirect(), IndexIs8Bit)); break;
            case 0xA8:
                // TAX/TAY use the full hidden 16-bit C accumulator whenever the
                // destination index register is 16-bit, even while M is set.
                _y = MaskIndex(_a);
                SetNegativeZero(_y, IndexIs8Bit);
                break;
            case 0xAA:
                _x = MaskIndex(_a);
                SetNegativeZero(_x, IndexIs8Bit);
                break;
            case 0xAB:
                _dataBank = PopByte();
                SetNegativeZero(_dataBank, is8Bit: true);
                break;
            case 0xAC: LoadIndex(ref _y, ReadValue(AddressAbsolute(), IndexIs8Bit)); break;
            case 0xAE: LoadIndex(ref _x, ReadValue(AddressAbsolute(), IndexIs8Bit)); break;
            case 0xB0: Branch(GetFlag(Carry)); break;
            case 0xB4: LoadIndex(ref _y, ReadValue(AddressDirectX(), IndexIs8Bit)); break;
            case 0xB6: LoadIndex(ref _x, ReadValue(AddressDirectY(), IndexIs8Bit)); break;
            case 0xB8: SetFlag(Overflow, false); break;
            case 0xBA:
                _x = MaskIndex(_stackPointer);
                SetNegativeZero(_x, IndexIs8Bit);
                break;
            case 0xBB:
                _x = MaskIndex(_y);
                SetNegativeZero(_x, IndexIs8Bit);
                break;
            case 0xBC: LoadIndex(ref _y, ReadValue(AddressAbsoluteX(), IndexIs8Bit)); break;
            case 0xBE: LoadIndex(ref _x, ReadValue(AddressAbsoluteY(), IndexIs8Bit)); break;

            case 0xC0: Compare(_y, FetchValue(IndexIs8Bit), IndexIs8Bit); break;
            case 0xC2: SetStatus((byte)(_status & ~FetchByte())); break;
            case 0xC4: Compare(_y, ReadValue(AddressDirect(), IndexIs8Bit), IndexIs8Bit); break;
            case 0xC6: IncrementMemory(AddressDirect(), -1); break;
            case 0xC8:
                _y = MaskIndex(_y + 1);
                SetNegativeZero(_y, IndexIs8Bit);
                break;
            case 0xCA:
                _x = MaskIndex(_x - 1);
                SetNegativeZero(_x, IndexIs8Bit);
                break;
            case 0xCB: _waiting = true; break;
            case 0xCC: Compare(_y, ReadValue(AddressAbsolute(), IndexIs8Bit), IndexIs8Bit); break;
            case 0xCE: IncrementMemory(AddressAbsolute(), -1); break;
            case 0xD0: Branch(!GetFlag(Zero)); break;
            case 0xD4:
            {
                var pointer = (ushort)(_directPage + FetchByte());
                PushWord(ReadWord(pointer));
                break;
            }
            case 0xD6: IncrementMemory(AddressDirectX(), -1); break;
            case 0xD8: SetFlag(Decimal, false); break;
            case 0xDA: PushIndex(_x); break;
            case 0xDB: _stopped = true; break;
            case 0xDC:
            {
                var pointer = FetchWord();
                var target = ReadLong(pointer);
                _programBank = (byte)(target >> 16);
                _programCounter = (ushort)target;
                break;
            }
            case 0xDE: IncrementMemory(AddressAbsoluteX(), -1); break;

            case 0xE0: Compare(_x, FetchValue(IndexIs8Bit), IndexIs8Bit); break;
            case 0xE2: SetStatus((byte)(_status | FetchByte())); break;
            case 0xE4: Compare(_x, ReadValue(AddressDirect(), IndexIs8Bit), IndexIs8Bit); break;
            case 0xE6: IncrementMemory(AddressDirect(), 1); break;
            case 0xE8:
                _x = MaskIndex(_x + 1);
                SetNegativeZero(_x, IndexIs8Bit);
                break;
            case 0xEA: break;
            case 0xEB:
                _a = (ushort)((_a << 8) | (_a >> 8));
                SetNegativeZero((byte)_a, is8Bit: true);
                break;
            case 0xEC: Compare(_x, ReadValue(AddressAbsolute(), IndexIs8Bit), IndexIs8Bit); break;
            case 0xEE: IncrementMemory(AddressAbsolute(), 1); break;
            case 0xF0: Branch(GetFlag(Zero)); break;
            case 0xF4: PushWord(FetchWord()); break;
            case 0xF6: IncrementMemory(AddressDirectX(), 1); break;
            case 0xF8: SetFlag(Decimal, true); break;
            case 0xFA:
                _x = PopIndex();
                SetNegativeZero(_x, IndexIs8Bit);
                break;
            case 0xFB:
            {
                var oldCarry = GetFlag(Carry);
                SetFlag(Carry, _emulation);
                _emulation = oldCarry;
                if (_emulation)
                {
                    _status |= AccumulatorWidth | IndexWidth;
                    _x &= 0x00FF;
                    _y &= 0x00FF;
                    _stackPointer = (ushort)(0x0100 | (byte)_stackPointer);
                }
                break;
            }
            case 0xFC:
            {
                var pointer = (ushort)(FetchWord() + _x);
                PushWord((ushort)(_programCounter - 1));
                _programCounter = ReadWord(((uint)_programBank << 16) | pointer);
                break;
            }
            case 0xFE: IncrementMemory(AddressAbsoluteX(), 1); break;
            default:
                throw new InvalidOperationException($"Unsupported 65C816 opcode ${opcode:X2} at ${_programBank:X2}:{(ushort)(_programCounter - 1):X4}.");
        }

        return Finish(3);
    }

    internal void SaveState(BinaryWriter writer)
    {
        writer.Write(_a);
        writer.Write(_x);
        writer.Write(_y);
        writer.Write(_stackPointer);
        writer.Write(_directPage);
        writer.Write(_programCounter);
        writer.Write(_programBank);
        writer.Write(_dataBank);
        writer.Write(_status);
        writer.Write(_emulation);
        writer.Write(_waiting);
        writer.Write(_stopped);
        writer.Write(TotalCycles);
    }

    internal void LoadState(BinaryReader reader)
    {
        _a = reader.ReadUInt16();
        _x = reader.ReadUInt16();
        _y = reader.ReadUInt16();
        _stackPointer = reader.ReadUInt16();
        _directPage = reader.ReadUInt16();
        _programCounter = reader.ReadUInt16();
        _programBank = reader.ReadByte();
        _dataBank = reader.ReadByte();
        _status = reader.ReadByte();
        _emulation = reader.ReadBoolean();
        _waiting = reader.ReadBoolean();
        _stopped = reader.ReadBoolean();
        TotalCycles = reader.ReadInt64();
        EnforceWidths();
    }

    private bool AccumulatorIs8Bit => (_status & AccumulatorWidth) != 0;

    private bool IndexIs8Bit => (_status & IndexWidth) != 0;

    private void ExecuteAccumulatorGroup(byte opcode, int low)
    {
        var operation = opcode & 0xE0;
        var is8Bit = AccumulatorIs8Bit;
        if (operation == 0x80)
        {
            WriteValue(ResolveAccumulatorGroupAddress(low), GetAccumulator(), is8Bit);
            return;
        }

        var operand = low == 0x09
            ? FetchValue(is8Bit)
            : ReadValue(ResolveAccumulatorGroupAddress(low), is8Bit);
        var accumulator = GetAccumulator();
        var mask = is8Bit ? 0x00FF : 0xFFFF;

        switch (operation)
        {
            case 0x00:
                SetAccumulator(accumulator | operand);
                SetNegativeZero(GetAccumulator(), is8Bit);
                break;
            case 0x20:
                SetAccumulator(accumulator & operand);
                SetNegativeZero(GetAccumulator(), is8Bit);
                break;
            case 0x40:
                SetAccumulator(accumulator ^ operand);
                SetNegativeZero(GetAccumulator(), is8Bit);
                break;
            case 0x60:
                SetAccumulator(AddWithCarry(accumulator, operand, is8Bit, subtract: false));
                break;
            case 0xA0:
                SetAccumulator(operand);
                SetNegativeZero(GetAccumulator(), is8Bit);
                break;
            case 0xC0:
                Compare(accumulator, operand, is8Bit);
                break;
            case 0xE0:
                SetAccumulator(AddWithCarry(accumulator, operand, is8Bit, subtract: true));
                break;
        }

        _a &= (ushort)(AccumulatorIs8Bit ? 0xFFFF : mask);
    }

    private uint ResolveAccumulatorGroupAddress(int low)
    {
        switch (low)
        {
            case 0x01:
            {
                var operand = FetchByte();
                var pointer = (ushort)(_directPage + operand + _x);
                return ((uint)_dataBank << 16) | ReadWord(pointer);
            }
            case 0x03:
                return (ushort)(_stackPointer + FetchByte());
            case 0x05:
                return AddressDirect();
            case 0x07:
                return ReadLong((ushort)(_directPage + FetchByte()));
            case 0x0D:
                return AddressAbsolute();
            case 0x0F:
                return FetchLong();
            case 0x11:
            {
                var pointer = ReadWord((ushort)(_directPage + FetchByte()));
                return (((uint)_dataBank << 16) + pointer + _y) & 0xFFFFFF;
            }
            case 0x12:
                return ((uint)_dataBank << 16) | ReadWord((ushort)(_directPage + FetchByte()));
            case 0x13:
            {
                var pointer = ReadWord((ushort)(_stackPointer + FetchByte()));
                return (((uint)_dataBank << 16) + pointer + _y) & 0xFFFFFF;
            }
            case 0x15:
                return AddressDirectX();
            case 0x17:
                return (ReadLong((ushort)(_directPage + FetchByte())) + _y) & 0xFFFFFF;
            case 0x19:
                return AddressAbsoluteY();
            case 0x1D:
                return AddressAbsoluteX();
            case 0x1F:
                return (FetchLong() + _x) & 0xFFFFFF;
            default:
                throw new InvalidOperationException($"Invalid accumulator-group addressing code ${low:X2}.");
        }
    }

    private ushort AddWithCarry(ushort left, ushort right, bool is8Bit, bool subtract)
    {
        var mask = is8Bit ? 0xFF : 0xFFFF;
        var sign = is8Bit ? 0x80 : 0x8000;
        var carryIn = GetFlag(Carry) ? 1 : 0;
        var binaryRight = subtract ? right ^ mask : right;
        var binary = (left & mask) + (binaryRight & mask) + carryIn;
        var binaryResult = binary & mask;
        SetFlag(Overflow, ((left ^ binaryResult) & (binaryRight ^ binaryResult) & sign) != 0);

        int result;
        bool carryOut;
        if (!GetFlag(Decimal))
        {
            result = binaryResult;
            carryOut = binary > mask;
        }
        else if (!subtract)
        {
            result = AddBcd(left, right, carryIn, is8Bit, out carryOut);
        }
        else
        {
            result = SubtractBcd(left, right, carryIn, is8Bit, out carryOut);
        }

        SetFlag(Carry, carryOut);
        SetNegativeZero((ushort)result, is8Bit);
        return (ushort)result;
    }

    private static int AddBcd(int left, int right, int carry, bool is8Bit, out bool carryOut)
    {
        var digits = is8Bit ? 2 : 4;
        var result = 0;
        for (var digit = 0; digit < digits; digit++)
        {
            var sum = ((left >> (digit * 4)) & 0x0F) + ((right >> (digit * 4)) & 0x0F) + carry;
            if (sum >= 10)
            {
                sum -= 10;
                carry = 1;
            }
            else
            {
                carry = 0;
            }

            result |= sum << (digit * 4);
        }

        carryOut = carry != 0;
        return result;
    }

    private static int SubtractBcd(int left, int right, int carry, bool is8Bit, out bool carryOut)
    {
        var digits = is8Bit ? 2 : 4;
        var borrow = carry != 0 ? 0 : 1;
        var result = 0;
        for (var digit = 0; digit < digits; digit++)
        {
            var difference = ((left >> (digit * 4)) & 0x0F) - ((right >> (digit * 4)) & 0x0F) - borrow;
            if (difference < 0)
            {
                difference += 10;
                borrow = 1;
            }
            else
            {
                borrow = 0;
            }

            result |= difference << (digit * 4);
        }

        carryOut = borrow == 0;
        return result;
    }

    private void Compare(ushort left, ushort right, bool is8Bit)
    {
        var mask = is8Bit ? 0xFF : 0xFFFF;
        var result = (left - right) & mask;
        SetFlag(Carry, (left & mask) >= (right & mask));
        SetNegativeZero((ushort)result, is8Bit);
    }

    private void Bit(ushort value, bool immediate)
    {
        var is8Bit = AccumulatorIs8Bit;
        var mask = is8Bit ? 0xFF : 0xFFFF;
        SetFlag(Zero, (GetAccumulator() & value & mask) == 0);
        if (!immediate)
        {
            SetFlag(Negative, (value & (is8Bit ? 0x80 : 0x8000)) != 0);
            SetFlag(Overflow, (value & (is8Bit ? 0x40 : 0x4000)) != 0);
        }
    }

    private void TestAndSetBits(uint address)
    {
        var value = ReadValue(address, AccumulatorIs8Bit);
        SetFlag(Zero, (value & GetAccumulator()) == 0);
        WriteValue(address, (ushort)(value | GetAccumulator()), AccumulatorIs8Bit);
    }

    private void TestAndResetBits(uint address)
    {
        var value = ReadValue(address, AccumulatorIs8Bit);
        SetFlag(Zero, (value & GetAccumulator()) == 0);
        WriteValue(address, (ushort)(value & ~GetAccumulator()), AccumulatorIs8Bit);
    }

    private void ShiftAccumulator(ShiftKind kind)
    {
        var is8Bit = AccumulatorIs8Bit;
        var value = GetAccumulator();
        SetAccumulator(Shift(value, kind, is8Bit));
        SetNegativeZero(GetAccumulator(), is8Bit);
    }

    private void ShiftMemory(uint address, ShiftKind kind)
    {
        var value = ReadValue(address, AccumulatorIs8Bit);
        value = Shift(value, kind, AccumulatorIs8Bit);
        WriteValue(address, value, AccumulatorIs8Bit);
        SetNegativeZero(value, AccumulatorIs8Bit);
    }

    private ushort Shift(ushort value, ShiftKind kind, bool is8Bit)
    {
        var mask = is8Bit ? 0xFF : 0xFFFF;
        var sign = is8Bit ? 0x80 : 0x8000;
        var carryIn = GetFlag(Carry) ? 1 : 0;
        int result;
        switch (kind)
        {
            case ShiftKind.Asl:
                SetFlag(Carry, (value & sign) != 0);
                result = (value << 1) & mask;
                break;
            case ShiftKind.Lsr:
                SetFlag(Carry, (value & 1) != 0);
                result = value >> 1;
                break;
            case ShiftKind.Rol:
                SetFlag(Carry, (value & sign) != 0);
                result = ((value << 1) | carryIn) & mask;
                break;
            default:
                SetFlag(Carry, (value & 1) != 0);
                result = (value >> 1) | (carryIn != 0 ? sign : 0);
                break;
        }

        return (ushort)result;
    }

    private void IncrementAccumulator(int delta)
    {
        var mask = AccumulatorIs8Bit ? 0xFF : 0xFFFF;
        SetAccumulator((GetAccumulator() + delta) & mask);
        SetNegativeZero(GetAccumulator(), AccumulatorIs8Bit);
    }

    private void IncrementMemory(uint address, int delta)
    {
        var is8Bit = AccumulatorIs8Bit;
        var mask = is8Bit ? 0xFF : 0xFFFF;
        var value = (ReadValue(address, is8Bit) + delta) & mask;
        WriteValue(address, (ushort)value, is8Bit);
        SetNegativeZero((ushort)value, is8Bit);
    }

    private void Branch(bool always)
    {
        var displacement = (sbyte)FetchByte();
        if (always)
        {
            _programCounter = (ushort)(_programCounter + displacement);
        }
    }

    private void BlockMove(bool increment)
    {
        // MVN/MVP encode the destination bank first and source bank second,
        // even though assemblers conventionally display source,destination.
        var destinationBank = FetchByte();
        var sourceBank = FetchByte();
        var value = _bus.Read(((uint)sourceBank << 16) | _x);
        _bus.Write(((uint)destinationBank << 16) | _y, value);
        _dataBank = destinationBank;
        _x = MaskIndex(_x + (increment ? 1 : -1));
        _y = MaskIndex(_y + (increment ? 1 : -1));
        _a--;
        if (_a != 0xFFFF)
        {
            _programCounter -= 3;
        }
    }

    private void ServiceInterrupt(ushort vector, bool software)
    {
        if (!_emulation)
        {
            PushByte(_programBank);
        }

        PushWord(_programCounter);
        // In native mode bit 4 is the live X width flag and must survive an
        // interrupt. Only emulation mode overlays the 6502 B marker there.
        var pushedStatus = !_emulation || software ? _status : (byte)(_status & ~0x10);
        PushByte(pushedStatus);
        SetFlag(IrqDisable, true);
        SetFlag(Decimal, false);
        _programBank = 0;
        _programCounter = ReadWord(vector);
    }

    private void PushAccumulator()
    {
        if (AccumulatorIs8Bit) PushByte((byte)_a);
        else PushWord(_a);
    }

    private void PullAccumulator()
    {
        if (AccumulatorIs8Bit)
        {
            SetAccumulator(PopByte());
        }
        else
        {
            _a = PopWord();
        }

        SetNegativeZero(GetAccumulator(), AccumulatorIs8Bit);
    }

    private void PushIndex(ushort value)
    {
        if (IndexIs8Bit) PushByte((byte)value);
        else PushWord(value);
    }

    private ushort PopIndex() => IndexIs8Bit ? PopByte() : PopWord();

    private void LoadIndex(ref ushort register, ushort value)
    {
        register = MaskIndex(value);
        SetNegativeZero(register, IndexIs8Bit);
    }

    private void TransferToAccumulator(ushort value)
    {
        SetAccumulator(value);
        SetNegativeZero(GetAccumulator(), AccumulatorIs8Bit);
    }

    private void SetStackPointer(ushort value)
    {
        _stackPointer = _emulation ? (ushort)(0x0100 | (byte)value) : value;
    }

    private ushort GetAccumulator() => AccumulatorIs8Bit ? (byte)_a : _a;

    private void SetAccumulator(int value)
    {
        _a = AccumulatorIs8Bit
            ? (ushort)((_a & 0xFF00) | (value & 0xFF))
            : (ushort)value;
    }

    private ushort MaskIndex(int value) => (ushort)(IndexIs8Bit ? value & 0xFF : value);

    private void SetStatus(byte value)
    {
        _status = value;
        if (_emulation)
        {
            _status |= AccumulatorWidth | IndexWidth;
        }

        EnforceWidths();
    }

    private void EnforceWidths()
    {
        if (IndexIs8Bit)
        {
            _x &= 0x00FF;
            _y &= 0x00FF;
        }

        if (_emulation)
        {
            _stackPointer = (ushort)(0x0100 | (byte)_stackPointer);
        }
    }

    private void SetNegativeZero(ushort value, bool is8Bit)
    {
        var mask = is8Bit ? 0xFF : 0xFFFF;
        SetFlag(Zero, (value & mask) == 0);
        SetFlag(Negative, (value & (is8Bit ? 0x80 : 0x8000)) != 0);
    }

    private bool GetFlag(byte flag) => (_status & flag) != 0;

    private void SetFlag(byte flag, bool value)
    {
        if (value) _status |= flag;
        else _status &= (byte)~flag;
    }

    private byte FetchByte()
    {
        var value = _bus.Read(((uint)_programBank << 16) | _programCounter);
        _programCounter++;
        return value;
    }

    private ushort FetchWord()
    {
        var low = FetchByte();
        return (ushort)(low | (FetchByte() << 8));
    }

    private uint FetchLong()
    {
        var low = FetchWord();
        return low | ((uint)FetchByte() << 16);
    }

    private ushort FetchValue(bool is8Bit) => is8Bit ? FetchByte() : FetchWord();

    private ushort ReadValue(uint address, bool is8Bit) =>
        is8Bit ? _bus.Read(address) : ReadWord(address);

    private void WriteValue(uint address, ushort value, bool is8Bit)
    {
        _bus.Write(address, (byte)value);
        if (!is8Bit)
        {
            _bus.Write(IncrementWithinBank(address), (byte)(value >> 8));
        }
    }

    private ushort ReadWord(uint address)
    {
        var low = _bus.Read(address);
        return (ushort)(low | (_bus.Read(IncrementWithinBank(address)) << 8));
    }

    private uint ReadLong(uint address)
    {
        var low = ReadWord(address);
        return low | ((uint)_bus.Read(IncrementWithinBank(IncrementWithinBank(address))) << 16);
    }

    private uint AddressDirect() => (ushort)(_directPage + FetchByte());

    private uint AddressDirectX() => (ushort)(_directPage + FetchByte() + _x);

    private uint AddressDirectY() => (ushort)(_directPage + FetchByte() + _y);

    private uint AddressAbsolute() => ((uint)_dataBank << 16) | FetchWord();

    private uint AddressAbsoluteX() => (((uint)_dataBank << 16) + FetchWord() + _x) & 0xFFFFFF;

    private uint AddressAbsoluteY() => (((uint)_dataBank << 16) + FetchWord() + _y) & 0xFFFFFF;

    private void PushByte(byte value)
    {
        _bus.Write(_stackPointer, value);
        _stackPointer--;
        if (_emulation) _stackPointer = (ushort)(0x0100 | (byte)_stackPointer);
    }

    private byte PopByte()
    {
        _stackPointer++;
        if (_emulation) _stackPointer = (ushort)(0x0100 | (byte)_stackPointer);
        return _bus.Read(_stackPointer);
    }

    private void PushWord(ushort value)
    {
        PushByte((byte)(value >> 8));
        PushByte((byte)value);
    }

    private ushort PopWord()
    {
        var low = PopByte();
        return (ushort)(low | (PopByte() << 8));
    }

    private int Finish(int cycles)
    {
        TotalCycles += cycles;
        return cycles;
    }

    private static bool IsAccumulatorGroupAddress(int low) =>
        low is 0x01 or 0x03 or 0x05 or 0x07 or 0x09 or 0x0D or 0x0F or
            0x11 or 0x12 or 0x13 or 0x15 or 0x17 or 0x19 or 0x1D or 0x1F;

    private static uint IncrementWithinBank(uint address) =>
        (address & 0xFF0000) | (ushort)(address + 1);

    private enum ShiftKind
    {
        Asl,
        Lsr,
        Rol,
        Ror
    }
}
