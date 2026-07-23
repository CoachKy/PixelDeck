namespace PixelDeck.Emulation.Snes;

internal sealed class Spc700
{
    private static readonly byte[] CyclesPerOpcode =
    [
        2,8,4,5,3,4,3,6,2,6,5,4,5,4,6,8, 2,8,4,5,4,5,5,6,5,5,6,5,2,2,4,6,
        2,8,4,5,3,4,3,6,2,6,5,4,5,4,5,4, 2,8,4,5,4,5,5,6,5,5,6,5,2,2,3,8,
        2,8,4,5,3,4,3,6,2,6,4,4,5,4,6,6, 2,8,4,5,4,5,5,6,5,5,4,5,2,2,4,3,
        2,8,4,5,3,4,3,6,2,6,4,4,5,4,5,5, 2,8,4,5,4,5,5,6,5,5,5,5,2,2,3,6,
        2,8,4,5,3,4,3,6,2,6,5,4,5,2,4,5, 2,8,4,5,4,5,5,6,5,5,5,5,2,2,12,5,
        2,8,4,5,3,4,3,6,2,6,4,4,5,2,4,4, 2,8,4,5,4,5,5,6,5,5,5,5,2,2,3,4,
        2,8,4,5,4,5,4,7,2,5,6,4,5,2,4,9, 2,8,4,5,5,6,6,7,4,5,5,5,2,2,6,3,
        2,8,4,5,3,4,3,6,2,4,5,3,4,3,4,3, 2,8,4,5,4,5,5,6,3,4,5,4,2,2,4,3
    ];

    private readonly Func<ushort, byte> _read;
    private readonly Action<ushort, byte> _write;
    private byte _a;
    private byte _x;
    private byte _y;
    private byte _stackPointer;
    private ushort _programCounter;
    private bool _carry;
    private bool _zero;
    private bool _overflow;
    private bool _negative;
    private bool _interrupt;
    private bool _halfCarry;
    private bool _directPage;
    private bool _break;
    private bool _stopped;
    private int _extraCycles;

    public Spc700(Func<ushort, byte> read, Action<ushort, byte> write)
    {
        _read = read;
        _write = write;
    }

    public long ExecutedInstructions { get; private set; }

    public byte FirstUnsupportedOpcode { get; private set; }

    public ushort FirstUnsupportedAddress { get; private set; } = ushort.MaxValue;

    public void Reset()
    {
        _a = 0;
        _x = 0;
        _y = 0;
        _stackPointer = 0;
        _carry = false;
        _zero = false;
        _overflow = false;
        _negative = false;
        _interrupt = false;
        _halfCarry = false;
        _directPage = false;
        _break = false;
        _stopped = false;
        _programCounter = ReadWord(0xFFFE, 0xFFFF);
        ExecutedInstructions = 0;
        FirstUnsupportedOpcode = 0;
        FirstUnsupportedAddress = ushort.MaxValue;
    }

    public int Step()
    {
        if (_stopped)
        {
            return 1;
        }

        var opcode = FetchByte();
        _extraCycles = 0;
        Execute(opcode, (ushort)(_programCounter - 1));
        ExecutedInstructions++;
        return CyclesPerOpcode[opcode] + _extraCycles;
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(_a);
        writer.Write(_x);
        writer.Write(_y);
        writer.Write(_stackPointer);
        writer.Write(_programCounter);
        writer.Write(GetStatus());
        writer.Write(_stopped);
        writer.Write(ExecutedInstructions);
        writer.Write(FirstUnsupportedOpcode);
        writer.Write(FirstUnsupportedAddress);
    }

    public void LoadState(BinaryReader reader)
    {
        _a = reader.ReadByte();
        _x = reader.ReadByte();
        _y = reader.ReadByte();
        _stackPointer = reader.ReadByte();
        _programCounter = reader.ReadUInt16();
        SetStatus(reader.ReadByte());
        _stopped = reader.ReadBoolean();
        ExecutedInstructions = reader.ReadInt64();
        FirstUnsupportedOpcode = reader.ReadByte();
        FirstUnsupportedAddress = reader.ReadUInt16();
    }

