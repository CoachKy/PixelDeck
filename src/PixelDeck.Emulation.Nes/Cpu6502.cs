namespace PixelDeck.Emulation.Nes;

internal sealed class Cpu6502
{
    private enum RmwOperation
    {
        Asl,
        Rol,
        Lsr,
        Ror,
        Decrement,
        Increment,
        Slo,
        Rla,
        Sre,
        Rra,
        Dcp,
        Isc
    }

    private const byte Carry = 1 << 0;
    private const byte Zero = 1 << 1;
    private const byte InterruptDisable = 1 << 2;
    private const byte Decimal = 1 << 3;
    private const byte Break = 1 << 4;
    private const byte Unused = 1 << 5;
    private const byte Overflow = 1 << 6;
    private const byte Negative = 1 << 7;

    private readonly NesBus _bus;
    private bool _jammed;
    private bool _irqDisablePolled = true;
    private bool _nmiInterruptPending;
    private bool _irqInterruptPending;
    private bool _branchDelaysInterruptPoll;
    private long _dmaCyclesThisStep;

    public Cpu6502(NesBus bus)
    {
        _bus = bus;
    }

    public byte A { get; private set; }

    public byte X { get; private set; }

    public byte Y { get; private set; }

    public byte StackPointer { get; private set; } = 0xFD;

    public byte Status { get; private set; } = InterruptDisable | Unused;

    public ushort ProgramCounter { get; private set; }

    public long TotalCycles => _bus.Scheduler.CpuCycles;

    public void Reset()
    {
        _bus.Scheduler.ResetCpuCycleCount();
        A = 0;
        X = 0;
        Y = 0;
        StackPointer = 0xFD;
        Status = InterruptDisable | Unused;
        for (var cycle = 0; cycle < 5; cycle++)
        {
            IdleCycle();
        }

        ProgramCounter = ReadWord(0xFFFC);
        _jammed = false;
        _irqDisablePolled = true;
        _nmiInterruptPending = false;
        _irqInterruptPending = false;
        _branchDelaysInterruptPoll = false;
        _dmaCyclesThisStep = 0;
    }

    public void SoftReset()
    {
        Status = (byte)(Status | InterruptDisable | Unused);
        _ = Read(ProgramCounter);
        _ = Read(ProgramCounter);
        for (var stackRead = 0; stackRead < 3; stackRead++)
        {
            _ = Read((ushort)(0x0100 | StackPointer));
            StackPointer--;
        }

        ProgramCounter = ReadWord(0xFFFC);
        _jammed = false;
        _irqDisablePolled = true;
        _nmiInterruptPending = false;
        _irqInterruptPending = false;
        _branchDelaysInterruptPoll = false;
        _dmaCyclesThisStep = 0;
    }

    internal void SaveState(BinaryWriter writer)
    {
        writer.Write(A);
        writer.Write(X);
        writer.Write(Y);
        writer.Write(StackPointer);
        writer.Write(Status);
        writer.Write(ProgramCounter);
        writer.Write(_jammed);
        writer.Write(_irqDisablePolled);
        writer.Write(_nmiInterruptPending);
        writer.Write(_irqInterruptPending);
        writer.Write(_branchDelaysInterruptPoll);
    }

    internal void LoadState(BinaryReader reader)
    {
        A = reader.ReadByte();
        X = reader.ReadByte();
        Y = reader.ReadByte();
        StackPointer = reader.ReadByte();
        Status = reader.ReadByte();
        ProgramCounter = reader.ReadUInt16();
        _jammed = reader.ReadBoolean();
        _irqDisablePolled = reader.ReadBoolean();
        _nmiInterruptPending = reader.ReadBoolean();
        _irqInterruptPending = reader.ReadBoolean();
        _branchDelaysInterruptPoll = reader.ReadBoolean();
    }

    public int Step()
    {
        var stepStartedAt = TotalCycles;
        _branchDelaysInterruptPoll = false;
        _dmaCyclesThisStep = 0;
        if (_jammed)
        {
            IdleCycle();
            return 1;
        }

        if (_nmiInterruptPending)
        {
            _nmiInterruptPending = false;
            _irqInterruptPending = false;
            ServiceInterrupt(nmiSelected: true);
            return checked((int)(TotalCycles - stepStartedAt));
        }

        if (_irqInterruptPending)
        {
            _nmiInterruptPending = false;
            _irqInterruptPending = false;
            ServiceInterrupt(nmiSelected: false);
            return checked((int)(TotalCycles - stepStartedAt));
        }

        var instructionAddress = ProgramCounter;
        var opcode = FetchByte();
        var interruptDisableBefore = GetFlag(InterruptDisable);
        var cycles = Execute(opcode, instructionAddress);
        _irqDisablePolled = opcode is 0x28 or 0x58 or 0x78
            ? interruptDisableBefore
            : GetFlag(InterruptDisable);
        CompleteInstructionCycles(stepStartedAt, cycles);
        if (opcode == 0x00)
        {
            // BRK samples NMI before its status push, then explicitly cancels
            // NMI polling between the low and high vector reads.
            _nmiInterruptPending = false;
            _irqInterruptPending = false;
        }
        else
        {
            PollInterrupts();
        }

        Status |= Unused;
        return checked((int)(TotalCycles - stepStartedAt));
    }