    private void Execute(byte opcode, ushort instructionAddress)
    {
        var low = opcode & 0x0F;

        if (low == 0x01)
        {
            PushWord(_programCounter);
            var vector = (ushort)(0xFFDE - (2 * (opcode >> 4)));
            _programCounter = ReadWord(vector, (ushort)(vector + 1));
            return;
        }

        if (low == 0x02)
        {
            var address = Direct();
            var mask = 1 << (opcode >> 5);
            var value = Read(address);
            value = (byte)(((opcode & 0x10) == 0) ? value | mask : value & ~mask);
            Write(address, value);
            return;
        }

        if (low == 0x03)
        {
            var value = Read(Direct());
            var mask = 1 << (opcode >> 5);
            var branchWhenSet = (opcode & 0x10) == 0;
            Branch(branchWhenSet ? (value & mask) != 0 : (value & mask) == 0);
            return;
        }

        var operation = opcode & 0xE0;
        if (operation is 0x00 or 0x20 or 0x40 or 0x60 or 0x80 or 0xA0 &&
            low is >= 0x04 and <= 0x09)
        {
            if (low <= 0x07 || (low == 0x08 && (opcode & 0x10) == 0))
            {
                ApplyAccumulatorOperation(operation, Read(ResolveAccumulatorAddress(opcode)));
            }
            else
            {
                ushort source;
                ushort destination;
                if (low == 0x08)
                {
                    source = Immediate();
                    destination = Direct();
                }
                else if ((opcode & 0x10) == 0)
                {
                    source = Direct();
                    destination = Direct();
                }
                else
                {
                    source = (ushort)(_y | (_directPage ? 0x0100 : 0));
                    destination = (ushort)(_x | (_directPage ? 0x0100 : 0));
                }

                ApplyMemoryOperation(operation, destination, source);
            }

            return;
        }

        if (operation == 0xE0 && low is >= 0x04 and <= 0x07)
        {
            MoveA(Read(ResolveAccumulatorAddress(opcode)));
            return;
        }

        if (operation == 0xC0 && low is >= 0x04 and <= 0x07)
        {
            var address = ResolveAccumulatorAddress(opcode);
            Read(address);
            Write(address, _a);
            return;
        }

        switch (opcode)
        {
            case 0x00: break;
            case 0x0A: BitOperation(BitOperationKind.Or); break;
            case 0x0B: ShiftMemory(Direct(), ShiftKind.Asl); break;
            case 0x0C: ShiftMemory(Absolute(), ShiftKind.Asl); break;
            case 0x0D: PushByte(GetStatus()); break;
            case 0x0E: TestAndSetBits(); break;
            case 0x0F:
                PushWord(_programCounter);
                PushByte(GetStatus());
                _interrupt = false;
                _break = true;
                _programCounter = ReadWord(0xFFDE, 0xFFDF);
                break;
            case 0x10: Branch(!_negative); break;
            case 0x1A: IncrementWord(-1); break;
            case 0x1B: ShiftMemory(DirectX(), ShiftKind.Asl); break;
            case 0x1C: ShiftAccumulator(ShiftKind.Asl); break;
            case 0xCD: MoveX(FetchByte()); break;
            case 0xBD: _stackPointer = _x; break;
            case 0xE8: MoveA(FetchByte()); break;
            case 0x1D: _x--; SetNegativeZero(_x); break;
            case 0x1E: Compare(_x, Read(Absolute())); break;
            case 0x1F:
            {
                var pointer = FetchWord();
                var pointerLow = (ushort)(pointer + _x);
                _programCounter = ReadWord(pointerLow, (ushort)(pointerLow + 1));
                break;
            }
            case 0x20: _directPage = false; break;
            case 0x2A: BitOperation(BitOperationKind.OrNot); break;
            case 0x2B: ShiftMemory(Direct(), ShiftKind.Rol); break;
            case 0x2C: ShiftMemory(Absolute(), ShiftKind.Rol); break;
            case 0x2D: PushByte(_a); break;
            case 0x2E: CompareAndBranch(Direct()); break;
            case 0x2F:
            {
                var displacement = (sbyte)FetchByte();
                _programCounter = (ushort)(_programCounter + displacement);
                break;
            }
            case 0x30: Branch(_negative); break;
            case 0x3A: IncrementWord(1); break;
            case 0x3B: ShiftMemory(DirectX(), ShiftKind.Rol); break;
            case 0x3C: ShiftAccumulator(ShiftKind.Rol); break;
            case 0x3D: _x++; SetNegativeZero(_x); break;
            case 0x3E: Compare(_x, Read(Direct())); break;
            case 0x3F:
            {
                var destination = FetchWord();
                PushWord(_programCounter);
                _programCounter = destination;
                break;
            }
            case 0x40: _directPage = true; break;
            case 0x4A: BitOperation(BitOperationKind.And); break;
            case 0x4B: ShiftMemory(Direct(), ShiftKind.Lsr); break;
            case 0x4C: ShiftMemory(Absolute(), ShiftKind.Lsr); break;
            case 0x4D: PushByte(_x); break;
            case 0x4E: TestAndClearBits(); break;
            case 0x4F:
            {
                var destination = FetchByte();
                PushWord(_programCounter);
                _programCounter = (ushort)(0xFF00 | destination);
                break;
            }
            case 0x50: Branch(!_overflow); break;
            case 0x5A: CompareWord(); break;
            case 0x5B: ShiftMemory(DirectX(), ShiftKind.Lsr); break;
            case 0x5C: ShiftAccumulator(ShiftKind.Lsr); break;
            case 0x5D: MoveX(_a); break;
            case 0x5E: Compare(_y, Read(Absolute())); break;
            case 0x5F: _programCounter = FetchWord(); break;
            case 0x60: _carry = false; break;
            case 0x6A: BitOperation(BitOperationKind.AndNot); break;
            case 0x6B: ShiftMemory(Direct(), ShiftKind.Ror); break;
            case 0x6C: ShiftMemory(Absolute(), ShiftKind.Ror); break;
            case 0x6D: PushByte(_y); break;
            case 0x6E: DecrementAndBranch(Direct()); break;
            case 0x6F: _programCounter = PopWord(); break;
            case 0x70: Branch(_overflow); break;
            case 0x7A: AddWord(subtract: false); break;
            case 0x7B: ShiftMemory(DirectX(), ShiftKind.Ror); break;
            case 0x7C: ShiftAccumulator(ShiftKind.Ror); break;
            case 0x7D: MoveA(_x); break;
            case 0x7E: Compare(_y, Read(Direct())); break;
            case 0x7F:
                SetStatus(PopByte());
                _programCounter = PopWord();
                break;
            case 0x80: _carry = true; break;
            case 0x8A: BitOperation(BitOperationKind.ExclusiveOr); break;
            case 0x8B: IncrementMemory(Direct(), -1); break;
            case 0x8C: IncrementMemory(Absolute(), -1); break;
            case 0x8D: MoveY(FetchByte()); break;
            case 0x8E: SetStatus(PopByte()); break;
            case 0x8F:
            {
                var value = FetchByte();
                Write(Direct(), value);
                break;
            }
            case 0x90: Branch(!_carry); break;
            case 0x9A: AddWord(subtract: true); break;
            case 0x9B: IncrementMemory(DirectX(), -1); break;
            case 0x9C: _a--; SetNegativeZero(_a); break;
            case 0x9D: MoveX(_stackPointer); break;
            case 0x9E: Divide(); break;
            case 0x9F: _a = (byte)((_a >> 4) | (_a << 4)); SetNegativeZero(_a); break;
            case 0xA0: _interrupt = true; break;
            case 0xAA: BitOperation(BitOperationKind.MoveToCarry); break;
            case 0xAB: IncrementMemory(Direct(), 1); break;
            case 0xAC: IncrementMemory(Absolute(), 1); break;
            case 0xAD: Compare(_y, FetchByte()); break;
            case 0xAE: _a = PopByte(); break;
            case 0xAF: Write(IndirectXPostIncrement(), _a); break;
            case 0xB0: Branch(_carry); break;
            case 0xBA:
            {
                var lowAddress = Direct();
                _a = Read(lowAddress);
                _y = Read(DirectHigh(lowAddress));
                SetWordNegativeZero((ushort)(_a | (_y << 8)));
                break;
            }
            case 0xBB: IncrementMemory(DirectX(), 1); break;
            case 0xBC: _a++; SetNegativeZero(_a); break;
            case 0xBE: DecimalAdjust(subtract: true); break;
            case 0xBF: MoveA(Read(IndirectXPostIncrement())); break;
            case 0xC0: _interrupt = false; break;
            case 0xC8: Compare(_x, FetchByte()); break;
            case 0xC9: Store(Absolute(), _x); break;
            case 0xCA: BitOperation(BitOperationKind.MoveFromCarry); break;
            case 0xCB: Store(Direct(), _y); break;
            case 0xCC: Store(Absolute(), _y); break;
            case 0xCE: _x = PopByte(); break;
            case 0xCF:
            {
                var result = _a * _y;
                _a = (byte)result;
                _y = (byte)(result >> 8);
                SetNegativeZero(_y);
                break;
            }
            case 0xD0: Branch(!_zero); break;
            case 0xD8: Store(Direct(), _x); break;
            case 0xD9: Store(DirectY(), _x); break;
            case 0xDA: StoreWord(); break;
            case 0xDB: Store(DirectX(), _y); break;
            case 0xDC: _y--; SetNegativeZero(_y); break;
            case 0xDD: MoveA(_y); break;
            case 0xDE: CompareAndBranch(DirectX()); break;
            case 0xDF: DecimalAdjust(subtract: false); break;
            case 0xE0: _overflow = false; _halfCarry = false; break;
            case 0xE9: MoveX(Read(Absolute())); break;
            case 0xEA: BitOperation(BitOperationKind.Invert); break;
            case 0xEB: MoveY(Read(Direct())); break;
            case 0xEC: MoveY(Read(Absolute())); break;
            case 0xED: _carry = !_carry; break;
            case 0xEE: _y = PopByte(); break;
            case 0xEF:
                _stopped = true;
                break;
            case 0xF0: Branch(_zero); break;
            case 0xF8: MoveX(Read(Direct())); break;
            case 0xF9: MoveX(Read(DirectY())); break;
            case 0xFA:
            {
                var source = Direct();
                var destination = Direct();
                Write(destination, Read(source));
                break;
            }
            case 0xFB: MoveY(Read(DirectX())); break;
            case 0xFC: _y++; SetNegativeZero(_y); break;
            case 0xFD: MoveY(_a); break;
            case 0xFE: _y--; Branch(_y != 0); break;
            case 0xFF: _stopped = true; break;
            default:
                if (FirstUnsupportedAddress == ushort.MaxValue)
                {
                    FirstUnsupportedOpcode = opcode;
                    FirstUnsupportedAddress = instructionAddress;
                }

                _stopped = true;
                break;
        }
    }

    private ushort ResolveAccumulatorAddress(byte opcode)
    {
        var indexed = (opcode & 0x10) != 0;
        return (opcode & 0x0F) switch
        {
            0x04 => indexed ? DirectX() : Direct(),
            0x05 => indexed ? AbsoluteX() : Absolute(),
            0x06 => indexed ? AbsoluteY() : IndirectX(),
            0x07 => indexed ? IndirectDirectY() : IndexedIndirect(),
            0x08 => Immediate(),
            _ => throw new InvalidOperationException($"Invalid SPC700 accumulator address opcode ${opcode:X2}.")
        };
    }

    private void ApplyAccumulatorOperation(int operation, byte value)
    {
        switch (operation)
        {
            case 0x00:
                _a |= value;
                SetNegativeZero(_a);
                break;
            case 0x20:
                _a &= value;
                SetNegativeZero(_a);
                break;
            case 0x40:
                _a ^= value;
                SetNegativeZero(_a);
                break;
            case 0x60:
                Compare(_a, value);
                break;
            case 0x80:
                _a = Add(_a, value, subtract: false);
                break;
            case 0xA0:
                _a = Add(_a, value, subtract: true);
                break;
            case 0xE0:
                MoveA(value);
                break;
        }
    }

    private void ApplyMemoryOperation(int operation, ushort destination, ushort source)
    {
        var left = Read(destination);
        var right = Read(source);
        switch (operation)
        {
            case 0x00:
                left |= right;
                Write(destination, left);
                SetNegativeZero(left);
                break;
            case 0x20:
                left &= right;
                Write(destination, left);
                SetNegativeZero(left);
                break;
            case 0x40:
                left ^= right;
                Write(destination, left);
                SetNegativeZero(left);
                break;
            case 0x60:
                Compare(left, right);
                break;
            case 0x80:
                Write(destination, Add(left, right, subtract: false));
                break;
            case 0xA0:
                Write(destination, Add(left, right, subtract: true));
                break;
        }
    }