    private int Execute(byte opcode, ushort instructionAddress)
    {
        ushort address;
        bool crossed;

        switch (opcode)
        {
            case 0x00: Brk(); return 7;
            case 0x01: A |= Read(IndexedIndirect()); SetZeroNegative(A); return 6;
            case 0x05: A |= Read(ZeroPage()); SetZeroNegative(A); return 3;
            case 0x06: address = ZeroPage(); ReadModifyWrite(address, RmwOperation.Asl); return 5;
            case 0x08: PushWithDummyRead((byte)(Status | Break | Unused)); return 3;
            case 0x09: A |= FetchByte(); SetZeroNegative(A); return 2;
            case 0x0A: A = Asl(A); return 2;
            case 0x0D: A |= Read(Absolute()); SetZeroNegative(A); return 4;
            case 0x0E: address = Absolute(); ReadModifyWrite(address, RmwOperation.Asl); return 6;

            case 0x10: return Branch(!GetFlag(Negative));
            case 0x11: A |= Read(IndirectIndexed(out crossed)); SetZeroNegative(A); return 5 + Bool(crossed);
            case 0x15: A |= Read(ZeroPageX()); SetZeroNegative(A); return 4;
            case 0x16: address = ZeroPageX(); ReadModifyWrite(address, RmwOperation.Asl); return 6;
            case 0x18: SetFlag(Carry, false); return 2;
            case 0x19: A |= Read(AbsoluteY(out crossed)); SetZeroNegative(A); return 4 + Bool(crossed);
            case 0x1D: A |= Read(AbsoluteX(out crossed)); SetZeroNegative(A); return 4 + Bool(crossed);
            case 0x1E: address = AbsoluteX(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Asl); return 7;

            case 0x20: Jsr(); return 6;
            case 0x21: A &= Read(IndexedIndirect()); SetZeroNegative(A); return 6;
            case 0x24: Bit(Read(ZeroPage())); return 3;
            case 0x25: A &= Read(ZeroPage()); SetZeroNegative(A); return 3;
            case 0x26: address = ZeroPage(); ReadModifyWrite(address, RmwOperation.Rol); return 5;
            case 0x28: Status = (byte)((PopWithDummyReads() & ~Break) | Unused); return 4;
            case 0x29: A &= FetchByte(); SetZeroNegative(A); return 2;
            case 0x2A: A = Rol(A); return 2;
            case 0x2C: Bit(Read(Absolute())); return 4;
            case 0x2D: A &= Read(Absolute()); SetZeroNegative(A); return 4;
            case 0x2E: address = Absolute(); ReadModifyWrite(address, RmwOperation.Rol); return 6;

            case 0x30: return Branch(GetFlag(Negative));
            case 0x31: A &= Read(IndirectIndexed(out crossed)); SetZeroNegative(A); return 5 + Bool(crossed);
            case 0x35: A &= Read(ZeroPageX()); SetZeroNegative(A); return 4;
            case 0x36: address = ZeroPageX(); ReadModifyWrite(address, RmwOperation.Rol); return 6;
            case 0x38: SetFlag(Carry, true); return 2;
            case 0x39: A &= Read(AbsoluteY(out crossed)); SetZeroNegative(A); return 4 + Bool(crossed);
            case 0x3D: A &= Read(AbsoluteX(out crossed)); SetZeroNegative(A); return 4 + Bool(crossed);
            case 0x3E: address = AbsoluteX(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Rol); return 7;

            case 0x40: Rti(); return 6;
            case 0x41: A ^= Read(IndexedIndirect()); SetZeroNegative(A); return 6;
            case 0x45: A ^= Read(ZeroPage()); SetZeroNegative(A); return 3;
            case 0x46: address = ZeroPage(); ReadModifyWrite(address, RmwOperation.Lsr); return 5;
            case 0x48: PushWithDummyRead(A); return 3;
            case 0x49: A ^= FetchByte(); SetZeroNegative(A); return 2;
            case 0x4A: A = Lsr(A); return 2;
            case 0x4C: ProgramCounter = Absolute(); return 3;
            case 0x4D: A ^= Read(Absolute()); SetZeroNegative(A); return 4;
            case 0x4E: address = Absolute(); ReadModifyWrite(address, RmwOperation.Lsr); return 6;

            case 0x50: return Branch(!GetFlag(Overflow));
            case 0x51: A ^= Read(IndirectIndexed(out crossed)); SetZeroNegative(A); return 5 + Bool(crossed);
            case 0x55: A ^= Read(ZeroPageX()); SetZeroNegative(A); return 4;
            case 0x56: address = ZeroPageX(); ReadModifyWrite(address, RmwOperation.Lsr); return 6;
            case 0x58: SetFlag(InterruptDisable, false); return 2;
            case 0x59: A ^= Read(AbsoluteY(out crossed)); SetZeroNegative(A); return 4 + Bool(crossed);
            case 0x5D: A ^= Read(AbsoluteX(out crossed)); SetZeroNegative(A); return 4 + Bool(crossed);
            case 0x5E: address = AbsoluteX(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Lsr); return 7;

            case 0x60: Rts(); return 6;
            case 0x61: Adc(Read(IndexedIndirect())); return 6;
            case 0x65: Adc(Read(ZeroPage())); return 3;
            case 0x66: address = ZeroPage(); ReadModifyWrite(address, RmwOperation.Ror); return 5;
            case 0x68: A = PopWithDummyReads(); SetZeroNegative(A); return 4;
            case 0x69: Adc(FetchByte()); return 2;
            case 0x6A: A = Ror(A); return 2;
            case 0x6C: ProgramCounter = IndirectJump(); return 5;
            case 0x6D: Adc(Read(Absolute())); return 4;
            case 0x6E: address = Absolute(); ReadModifyWrite(address, RmwOperation.Ror); return 6;

            case 0x70: return Branch(GetFlag(Overflow));
            case 0x71: Adc(Read(IndirectIndexed(out crossed))); return 5 + Bool(crossed);
            case 0x75: Adc(Read(ZeroPageX())); return 4;
            case 0x76: address = ZeroPageX(); ReadModifyWrite(address, RmwOperation.Ror); return 6;
            case 0x78: SetFlag(InterruptDisable, true); return 2;
            case 0x79: Adc(Read(AbsoluteY(out crossed))); return 4 + Bool(crossed);
            case 0x7D: Adc(Read(AbsoluteX(out crossed))); return 4 + Bool(crossed);
            case 0x7E: address = AbsoluteX(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Ror); return 7;

            case 0x81: Write(IndexedIndirect(), A); return 6;
            case 0x84: Write(ZeroPage(), Y); return 3;
            case 0x85: Write(ZeroPage(), A); return 3;
            case 0x86: Write(ZeroPage(), X); return 3;
            case 0x88: Y--; SetZeroNegative(Y); return 2;
            case 0x8A: A = X; SetZeroNegative(A); return 2;
            case 0x8C: Write(Absolute(), Y); return 4;
            case 0x8D: Write(Absolute(), A); return 4;
            case 0x8E: Write(Absolute(), X); return 4;

            case 0x90: return Branch(!GetFlag(Carry));
            case 0x91: Write(IndirectIndexed(out _, dummyRead: true), A); return 6;
            case 0x94: Write(ZeroPageX(), Y); return 4;
            case 0x95: Write(ZeroPageX(), A); return 4;
            case 0x96: Write(ZeroPageY(), X); return 4;
            case 0x98: A = Y; SetZeroNegative(A); return 2;
            case 0x99: Write(AbsoluteY(out _, dummyRead: true), A); return 5;
            case 0x9A: StackPointer = X; return 2;
            case 0x9D: Write(AbsoluteX(out _, dummyRead: true), A); return 5;

            case 0xA0: Y = FetchByte(); SetZeroNegative(Y); return 2;
            case 0xA1: A = Read(IndexedIndirect()); SetZeroNegative(A); return 6;
            case 0xA2: X = FetchByte(); SetZeroNegative(X); return 2;
            case 0xA4: Y = Read(ZeroPage()); SetZeroNegative(Y); return 3;
            case 0xA5: A = Read(ZeroPage()); SetZeroNegative(A); return 3;
            case 0xA6: X = Read(ZeroPage()); SetZeroNegative(X); return 3;
            case 0xA8: Y = A; SetZeroNegative(Y); return 2;
            case 0xA9: A = FetchByte(); SetZeroNegative(A); return 2;
            case 0xAA: X = A; SetZeroNegative(X); return 2;
            case 0xAC: Y = Read(Absolute()); SetZeroNegative(Y); return 4;
            case 0xAD: A = Read(Absolute()); SetZeroNegative(A); return 4;
            case 0xAE: X = Read(Absolute()); SetZeroNegative(X); return 4;

            case 0xB0: return Branch(GetFlag(Carry));
            case 0xB1: A = Read(IndirectIndexed(out crossed)); SetZeroNegative(A); return 5 + Bool(crossed);
            case 0xB4: Y = Read(ZeroPageX()); SetZeroNegative(Y); return 4;
            case 0xB5: A = Read(ZeroPageX()); SetZeroNegative(A); return 4;
            case 0xB6: X = Read(ZeroPageY()); SetZeroNegative(X); return 4;
            case 0xB8: SetFlag(Overflow, false); return 2;
            case 0xB9: A = Read(AbsoluteY(out crossed)); SetZeroNegative(A); return 4 + Bool(crossed);
            case 0xBA: X = StackPointer; SetZeroNegative(X); return 2;
            case 0xBC: Y = Read(AbsoluteX(out crossed)); SetZeroNegative(Y); return 4 + Bool(crossed);
            case 0xBD: A = Read(AbsoluteX(out crossed)); SetZeroNegative(A); return 4 + Bool(crossed);
            case 0xBE: X = Read(AbsoluteY(out crossed)); SetZeroNegative(X); return 4 + Bool(crossed);

            case 0xC0: Compare(Y, FetchByte()); return 2;
            case 0xC1: Compare(A, Read(IndexedIndirect())); return 6;
            case 0xC4: Compare(Y, Read(ZeroPage())); return 3;
            case 0xC5: Compare(A, Read(ZeroPage())); return 3;
            case 0xC6: address = ZeroPage(); ReadModifyWrite(address, RmwOperation.Decrement); return 5;
            case 0xC8: Y++; SetZeroNegative(Y); return 2;
            case 0xC9: Compare(A, FetchByte()); return 2;
            case 0xCA: X--; SetZeroNegative(X); return 2;
            case 0xCC: Compare(Y, Read(Absolute())); return 4;
            case 0xCD: Compare(A, Read(Absolute())); return 4;
            case 0xCE: address = Absolute(); ReadModifyWrite(address, RmwOperation.Decrement); return 6;

            case 0xD0: return Branch(!GetFlag(Zero));
            case 0xD1: Compare(A, Read(IndirectIndexed(out crossed))); return 5 + Bool(crossed);
            case 0xD5: Compare(A, Read(ZeroPageX())); return 4;
            case 0xD6: address = ZeroPageX(); ReadModifyWrite(address, RmwOperation.Decrement); return 6;
            case 0xD8: SetFlag(Decimal, false); return 2;
            case 0xD9: Compare(A, Read(AbsoluteY(out crossed))); return 4 + Bool(crossed);
            case 0xDD: Compare(A, Read(AbsoluteX(out crossed))); return 4 + Bool(crossed);
            case 0xDE: address = AbsoluteX(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Decrement); return 7;

            case 0xE0: Compare(X, FetchByte()); return 2;
            case 0xE1: Sbc(Read(IndexedIndirect())); return 6;
            case 0xE4: Compare(X, Read(ZeroPage())); return 3;
            case 0xE5: Sbc(Read(ZeroPage())); return 3;
            case 0xE6: address = ZeroPage(); ReadModifyWrite(address, RmwOperation.Increment); return 5;
            case 0xE8: X++; SetZeroNegative(X); return 2;
            case 0xE9: Sbc(FetchByte()); return 2;
            case 0xEA: return 2;
            case 0xEC: Compare(X, Read(Absolute())); return 4;
            case 0xED: Sbc(Read(Absolute())); return 4;
            case 0xEE: address = Absolute(); ReadModifyWrite(address, RmwOperation.Increment); return 6;

            case 0xF0: return Branch(GetFlag(Zero));
            case 0xF1: Sbc(Read(IndirectIndexed(out crossed))); return 5 + Bool(crossed);
            case 0xF5: Sbc(Read(ZeroPageX())); return 4;
            case 0xF6: address = ZeroPageX(); ReadModifyWrite(address, RmwOperation.Increment); return 6;
            case 0xF8: SetFlag(Decimal, true); return 2;
            case 0xF9: Sbc(Read(AbsoluteY(out crossed))); return 4 + Bool(crossed);
            case 0xFD: Sbc(Read(AbsoluteX(out crossed))); return 4 + Bool(crossed);
            case 0xFE: address = AbsoluteX(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Increment); return 7;

            // Undocumented NMOS 6502 instructions used by some licensed NES
            // software and homebrew.
            case 0x03: address = IndexedIndirect(); ReadModifyWrite(address, RmwOperation.Slo); return 8;
            case 0x07: address = ZeroPage(); ReadModifyWrite(address, RmwOperation.Slo); return 5;
            case 0x0B or 0x2B: Anc(FetchByte()); return 2;
            case 0x0F: address = Absolute(); ReadModifyWrite(address, RmwOperation.Slo); return 6;
            case 0x13: address = IndirectIndexed(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Slo); return 8;
            case 0x17: address = ZeroPageX(); ReadModifyWrite(address, RmwOperation.Slo); return 6;
            case 0x1B: address = AbsoluteY(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Slo); return 7;
            case 0x1F: address = AbsoluteX(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Slo); return 7;

            case 0x23: address = IndexedIndirect(); ReadModifyWrite(address, RmwOperation.Rla); return 8;
            case 0x27: address = ZeroPage(); ReadModifyWrite(address, RmwOperation.Rla); return 5;
            case 0x2F: address = Absolute(); ReadModifyWrite(address, RmwOperation.Rla); return 6;
            case 0x33: address = IndirectIndexed(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Rla); return 8;
            case 0x37: address = ZeroPageX(); ReadModifyWrite(address, RmwOperation.Rla); return 6;
            case 0x3B: address = AbsoluteY(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Rla); return 7;
            case 0x3F: address = AbsoluteX(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Rla); return 7;

            case 0x43: address = IndexedIndirect(); ReadModifyWrite(address, RmwOperation.Sre); return 8;
            case 0x47: address = ZeroPage(); ReadModifyWrite(address, RmwOperation.Sre); return 5;
            case 0x4B: A &= FetchByte(); A = Lsr(A); return 2;
            case 0x4F: address = Absolute(); ReadModifyWrite(address, RmwOperation.Sre); return 6;
            case 0x53: address = IndirectIndexed(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Sre); return 8;
            case 0x57: address = ZeroPageX(); ReadModifyWrite(address, RmwOperation.Sre); return 6;
            case 0x5B: address = AbsoluteY(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Sre); return 7;
            case 0x5F: address = AbsoluteX(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Sre); return 7;

            case 0x63: address = IndexedIndirect(); ReadModifyWrite(address, RmwOperation.Rra); return 8;
            case 0x67: address = ZeroPage(); ReadModifyWrite(address, RmwOperation.Rra); return 5;
            case 0x6B: Arr(FetchByte()); return 2;
            case 0x6F: address = Absolute(); ReadModifyWrite(address, RmwOperation.Rra); return 6;
            case 0x73: address = IndirectIndexed(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Rra); return 8;
            case 0x77: address = ZeroPageX(); ReadModifyWrite(address, RmwOperation.Rra); return 6;
            case 0x7B: address = AbsoluteY(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Rra); return 7;
            case 0x7F: address = AbsoluteX(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Rra); return 7;

            case 0x83: Write(IndexedIndirect(), (byte)(A & X)); return 6;
            case 0x87: Write(ZeroPage(), (byte)(A & X)); return 3;
            case 0x8B: Xaa(FetchByte()); return 2;
            case 0x8F: Write(Absolute(), (byte)(A & X)); return 4;
            case 0x97: Write(ZeroPageY(), (byte)(A & X)); return 4;
            case 0x93:
                address = IndirectIndexed(out _, out var indirectBase, dummyRead: true);
                StoreHighMasked(indirectBase, address, (byte)(A & X));
                return 6;
            case 0x9B:
                var tasBase = FetchWord();
                address = (ushort)(tasBase + Y);
                _ = Read(IndexedDummyAddress(tasBase, address));
                StackPointer = (byte)(A & X);
                StoreHighMasked(tasBase, address, StackPointer);
                return 5;
            case 0x9C:
                var shyBase = FetchWord();
                address = (ushort)(shyBase + X);
                _ = Read(IndexedDummyAddress(shyBase, address));
                StoreHighMasked(shyBase, address, Y);
                return 5;
            case 0x9E:
                var shxBase = FetchWord();
                address = (ushort)(shxBase + Y);
                _ = Read(IndexedDummyAddress(shxBase, address));
                StoreHighMasked(shxBase, address, X);
                return 5;
            case 0x9F:
                var ahxBase = FetchWord();
                address = (ushort)(ahxBase + Y);
                _ = Read(IndexedDummyAddress(ahxBase, address));
                StoreHighMasked(ahxBase, address, (byte)(A & X));
                return 5;

            case 0xA3: LoadAx(Read(IndexedIndirect())); return 6;
            case 0xA7: LoadAx(Read(ZeroPage())); return 3;
            case 0xAB: LoadAx(FetchByte()); return 2;
            case 0xAF: LoadAx(Read(Absolute())); return 4;
            case 0xB3: LoadAx(Read(IndirectIndexed(out crossed))); return 5 + Bool(crossed);
            case 0xB7: LoadAx(Read(ZeroPageY())); return 4;
            case 0xBB:
                var lasValue = (byte)(Read(AbsoluteY(out crossed)) & StackPointer);
                A = X = StackPointer = lasValue;
                SetZeroNegative(lasValue);
                return 4 + Bool(crossed);
            case 0xBF: LoadAx(Read(AbsoluteY(out crossed))); return 4 + Bool(crossed);

            case 0xC3: address = IndexedIndirect(); ReadModifyWrite(address, RmwOperation.Dcp); return 8;
            case 0xC7: address = ZeroPage(); ReadModifyWrite(address, RmwOperation.Dcp); return 5;
            case 0xCB: Axs(FetchByte()); return 2;
            case 0xCF: address = Absolute(); ReadModifyWrite(address, RmwOperation.Dcp); return 6;
            case 0xD3: address = IndirectIndexed(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Dcp); return 8;
            case 0xD7: address = ZeroPageX(); ReadModifyWrite(address, RmwOperation.Dcp); return 6;
            case 0xDB: address = AbsoluteY(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Dcp); return 7;
            case 0xDF: address = AbsoluteX(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Dcp); return 7;

            case 0xE3: address = IndexedIndirect(); ReadModifyWrite(address, RmwOperation.Isc); return 8;
            case 0xE7: address = ZeroPage(); ReadModifyWrite(address, RmwOperation.Isc); return 5;
            case 0xEF: address = Absolute(); ReadModifyWrite(address, RmwOperation.Isc); return 6;
            case 0xF3: address = IndirectIndexed(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Isc); return 8;
            case 0xF7: address = ZeroPageX(); ReadModifyWrite(address, RmwOperation.Isc); return 6;
            case 0xFB: address = AbsoluteY(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Isc); return 7;
            case 0xFF: address = AbsoluteX(out _, dummyRead: true); ReadModifyWrite(address, RmwOperation.Isc); return 7;

            // Stable undocumented NOP encodings used by some commercial software.
            case 0x1A or 0x3A or 0x5A or 0x7A or 0xDA or 0xFA: return 2;
            case 0x04 or 0x44 or 0x64: _ = Read(ZeroPage()); return 3;
            case 0x14 or 0x34 or 0x54 or 0x74 or 0xD4 or 0xF4: _ = Read(ZeroPageX()); return 4;
            case 0x80 or 0x82 or 0x89 or 0xC2 or 0xE2: _ = FetchByte(); return 2;
            case 0x0C: _ = Read(Absolute()); return 4;
            case 0x1C or 0x3C or 0x5C or 0x7C or 0xDC or 0xFC:
                _ = Read(AbsoluteX(out crossed));
                return 4 + Bool(crossed);
            case 0xEB: Sbc(FetchByte()); return 2;
            case 0x02 or 0x12 or 0x22 or 0x32 or 0x42 or 0x52 or 0x62 or 0x72
                or 0x92 or 0xB2 or 0xD2 or 0xF2:
                _jammed = true;
                return 2;
            default:
                throw new InvalidOperationException($"Unsupported 6502 opcode ${opcode:X2} at ${instructionAddress:X4}.");
        }
    }

    private byte Read(ushort address)
    {
        ProcessPendingDma(address);
        var value = _bus.Read(address);
        IdleCycle();
        return value;
    }

    private void Write(ushort address, byte value)
    {
        _bus.Write(address, value);
        IdleCycle();
    }

    private void ReadModifyWrite(ushort address, RmwOperation operation)
    {
        var original = Read(address);
        Write(address, original);
        var result = operation switch
        {
            RmwOperation.Asl => Asl(original),
            RmwOperation.Rol => Rol(original),
            RmwOperation.Lsr => Lsr(original),
            RmwOperation.Ror => Ror(original),
            RmwOperation.Decrement => Decrement(original),
            RmwOperation.Increment => Increment(original),
            RmwOperation.Slo => Slo(original),
            RmwOperation.Rla => Rla(original),
            RmwOperation.Sre => Sre(original),
            RmwOperation.Rra => Rra(original),
            RmwOperation.Dcp => Dcp(original),
            RmwOperation.Isc => Isc(original),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        Write(address, result);
    }

    private byte FetchByte() => Read(ProgramCounter++);

    private ushort FetchWord()
    {
        var low = FetchByte();
        var high = FetchByte();
        return (ushort)(low | (high << 8));
    }

    private ushort ReadWord(ushort address)
    {
        var low = Read(address);
        var high = Read((ushort)(address + 1));
        return (ushort)(low | (high << 8));
    }

    private ushort ZeroPage() => FetchByte();

    private ushort ZeroPageX()
    {
        var baseAddress = FetchByte();
        _ = Read(baseAddress);
        return (byte)(baseAddress + X);
    }

    private ushort ZeroPageY()
    {
        var baseAddress = FetchByte();
        _ = Read(baseAddress);
        return (byte)(baseAddress + Y);
    }

    private ushort Absolute() => FetchWord();

    private ushort AbsoluteX(out bool crossed, bool dummyRead = false)
    {
        var baseAddress = FetchWord();
        var result = (ushort)(baseAddress + X);
        crossed = PageCrossed(baseAddress, result);
        if (dummyRead || crossed)
        {
            _ = Read(IndexedDummyAddress(baseAddress, result));
        }

        return result;
    }

    private ushort AbsoluteY(out bool crossed, bool dummyRead = false)
    {
        var baseAddress = FetchWord();
        var result = (ushort)(baseAddress + Y);
        crossed = PageCrossed(baseAddress, result);
        if (dummyRead || crossed)
        {
            _ = Read(IndexedDummyAddress(baseAddress, result));
        }

        return result;
    }

    private ushort IndexedIndirect()
    {
        var basePointer = FetchByte();
        _ = Read(basePointer);
        var pointer = (byte)(basePointer + X);
        var low = Read(pointer);
        var high = Read((byte)(pointer + 1));
        return (ushort)(low | (high << 8));
    }

    private ushort IndirectIndexed(out bool crossed, bool dummyRead = false) =>
        IndirectIndexed(out crossed, out _, dummyRead);

    private ushort IndirectIndexed(
        out bool crossed,
        out ushort baseAddress,
        bool dummyRead = false)
    {
        var pointer = FetchByte();
        var low = Read(pointer);
        var high = Read((byte)(pointer + 1));
        baseAddress = (ushort)(low | (high << 8));
        var result = (ushort)(baseAddress + Y);
        crossed = PageCrossed(baseAddress, result);
        if (dummyRead || crossed)
        {
            _ = Read(IndexedDummyAddress(baseAddress, result));
        }

        return result;
    }

    private static ushort IndexedDummyAddress(ushort baseAddress, ushort result) =>
        (ushort)((baseAddress & 0xFF00) | (result & 0x00FF));

    private ushort IndirectJump()
    {
        var pointer = FetchWord();
        var low = Read(pointer);
        var highAddress = (ushort)((pointer & 0xFF00) | ((pointer + 1) & 0x00FF));
        var high = Read(highAddress);
        return (ushort)(low | (high << 8));
    }

    private void Jsr()
    {
        var targetLow = FetchByte();
        _ = Read((ushort)(0x0100 | StackPointer));
        var returnAddress = ProgramCounter;
        Push((byte)(returnAddress >> 8));
        Push((byte)returnAddress);
        var targetHigh = FetchByte();
        ProgramCounter = (ushort)(targetLow | (targetHigh << 8));
    }

    private void Rts()
    {
        _ = Read(ProgramCounter);
        _ = Read((ushort)(0x0100 | StackPointer));
        var low = Pop();
        var high = Pop();
        ProgramCounter = (ushort)(low | (high << 8));
        _ = Read(ProgramCounter);
        ProgramCounter++;
    }

    private void Brk()
    {
        _ = Read(ProgramCounter++);
        Push((byte)(ProgramCounter >> 8));
        Push((byte)ProgramCounter);
        var vector = 0xFFFE;
        if (_bus.Ppu.NmiRequested)
        {
            vector = 0xFFFA;
            _bus.Ppu.AcknowledgeNmi();
        }

        Push((byte)(Status | Break | Unused));
        SetFlag(InterruptDisable, true);
        var low = Read((ushort)vector);
        var high = Read((ushort)(vector + 1));
        ProgramCounter = (ushort)(low | (high << 8));
    }

    private void Rti()
    {
        _ = Read(ProgramCounter);
        _ = Read((ushort)(0x0100 | StackPointer));
        Status = (byte)((Pop() & ~Break) | Unused);
        var low = Pop();
        var high = Pop();
        ProgramCounter = (ushort)(low | (high << 8));
    }

    private void ServiceInterrupt(bool nmiSelected)
    {
        _ = Read(ProgramCounter);
        _ = Read(ProgramCounter);
        Push((byte)(ProgramCounter >> 8));
        Push((byte)ProgramCounter);
        var vector = 0xFFFE;
        if (nmiSelected || _bus.Ppu.NmiRequested)
        {
            vector = 0xFFFA;
            _bus.Ppu.AcknowledgeNmi();
        }

        Push((byte)((Status & ~Break) | Unused));
        SetFlag(InterruptDisable, true);
        _irqDisablePolled = true;
        var low = Read((ushort)vector);
        var high = Read((ushort)(vector + 1));
        ProgramCounter = (ushort)(low | (high << 8));
    }

    private int Branch(bool condition)
    {
        var irqWasPendingBeforeOperand = _bus.Scheduler.IrqPendingAtPollPoint;
        var offset = (sbyte)FetchByte();
        if (!condition)
        {
            return 2;
        }

        var previous = ProgramCounter;
        _ = Read(previous);
        ProgramCounter = (ushort)(ProgramCounter + offset);
        if (PageCrossed(previous, ProgramCounter))
        {
            _ = Read(IndexedDummyAddress(previous, ProgramCounter));
        }
        else if (!irqWasPendingBeforeOperand)
        {
            _branchDelaysInterruptPoll = true;
        }

        return 3 + Bool(PageCrossed(previous, ProgramCounter));
    }

    private void Push(byte value)
    {
        Write((ushort)(0x0100 | StackPointer), value);
        StackPointer--;
    }

    private void PushWithDummyRead(byte value)
    {
        _ = Read(ProgramCounter);
        Push(value);
    }

    private byte Pop()
    {
        StackPointer++;
        return Read((ushort)(0x0100 | StackPointer));
    }

    private byte PopWithDummyReads()
    {
        _ = Read(ProgramCounter);
        _ = Read((ushort)(0x0100 | StackPointer));
        return Pop();
    }

    private void CompleteInstructionCycles(long instructionStartedAt, int expectedCycles)
    {
        var expectedEnd = instructionStartedAt + expectedCycles + _dmaCyclesThisStep;
        if (TotalCycles > expectedEnd)
        {
            throw new InvalidOperationException(
                $"A CPU instruction used {TotalCycles - instructionStartedAt} scheduled cycles; expected {expectedCycles}.");
        }

        while (TotalCycles < expectedEnd)
        {
            IdleCycle();
        }
    }

    private void PollInterrupts()
    {
        _nmiInterruptPending = _bus.Scheduler.NmiPendingAtPollPoint;
        _irqInterruptPending =
            (_bus.Scheduler.ApuIrqPendingBeforePollPoint ||
             _bus.Scheduler.CartridgeIrqPendingAtPollPoint) &&
            !_irqDisablePolled &&
            !_branchDelaysInterruptPoll &&
            !_nmiInterruptPending;
    }

    private void ProcessPendingDma(ushort haltedReadAddress)
    {
        var hasOamDma = _bus.TryTakeOamDma(out var oamPage);
        var hasDmcDma = _bus.Apu.DmcDmaPending;
        if (!hasOamDma && !hasDmcDma)
        {
            return;
        }

        var dmaStartedAt = TotalCycles;
        var skipRepeatedInputReads = haltedReadAddress is 0x4016 or 0x4017;
        var oamAddress = 0;
        var oamWriteReady = false;
        byte oamValue = 0;
        var dmcHaltNeeded = hasDmcDma;
        var dmcDummyNeeded = hasDmcDma;

        // A DMA unit can only halt the CPU on a read cycle. The attempted CPU
        // read is repeated once as the halt cycle.
        _ = _bus.Read(haltedReadAddress);
        IdleCycle();
        dmcHaltNeeded = false;

        while (hasOamDma || hasDmcDma)
        {
            var getCycle = (TotalCycles & 1) == 0;
            if (getCycle)
            {
                if (hasDmcDma && !dmcHaltNeeded && !dmcDummyNeeded)
                {
                    var value = _bus.Read(_bus.Apu.DmcDmaAddress);
                    IdleCycle();
                    _bus.Apu.CompleteDmcDma(value);
                    hasDmcDma = false;
                }
                else if (hasOamDma)
                {
                    AdvanceDmcDmaPhase(ref dmcHaltNeeded, ref dmcDummyNeeded);
                    oamValue = _bus.Read((ushort)((oamPage << 8) | oamAddress));
                    IdleCycle();
                    oamWriteReady = true;
                }
                else
                {
                    AdvanceDmcDmaPhase(ref dmcHaltNeeded, ref dmcDummyNeeded);
                    if (!skipRepeatedInputReads)
                    {
                        _ = _bus.Read(haltedReadAddress);
                    }

                    IdleCycle();
                }
            }
            else if (hasOamDma && oamWriteReady)
            {
                AdvanceDmcDmaPhase(ref dmcHaltNeeded, ref dmcDummyNeeded);
                _bus.WriteOamDmaByte(oamValue);
                IdleCycle();
                oamWriteReady = false;
                oamAddress++;
                hasOamDma = oamAddress < 256;
            }
            else
            {
                AdvanceDmcDmaPhase(ref dmcHaltNeeded, ref dmcDummyNeeded);
                if (!skipRepeatedInputReads)
                {
                    _ = _bus.Read(haltedReadAddress);
                }

                IdleCycle();
            }

            if (!hasDmcDma && _bus.Apu.DmcDmaPending)
            {
                hasDmcDma = true;
                dmcHaltNeeded = true;
                dmcDummyNeeded = true;
            }
        }

        _dmaCyclesThisStep += TotalCycles - dmaStartedAt;
    }

    private static void AdvanceDmcDmaPhase(ref bool haltNeeded, ref bool dummyNeeded)
    {
        if (haltNeeded)
        {
            haltNeeded = false;
        }
        else if (dummyNeeded)
        {
            dummyNeeded = false;
        }
    }

    private void IdleCycle() => _bus.Scheduler.ClockCpuCycle();

    private void Adc(byte value)
    {
        var carryIn = GetFlag(Carry) ? 1 : 0;
        var sum = A + value + carryIn;
        var result = (byte)sum;
        SetFlag(Carry, sum > 0xFF);
        SetFlag(Overflow, ((~(A ^ value) & (A ^ result)) & 0x80) != 0);
        A = result;
        SetZeroNegative(A);
    }

    private void Sbc(byte value) => Adc((byte)~value);

    private void Compare(byte register, byte value)
    {
        var result = (byte)(register - value);
        SetFlag(Carry, register >= value);
        SetZeroNegative(result);
    }

    private void Bit(byte value)
    {
        SetFlag(Zero, (A & value) == 0);
        SetFlag(Overflow, (value & Overflow) != 0);
        SetFlag(Negative, (value & Negative) != 0);
    }

    private byte Asl(byte value)
    {
        SetFlag(Carry, (value & 0x80) != 0);
        value <<= 1;
        SetZeroNegative(value);
        return value;
    }

    private byte Lsr(byte value)
    {
        SetFlag(Carry, (value & 0x01) != 0);
        value >>= 1;
        SetZeroNegative(value);
        return value;
    }

    private byte Rol(byte value)
    {
        var carryIn = GetFlag(Carry) ? 1 : 0;
        SetFlag(Carry, (value & 0x80) != 0);
        value = (byte)((value << 1) | carryIn);
        SetZeroNegative(value);
        return value;
    }

    private byte Ror(byte value)
    {
        var carryIn = GetFlag(Carry) ? 0x80 : 0;
        SetFlag(Carry, (value & 0x01) != 0);
        value = (byte)((value >> 1) | carryIn);
        SetZeroNegative(value);
        return value;
    }

    private byte Increment(byte value)
    {
        value++;
        SetZeroNegative(value);
        return value;
    }

    private byte Decrement(byte value)
    {
        value--;
        SetZeroNegative(value);
        return value;
    }

    private byte Slo(byte value)
    {
        value = Asl(value);
        A |= value;
        SetZeroNegative(A);
        return value;
    }

    private byte Rla(byte value)
    {
        value = Rol(value);
        A &= value;
        SetZeroNegative(A);
        return value;
    }

    private byte Sre(byte value)
    {
        value = Lsr(value);
        A ^= value;
        SetZeroNegative(A);
        return value;
    }

    private byte Rra(byte value)
    {
        value = Ror(value);
        Adc(value);
        return value;
    }

    private byte Dcp(byte value)
    {
        value = Decrement(value);
        Compare(A, value);
        return value;
    }

    private byte Isc(byte value)
    {
        value = Increment(value);
        Sbc(value);
        return value;
    }

    private void Anc(byte value)
    {
        A &= value;
        SetZeroNegative(A);
        SetFlag(Carry, GetFlag(Negative));
    }

    private void Arr(byte value)
    {
        A &= value;
        A = (byte)((A >> 1) | (GetFlag(Carry) ? 0x80 : 0));
        SetZeroNegative(A);
        SetFlag(Carry, (A & 0x40) != 0);
        SetFlag(Overflow, (((A >> 6) ^ (A >> 5)) & 1) != 0);
    }

    private void Axs(byte value)
    {
        var left = A & X;
        var result = left - value;
        SetFlag(Carry, result >= 0);
        X = (byte)result;
        SetZeroNegative(X);
    }

    private void Xaa(byte value)
    {
        // The physical opcode is unstable and varies between chips. $EE is
        // the conventional 2A03 approximation used to keep execution
        // deterministic when software encounters it.
        A = (byte)((A | 0xEE) & X & value);
        SetZeroNegative(A);
    }

    private void LoadAx(byte value)
    {
        A = X = value;
        SetZeroNegative(value);
    }

    private void StoreHighMasked(ushort baseAddress, ushort address, byte value)
    {
        value &= (byte)((baseAddress >> 8) + 1);
        if (PageCrossed(baseAddress, address))
        {
            address = (ushort)((value << 8) | (address & 0x00FF));
        }

        Write(address, value);
    }

    private void SetZeroNegative(byte value)
    {
        SetFlag(Zero, value == 0);
        SetFlag(Negative, (value & 0x80) != 0);
    }

    private bool GetFlag(byte flag) => (Status & flag) != 0;

    private void SetFlag(byte flag, bool enabled)
    {
        if (enabled)
        {
            Status |= flag;
        }
        else
        {
            Status &= (byte)~flag;
        }
    }

    private static bool PageCrossed(ushort first, ushort second) => (first & 0xFF00) != (second & 0xFF00);

    private static int Bool(bool value) => value ? 1 : 0;
}