    private byte Add(byte left, byte right, bool subtract)
    {
        var operand = subtract ? right ^ 0xFF : right;
        var result = left + operand + (_carry ? 1 : 0);
        _overflow = ((left ^ result) & (operand ^ result) & 0x80) != 0;
        _halfCarry = ((left & 0x0F) + (operand & 0x0F) + (_carry ? 1 : 0)) > 0x0F;
        _carry = result > 0xFF;
        var value = (byte)result;
        SetNegativeZero(value);
        return value;
    }

    private void BitOperation(BitOperationKind operation)
    {
        var encoded = FetchWord();
        var address = (ushort)(encoded & 0x1FFF);
        var bit = encoded >> 13;
        var mask = 1 << bit;
        var value = Read(address);
        var isSet = (value & mask) != 0;

        switch (operation)
        {
            case BitOperationKind.Or: _carry |= isSet; break;
            case BitOperationKind.OrNot: _carry |= !isSet; break;
            case BitOperationKind.And: _carry &= isSet; break;
            case BitOperationKind.AndNot: _carry &= !isSet; break;
            case BitOperationKind.ExclusiveOr: _carry ^= isSet; break;
            case BitOperationKind.MoveToCarry: _carry = isSet; break;
            case BitOperationKind.MoveFromCarry:
                Write(address, (byte)((value & ~mask) | (_carry ? mask : 0)));
                break;
            case BitOperationKind.Invert:
                Write(address, (byte)(value ^ mask));
                break;
        }
    }

    private void ShiftMemory(ushort address, ShiftKind kind)
    {
        var value = Read(address);
        value = Shift(value, kind);
        Write(address, value);
    }

    private void ShiftAccumulator(ShiftKind kind) => _a = Shift(_a, kind);

    private byte Shift(byte value, ShiftKind kind)
    {
        var previousCarry = _carry;
        switch (kind)
        {
            case ShiftKind.Asl:
                _carry = (value & 0x80) != 0;
                value <<= 1;
                break;
            case ShiftKind.Lsr:
                _carry = (value & 1) != 0;
                value >>= 1;
                break;
            case ShiftKind.Rol:
                _carry = (value & 0x80) != 0;
                value = (byte)((value << 1) | (previousCarry ? 1 : 0));
                break;
            case ShiftKind.Ror:
                _carry = (value & 1) != 0;
                value = (byte)((value >> 1) | (previousCarry ? 0x80 : 0));
                break;
        }

        SetNegativeZero(value);
        return value;
    }

    private void TestAndSetBits()
    {
        var address = Absolute();
        var value = Read(address);
        SetNegativeZero((byte)(_a - value));
        Write(address, (byte)(value | _a));
    }

    private void TestAndClearBits()
    {
        var address = Absolute();
        var value = Read(address);
        SetNegativeZero((byte)(_a - value));
        Write(address, (byte)(value & ~_a));
    }

    private void IncrementWord(int delta)
    {
        var lowAddress = Direct();
        var value = (ushort)(ReadWord(lowAddress, DirectHigh(lowAddress)) + delta);
        Write(lowAddress, (byte)value);
        Write(DirectHigh(lowAddress), (byte)(value >> 8));
        SetWordNegativeZero(value);
    }

    private void CompareWord()
    {
        var lowAddress = Direct();
        var right = ReadWord(lowAddress, DirectHigh(lowAddress));
        var left = (ushort)(_a | (_y << 8));
        var result = left - right;
        _carry = result >= 0;
        SetWordNegativeZero((ushort)result);
    }

    private void AddWord(bool subtract)
    {
        var lowAddress = Direct();
        var memory = ReadWord(lowAddress, DirectHigh(lowAddress));
        var left = (ushort)(_a | (_y << 8));
        var right = subtract ? memory ^ 0xFFFF : memory;
        var carry = subtract ? 1 : 0;
        var result = left + right + carry;
        _overflow = ((left ^ result) & (right ^ result) & 0x8000) != 0;
        _halfCarry = ((left & 0x0FFF) + (right & 0x0FFF) + carry) > 0x0FFF;
        _carry = result > 0xFFFF;
        var value = (ushort)result;
        _a = (byte)value;
        _y = (byte)(value >> 8);
        SetWordNegativeZero(value);
    }

    private void IncrementMemory(ushort address, int delta)
    {
        var value = (byte)(Read(address) + delta);
        Write(address, value);
        SetNegativeZero(value);
    }

    private void CompareAndBranch(ushort address)
    {
        var different = _a != Read(address);
        Branch(different);
    }

    private void DecrementAndBranch(ushort address)
    {
        var value = (byte)(Read(address) - 1);
        Write(address, value);
        Branch(value != 0);
    }

    private void Divide()
    {
        var value = (ushort)(_a | (_y << 8));
        int quotient;
        int remainder;
        if (_x == 0)
        {
            quotient = 0xFFFF;
            remainder = _a;
        }
        else
        {
            quotient = value / _x;
            remainder = value % _x;
        }

        _overflow = quotient > 0xFF;
        _halfCarry = (_x & 0x0F) <= (_y & 0x0F);
        _a = (byte)quotient;
        _y = (byte)remainder;
        SetNegativeZero(_a);
    }

    private void DecimalAdjust(bool subtract)
    {
        if (subtract)
        {
            if (_a > 0x99 || !_carry)
            {
                _a -= 0x60;
                _carry = false;
            }

            if ((_a & 0x0F) > 9 || !_halfCarry)
            {
                _a -= 6;
            }
        }
        else
        {
            if (_a > 0x99 || _carry)
            {
                _a += 0x60;
                _carry = true;
            }

            if ((_a & 0x0F) > 9 || _halfCarry)
            {
                _a += 6;
            }
        }

        SetNegativeZero(_a);
    }

    private void Store(ushort address, byte value)
    {
        Read(address);
        Write(address, value);
    }

    private void StoreWord()
    {
        var lowAddress = Direct();
        Read(lowAddress);
        Write(lowAddress, _a);
        Write(DirectHigh(lowAddress), _y);
    }

    private ushort Direct() => (ushort)(FetchByte() | (_directPage ? 0x0100 : 0));

    private ushort DirectX() =>
        (ushort)(((FetchByte() + _x) & 0xFF) | (_directPage ? 0x0100 : 0));

    private ushort DirectY() =>
        (ushort)(((FetchByte() + _y) & 0xFF) | (_directPage ? 0x0100 : 0));

    private ushort Absolute() => FetchWord();

    private ushort AbsoluteX() => (ushort)(FetchWord() + _x);

    private ushort AbsoluteY() => (ushort)(FetchWord() + _y);

    private ushort Immediate() => _programCounter++;

    private ushort DirectHigh(ushort address) =>
        (ushort)(((address + 1) & 0x00FF) | (address & 0xFF00));

    private ushort IndirectX() => (ushort)(_x | (_directPage ? 0x0100 : 0));

    private ushort IndexedIndirect()
    {
        var pointer = FetchByte();
        var lowAddress = (ushort)(((pointer + _x) & 0xFF) | (_directPage ? 0x0100 : 0));
        return ReadWord(lowAddress, DirectHigh(lowAddress));
    }

    private ushort IndirectDirectY()
    {
        var pointerLow = Direct();
        var pointer = ReadWord(pointerLow, DirectHigh(pointerLow));
        return (ushort)(pointer + _y);
    }

    private ushort IndirectXPostIncrement()
    {
        var address = IndirectX();
        _x++;
        return address;
    }

    private void Branch(bool condition)
    {
        var displacement = (sbyte)FetchByte();
        if (condition)
        {
            _programCounter = (ushort)(_programCounter + displacement);
            _extraCycles += 2;
        }
    }

    private void Compare(byte left, byte right)
    {
        var result = left - right;
        _carry = result >= 0;
        SetNegativeZero((byte)result);
    }

    private void MoveA(byte value)
    {
        _a = value;
        SetNegativeZero(_a);
    }

    private void MoveX(byte value)
    {
        _x = value;
        SetNegativeZero(_x);
    }

    private void MoveY(byte value)
    {
        _y = value;
        SetNegativeZero(_y);
    }

    private void PushByte(byte value)
    {
        Write((ushort)(0x0100 | _stackPointer), value);
        _stackPointer--;
    }

    private byte PopByte()
    {
        _stackPointer++;
        return Read((ushort)(0x0100 | _stackPointer));
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

    private void SetNegativeZero(byte value)
    {
        _zero = value == 0;
        _negative = (value & 0x80) != 0;
    }

    private void SetWordNegativeZero(ushort value)
    {
        _zero = value == 0;
        _negative = (value & 0x8000) != 0;
    }

    private byte GetStatus() =>
        (byte)((_negative ? 0x80 : 0) |
               (_overflow ? 0x40 : 0) |
               (_directPage ? 0x20 : 0) |
               (_break ? 0x10 : 0) |
               (_halfCarry ? 0x08 : 0) |
               (_interrupt ? 0x04 : 0) |
               (_zero ? 0x02 : 0) |
               (_carry ? 0x01 : 0));

    private void SetStatus(byte value)
    {
        _negative = (value & 0x80) != 0;
        _overflow = (value & 0x40) != 0;
        _directPage = (value & 0x20) != 0;
        _break = (value & 0x10) != 0;
        _halfCarry = (value & 0x08) != 0;
        _interrupt = (value & 0x04) != 0;
        _zero = (value & 0x02) != 0;
        _carry = (value & 0x01) != 0;
    }

    private byte FetchByte() => Read(_programCounter++);

    private ushort FetchWord()
    {
        var low = FetchByte();
        return (ushort)(low | (FetchByte() << 8));
    }

    private ushort ReadWord(ushort lowAddress, ushort highAddress) =>
        (ushort)(Read(lowAddress) | (Read(highAddress) << 8));

    private byte Read(ushort address) => _read(address);

    private void Write(ushort address, byte value) => _write(address, value);

    private enum ShiftKind
    {
        Asl,
        Lsr,
        Rol,
        Ror
    }

    private enum BitOperationKind
    {
        Or,
        OrNot,
        And,
        AndNot,
        ExclusiveOr,
        MoveToCarry,
        MoveFromCarry,
        Invert
    }
}
