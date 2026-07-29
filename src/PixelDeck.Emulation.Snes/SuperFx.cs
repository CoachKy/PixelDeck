namespace PixelDeck.Emulation.Snes;

/// <summary>
/// The Super FX / GSU: Argonaut's graphics coprocessor, used by Star Fox,
/// Stunt Race FX and Yoshi's Island.
/// </summary>
/// <remarks>
/// Unlike the SA-1 this is not a 65C816 — it is a custom 16-bit core with
/// sixteen general registers, a 512-byte instruction cache, and pixel-plotting
/// hardware. Its instruction set is built around two ideas that keep the
/// encoding dense: prefix opcodes (ALT1/ALT2/ALT3) that reinterpret the next
/// instruction, and register prefixes (TO/FROM/WITH) that redirect the source
/// and destination of the next instruction away from the default R0.
/// </remarks>
internal sealed class SuperFx
{
    private const int GamePakRamSize = 128 * 1024;
    private const int CacheSize = 512;

    // Status flag bits within SFR.
    private const int FlagZero = 1 << 1;
    private const int FlagCarry = 1 << 2;
    private const int FlagSign = 1 << 3;
    private const int FlagOverflow = 1 << 4;
    private const int FlagRunning = 1 << 5;
    private const int FlagRomReadPending = 1 << 6;
    private const int FlagAlt1 = 1 << 8;
    private const int FlagAlt2 = 1 << 9;
    private const int FlagImmediateLow = 1 << 10;
    private const int FlagImmediateHigh = 1 << 11;
    private const int FlagWith = 1 << 12;
    private const int FlagIrq = 1 << 15;

    private readonly SnesCartridge _cartridge;
    private readonly ushort[] _r = new ushort[16];
    private readonly byte[] _ram = new byte[GamePakRamSize];
    private readonly byte[] _cache = new byte[CacheSize];
    private readonly bool[] _cacheValid = new bool[CacheSize / 16];

    private int _sfr;
    private byte _programBank;
    private byte _romBank;
    private byte _ramBank;
    private ushort _cacheBase;
    private byte _screenBase;
    private byte _screenMode;
    private byte _colour;
    private byte _plotOptions;
    private byte _config;
    private byte _clockSelect;
    private byte _backupRamControl;
    private byte _romBuffer;

    // Register prefixes are consumed by the following instruction and then
    // fall back to R0, so they are held separately from the flags.
    private int _sourceRegister;
    private int _destinationRegister;

    private int _cycleBudget;

    // The GSU is pipelined: it has already fetched the instruction after a
    // branch by the time the branch resolves, so that instruction always runs
    // before the jump takes effect. Jumps are therefore staged here and
    // applied one instruction later. -1 means nothing staged.
    private int _delayedJumpTarget = -1;
    private int _delayedJumpBank = -1;
    private long _instructionsSinceStart;

    internal List<(string Location, long Instruction)> ProgramCounterSamples { get; } = [];

    /// <summary>
    /// How often each program address ran. A routine that never reaches STOP
    /// shows up as a handful of very hot addresses, which pins the loop down
    /// far better than periodic sampling.
    /// </summary>
    internal long[] ProgramCounterHistogram { get; } = new long[0x10000];

    /// <summary>Captures registers the first few times a chosen address runs.</summary>
    internal static int WatchProgramCounter { get; set; } = -1;

    internal List<string> WatchSamples { get; } = [];

    // The plot hardware buffers two 8-pixel columns before flushing them to the
    // frame buffer in Game Pak RAM.
    private readonly byte[] _pixelCacheColours = new byte[8];
    private int _pixelCacheOffset = -1;
    private byte _pixelCacheValidBits;

    public SuperFx(SnesCartridge cartridge) => _cartridge = cartridge;

    /// <summary>Instructions retired, for diagnostics.</summary>
    public long ExecutedInstructions { get; private set; }

    /// <summary>
    /// Why and where the core last halted, plus how often each opcode ran. A
    /// GSU that stops early is usually executing something it should not, and
    /// the histogram makes that visible without a full trace.
    /// </summary>
    internal long StopCount { get; private set; }

    internal long StartCount { get; private set; }

    internal ushort LastStopProgramCounter { get; private set; }

    internal byte LastStopProgramBank { get; private set; }

    internal bool LastStopWasHostRequested { get; private set; }

    internal long[] OpcodeHistogram { get; } = new long[256];

    internal string RegisterDump =>
        $"pbr={_programBank:X2} sfr={_sfr:X4} scmr={_screenMode:X2} rombr={_romBank:X2} rambr={_ramBank:X2} " +
        $"cbr={_cacheBase:X4} scbr={_screenBase:X2} colr={_colour:X2} por={_plotOptions:X2}\n" +
        "                " + string.Join(" ", Enumerable.Range(0, 16).Select(i => $"R{i}={_r[i]:X4}"));

    public bool IsRunning => (_sfr & FlagRunning) != 0;

    public ushort ProgramCounter => _r[15];

    public bool IrqPending => (_sfr & FlagIrq) != 0 && (_config & 0x80) == 0;

    // --- S-CPU facing ------------------------------------------------------

    public byte ReadRegister(ushort address)
    {
        if (address is >= 0x3000 and <= 0x301F)
        {
            var index = (address - 0x3000) / 2;
            return (address & 1) == 0 ? (byte)_r[index] : (byte)(_r[index] >> 8);
        }

        if (address is >= 0x3100 and <= 0x32FF)
        {
            return _cache[(address - 0x3100) % CacheSize];
        }

        // Reading the high byte of SFR acknowledges the GSU's interrupt.
        // Without this the flag latches on at the first STOP and never clears:
        // Star Fox's IRQ handler tests it first and returns early when it is
        // set, so the console-side H/V interrupt never gets acknowledged and
        // the CPU re-enters the handler forever instead of running the game.
        if (address == 0x3031)
        {
            var high = (byte)(_sfr >> 8);
            _sfr &= ~FlagIrq;
            return high;
        }

        return address switch
        {
            0x3030 => (byte)_sfr,
            0x3034 => _programBank,
            0x3036 => _romBank,
            0x3038 => _screenBase,
            0x3039 => _config,
            0x303A => _screenMode,
            0x303B => 0x04, // version code
            0x303C => _ramBank,
            0x303E => (byte)_cacheBase,
            0x303F => (byte)(_cacheBase >> 8),
            _ => 0x00
        };
    }

    public void WriteRegister(ushort address, byte value)
    {
        // While the GSU is running the S-CPU must not disturb its registers.
        if (address is >= 0x3000 and <= 0x301F)
        {
            if (IsRunning)
            {
                return;
            }

            var index = (address - 0x3000) / 2;
            _r[index] = (address & 1) == 0
                ? (ushort)((_r[index] & 0xFF00) | value)
                : (ushort)((_r[index] & 0x00FF) | (value << 8));

            // Writing the high byte of R15 is what starts the processor.
            if (index == 15 && (address & 1) != 0)
            {
                Start();
            }

            return;
        }

        if (address is >= 0x3100 and <= 0x32FF)
        {
            var offset = (address - 0x3100) % CacheSize;
            _cache[offset] = value;
            _cacheValid[offset / 16] = true;
            return;
        }

        switch (address)
        {
            case 0x3030:
                // Clearing the go flag halts the processor.
                _sfr = (_sfr & 0xFF00) | value;
                if ((value & FlagRunning) == 0)
                {
                    Stop(hostRequested: true);
                }

                break;
            case 0x3031:
                _sfr = (_sfr & 0x00FF) | (value << 8);
                break;
            case 0x3033:
                _backupRamControl = value;
                break;
            case 0x3034:
                _programBank = value;
                break;
            case 0x3037:
                _clockSelect = value;
                break;
            case 0x3038:
                _screenBase = value;
                break;
            case 0x3039:
                _config = value;
                break;
            case 0x303A:
                _screenMode = value;
                break;
        }
    }

    public byte ReadRam(uint offset) => _ram[offset % GamePakRamSize];

    public void WriteRam(uint offset, byte value) => _ram[offset % GamePakRamSize] = value;

    /// <summary>
    /// True when the GSU currently owns the bus, in which case the S-CPU reads
    /// open bus rather than cartridge data. SCMR bits 4 and 5 arbitrate.
    /// </summary>
    /// <summary>
    /// Whether the GSU currently holds a resource the S-CPU also uses.
    /// </summary>
    /// <remarks>
    /// Hardware hands the S-CPU open bus while the coprocessor owns ROM or
    /// RAM, and well-behaved games avoid reading it. Reproducing that here is
    /// actively harmful while GSU timing is only approximate: the GSU stays
    /// "running" longer than the real chip would, so the S-CPU is starved
    /// mid-routine and ends up executing cartridge data. Star Fox derailed
    /// into bank $72 and took 134 BRKs with strict arbitration, and neither
    /// happens without it. Reporting no contention is the less accurate but
    /// far more robust choice until the timing model is trustworthy.
    /// When it is re-enabled, note that RAN is SCMR bit 3 and RON bit 4 — the
    /// two height bits sit either side of them at bits 2 and 5.
    /// </remarks>
    public bool OwnsRom => false;

    public bool OwnsRam => false;

    private void Start()
    {
        StartCount++;
        _instructionsSinceStart = 0;
        _sfr |= FlagRunning;
        InvalidateCache();
    }

    private void Stop(bool hostRequested = false)
    {
        if (IsRunning)
        {
            StopCount++;
            LastStopProgramCounter = _r[15];
            LastStopProgramBank = _programBank;
            LastStopWasHostRequested = hostRequested;
        }

        _sfr &= ~FlagRunning;
        _sfr |= FlagIrq;
        _sourceRegister = 0;
        _destinationRegister = 0;
        _sfr &= ~(FlagAlt1 | FlagAlt2 | FlagWith);
    }

    private void InvalidateCache() => Array.Clear(_cacheValid);

    /// <summary>
    /// Runs the GSU alongside the S-CPU. The chip is clocked at either 10.74 or
    /// 21.48 MHz depending on CLSR, against the console's 3.58 MHz.
    /// </summary>
    public void Clock(int snesCycles)
    {
        if (!IsRunning)
        {
            _cycleBudget = 0;
            return;
        }

        var multiplier = (_clockSelect & 1) != 0 ? 6 : 3;
        _cycleBudget += snesCycles * multiplier;

        var guard = 0;
        while (_cycleBudget > 0 && IsRunning && guard++ < 1024)
        {
            _cycleBudget -= Step();
            ExecutedInstructions++;
        }

        _cycleBudget = Math.Clamp(_cycleBudget, -64, 512);
    }

    // --- Core --------------------------------------------------------------

    private byte FetchOpcode()
    {
        var pc = _r[15]++;
        return ReadProgramByte(pc);
    }

    private byte ReadProgramByte(ushort address)
    {
        // The cache covers a 512-byte window based at CBR; anything else comes
        // straight from the program bank.
        var cacheOffset = (address - _cacheBase) & 0xFFFF;
        if (cacheOffset < CacheSize && _cacheValid[cacheOffset / 16])
        {
            return _cache[cacheOffset];
        }

        return ReadRomByte(_programBank, address);
    }

    private byte ReadRomByte(byte bank, ushort address)
    {
        var rom = _cartridge.RomSpan;
        if (rom.Length == 0)
        {
            return 0;
        }

        // The GSU sees LoROM in banks $00-$3F and a linear image in $40-$5F.
        int linear;
        if (bank <= 0x3F)
        {
            if (address < 0x8000)
            {
                return 0;
            }

            linear = (bank << 15) | (address & 0x7FFF);
        }
        else
        {
            linear = ((bank - 0x40) << 16) | address;
        }

        return rom[linear % rom.Length];
    }

    private int Register(int index) => _r[index];

    /// <summary>Stages a jump to take effect after the next instruction.</summary>
    private void ScheduleJump(ushort target, int bank = -1)
    {
        _delayedJumpTarget = target;
        _delayedJumpBank = bank;
    }

    private void SetRegister(int index, ushort value)
    {
        // Writing the program counter as an instruction's destination is a
        // jump, and takes the same delay slot as a branch.
        if (index == 15)
        {
            ScheduleJump(value);
            return;
        }

        _r[index] = value;

        // R14 is the ROM pointer: writing it starts a buffered fetch that the
        // GETB family later collects. Hardware takes several cycles over it;
        // the fetch is done eagerly here and the latency is not modelled.
        if (index == 14)
        {
            _romBuffer = ReadRomByte(_romBank, value);
        }
    }

    private void SetZeroSign(ushort value)
    {
        _sfr &= ~(FlagZero | FlagSign);
        if (value == 0) _sfr |= FlagZero;
        if ((value & 0x8000) != 0) _sfr |= FlagSign;
    }

    /// <summary>
    /// Flags for instructions whose result is a byte. Their sign comes from bit
    /// 7, not bit 15, so testing the word would report every byte as positive.
    /// </summary>
    private void SetByteZeroSign(ushort value)
    {
        _sfr &= ~(FlagZero | FlagSign);
        if (value == 0) _sfr |= FlagZero;
        if ((value & 0x80) != 0) _sfr |= FlagSign;
    }

    private void EndInstruction()
    {
        _sourceRegister = 0;
        _destinationRegister = 0;
        _sfr &= ~(FlagAlt1 | FlagAlt2 | FlagWith);
    }

    private int SourceValue => _r[_sourceRegister];

    /// <summary>Executes one instruction; returns the cycles it cost.</summary>
    private int Step()
    {
        // A jump staged by the previous instruction lands after this one runs,
        // so capture it first and apply it on the way out.
        var stagedTarget = _delayedJumpTarget;
        var stagedBank = _delayedJumpBank;
        _delayedJumpTarget = -1;
        _delayedJumpBank = -1;

        // Periodically sample the program counter while running, so a routine
        // that never reaches STOP can be located without a full trace.
        if (++_instructionsSinceStart % 250_000 == 0 && ProgramCounterSamples.Count < 24)
        {
            ProgramCounterSamples.Add(($"{_programBank:X2}:{_r[15]:X4}", _instructionsSinceStart));
        }

        ProgramCounterHistogram[_r[15]]++;

        if (_r[15] == WatchProgramCounter && WatchSamples.Count < 12)
        {
            WatchSamples.Add(
                $"R0={_r[0]:X4} R1={_r[1]:X4} R4={_r[4]:X4} R7={_r[7]:X4} R9={_r[9]:X4} " +
                $"R12={_r[12]:X4} R14={_r[14]:X4} rombr={_romBank:X2} buf={_romBuffer:X2} sfr={_sfr:X4}");
        }

        var cycles = Execute();

        if (stagedTarget >= 0)
        {
            _r[15] = (ushort)stagedTarget;
            if (stagedBank >= 0)
            {
                _programBank = (byte)stagedBank;
                _cacheBase = (ushort)(_r[15] & 0xFFF0);
                InvalidateCache();
            }
        }

        return cycles;
    }

    private int Execute()
    {
        var opcode = FetchOpcode();
        OpcodeHistogram[opcode]++;
        var alt1 = (_sfr & FlagAlt1) != 0;
        var alt2 = (_sfr & FlagAlt2) != 0;

        switch (opcode)
        {
            case 0x00: // STOP
                Stop();
                EndInstruction();
                return 1;

            case 0x01: // NOP
                EndInstruction();
                return 1;

            case 0x02: // CACHE
                _cacheBase = (ushort)(_r[15] & 0xFFF0);
                InvalidateCache();
                EndInstruction();
                return 1;

            case 0x03: // LSR
            {
                var source = (ushort)SourceValue;
                _sfr = (source & 1) != 0 ? _sfr | FlagCarry : _sfr & ~FlagCarry;
                var result = (ushort)(source >> 1);
                SetRegister(_destinationRegister, result);
                SetZeroSign(result);
                EndInstruction();
                return 1;
            }

            case 0x04: // ROL
            {
                var source = (ushort)SourceValue;
                var carryIn = (_sfr & FlagCarry) != 0 ? 1 : 0;
                _sfr = (source & 0x8000) != 0 ? _sfr | FlagCarry : _sfr & ~FlagCarry;
                var result = (ushort)((source << 1) | carryIn);
                SetRegister(_destinationRegister, result);
                SetZeroSign(result);
                EndInstruction();
                return 1;
            }

            case >= 0x05 and <= 0x0F: // branches
                return Branch(opcode);

            case >= 0x10 and <= 0x1F: // TO Rn / MOVE when WITH is active
            {
                var index = opcode & 0x0F;
                if ((_sfr & FlagWith) != 0)
                {
                    SetRegister(index, (ushort)SourceValue);
                    EndInstruction();
                    return 1;
                }

                _destinationRegister = index;
                return 1; // prefix: flags survive
            }

            case >= 0x20 and <= 0x2F: // WITH Rn
                _sourceRegister = opcode & 0x0F;
                _destinationRegister = opcode & 0x0F;
                _sfr |= FlagWith;
                return 1;

            case >= 0x30 and <= 0x3B: // STW/STB (Rn)
            {
                var address = (ushort)_r[opcode & 0x0F];
                SetLastRamAddress(address);
                var value = (ushort)SourceValue;
                if (alt1)
                {
                    WriteRamByte(address, (byte)value);
                }
                else
                {
                    WriteRamByte(address, (byte)value);
                    WriteRamByte((ushort)(address + 1), (byte)(value >> 8));
                }

                EndInstruction();
                return 2;
            }

            case 0x3C: // LOOP
            {
                var counter = (ushort)(_r[12] - 1);
                _r[12] = counter;
                SetZeroSign(counter);
                if (counter != 0)
                {
                    ScheduleJump(_r[13]);
                }

                EndInstruction();
                return 1;
            }

            case 0x3D: // ALT1
                _sfr |= FlagAlt1;
                return 1;

            case 0x3E: // ALT2
                _sfr |= FlagAlt2;
                return 1;

            case 0x3F: // ALT3
                _sfr |= FlagAlt1 | FlagAlt2;
                return 1;

            case >= 0x40 and <= 0x4B: // LDW/LDB (Rn)
            {
                var address = (ushort)_r[opcode & 0x0F];
                SetLastRamAddress(address);
                ushort value;
                if (alt1)
                {
                    value = ReadRamByte(address);
                }
                else
                {
                    value = (ushort)(ReadRamByte(address) | (ReadRamByte((ushort)(address + 1)) << 8));
                }

                SetRegister(_destinationRegister, value);
                SetZeroSign(value);
                EndInstruction();
                return 2;
            }

            case 0x4C: // PLOT / RPIX
                if (alt1)
                {
                    var value = ReadPixel(_r[1], _r[2]);
                    SetRegister(_destinationRegister, value);
                    SetZeroSign(value);
                }
                else
                {
                    PlotPixel(_r[1], _r[2]);
                    _r[1]++;
                }

                EndInstruction();
                return 1;

            case 0x4D: // SWAP
            {
                var source = (ushort)SourceValue;
                var result = (ushort)((source >> 8) | (source << 8));
                SetRegister(_destinationRegister, result);
                SetZeroSign(result);
                EndInstruction();
                return 1;
            }

            case 0x4E: // COLOR / CMODE
                if (alt1)
                {
                    _plotOptions = (byte)SourceValue;
                }
                else
                {
                    _colour = TranslateColour((byte)SourceValue);
                }

                EndInstruction();
                return 1;

            case 0x4F: // NOT
            {
                var result = (ushort)~SourceValue;
                SetRegister(_destinationRegister, result);
                SetZeroSign(result);
                EndInstruction();
                return 1;
            }

            case >= 0x50 and <= 0x5F: // ADD / ADC / ADD #n / ADC #n
            {
                var operand = alt2 ? opcode & 0x0F : _r[opcode & 0x0F];
                var carry = alt1 && (_sfr & FlagCarry) != 0 ? 1 : 0;
                Add((ushort)SourceValue, (ushort)operand, carry);
                EndInstruction();
                return 1;
            }

            case >= 0x60 and <= 0x6F: // SUB / SBC / SUB #n / CMP
            {
                var operand = alt2 && !alt1 ? opcode & 0x0F : _r[opcode & 0x0F];
                var borrow = alt1 && !alt2 && (_sfr & FlagCarry) == 0 ? 1 : 0;
                var compareOnly = alt1 && alt2;
                Subtract((ushort)SourceValue, (ushort)operand, borrow, compareOnly);
                EndInstruction();
                return 1;
            }

            case 0x70: // MERGE
            {
                var result = (ushort)((_r[7] & 0xFF00) | (_r[8] >> 8));
                SetRegister(_destinationRegister, result);
                SetZeroSign(result);
                EndInstruction();
                return 1;
            }

            case >= 0x71 and <= 0x7F: // AND / BIC / AND #n / BIC #n
            {
                var operand = alt2 ? opcode & 0x0F : _r[opcode & 0x0F];
                if (alt1)
                {
                    operand = ~operand;
                }

                var result = (ushort)(SourceValue & operand);
                SetRegister(_destinationRegister, result);
                SetZeroSign(result);
                EndInstruction();
                return 1;
            }

            case >= 0x80 and <= 0x8F: // MULT / UMULT / MULT #n / UMULT #n
            {
                var operand = alt2 ? opcode & 0x0F : _r[opcode & 0x0F];
                int result;
                if (alt1)
                {
                    result = (byte)SourceValue * (byte)operand;
                }
                else
                {
                    result = (sbyte)SourceValue * (sbyte)operand;
                }

                SetRegister(_destinationRegister, (ushort)result);
                SetZeroSign((ushort)result);
                EndInstruction();
                return 2;
            }

            case 0x90: // SBK
            {
                // The high byte lands at the address with its low bit flipped,
                // not simply one higher: for an odd last-address that is one
                // byte *below*, which is how the hardware keeps a word write
                // inside its aligned pair.
                var value = (ushort)SourceValue;
                WriteRamByte(_lastRamAddress, (byte)value);
                WriteRamByte((ushort)(_lastRamAddress ^ 1), (byte)(value >> 8));
                EndInstruction();
                return 2;
            }

            case >= 0x91 and <= 0x94: // LINK #n
                _r[11] = (ushort)(_r[15] + (opcode & 0x0F));
                EndInstruction();
                return 1;

            case 0x95: // SEX
            {
                var result = (ushort)(sbyte)SourceValue;
                SetRegister(_destinationRegister, result);
                SetZeroSign(result);
                EndInstruction();
                return 1;
            }

            case 0x96: // ASR / DIV2
            {
                var source = (short)SourceValue;
                _sfr = (source & 1) != 0 ? _sfr | FlagCarry : _sfr & ~FlagCarry;
                if (alt1 && source == -1)
                {
                    source = 0;
                }
                else
                {
                    source >>= 1;
                }

                SetRegister(_destinationRegister, (ushort)source);
                SetZeroSign((ushort)source);
                EndInstruction();
                return 1;
            }

            case 0x97: // ROR
            {
                var source = (ushort)SourceValue;
                var carryIn = (_sfr & FlagCarry) != 0 ? 0x8000 : 0;
                _sfr = (source & 1) != 0 ? _sfr | FlagCarry : _sfr & ~FlagCarry;
                var result = (ushort)((source >> 1) | carryIn);
                SetRegister(_destinationRegister, result);
                SetZeroSign(result);
                EndInstruction();
                return 1;
            }

            case >= 0x98 and <= 0x9D: // JMP / LJMP Rn
            {
                var index = opcode & 0x0F;
                ScheduleJump(_r[index], alt1 ? (byte)SourceValue : -1);
                EndInstruction();
                return 1;
            }

            case 0x9E: // LOB
            {
                var result = (ushort)(SourceValue & 0x00FF);
                SetRegister(_destinationRegister, result);
                SetByteZeroSign(result);
                EndInstruction();
                return 1;
            }

            case 0x9F: // FMULT / LMULT
            {
                var product = (short)SourceValue * (short)_r[6];
                if (alt1)
                {
                    _r[4] = (ushort)product;
                }

                var result = (ushort)(product >> 16);
                SetRegister(_destinationRegister, result);
                SetZeroSign(result);
                _sfr = (product & 0x8000) != 0 ? _sfr | FlagCarry : _sfr & ~FlagCarry;
                EndInstruction();
                return 8;
            }

            case >= 0xA0 and <= 0xAF: // IBT / LMS / SMS
            {
                var index = opcode & 0x0F;
                var immediate = FetchOpcode();
                if (alt1)
                {
                    var address = (ushort)(immediate << 1);
                    SetLastRamAddress(address);
                    SetRegister(
                        index,
                        (ushort)(ReadRamByte(address) | (ReadRamByte((ushort)(address + 1)) << 8)));
                }
                else if (alt2)
                {
                    var address = (ushort)(immediate << 1);
                    SetLastRamAddress(address);
                    WriteRamByte(address, (byte)_r[index]);
                    WriteRamByte((ushort)(address + 1), (byte)(_r[index] >> 8));
                }
                else
                {
                    // Routed through SetRegister so that loading R15 takes the
                    // branch delay slot, which is how the assembler emits an
                    // unconditional jump (an immediate load of R15 followed by
                    // a NOP).
                    SetRegister(index, (ushort)(sbyte)immediate);
                }

                EndInstruction();
                return 1;
            }

            case >= 0xB0 and <= 0xBF: // FROM Rn / MOVES
            {
                var index = opcode & 0x0F;
                if ((_sfr & FlagWith) != 0)
                {
                    var value = (ushort)_r[index];
                    SetRegister(_destinationRegister, value);
                    SetZeroSign(value);
                    EndInstruction();
                    return 1;
                }

                _sourceRegister = index;
                return 1;
            }

            case 0xC0: // HIB
            {
                var result = (ushort)(SourceValue >> 8);
                SetRegister(_destinationRegister, result);
                SetByteZeroSign(result);
                EndInstruction();
                return 1;
            }

            case >= 0xC1 and <= 0xCF: // OR / XOR / OR #n / XOR #n
            {
                var operand = alt2 ? opcode & 0x0F : _r[opcode & 0x0F];
                var result = (ushort)(alt1 ? SourceValue ^ operand : SourceValue | operand);
                SetRegister(_destinationRegister, result);
                SetZeroSign(result);
                EndInstruction();
                return 1;
            }

            case >= 0xD0 and <= 0xDE: // INC Rn
            {
                var index = opcode & 0x0F;
                var result = (ushort)(_r[index] + 1);
                // Through SetRegister so that stepping R14 re-arms the ROM
                // buffer that GETB reads; walking ROM with INC R14 / GETB is a
                // standard idiom and would otherwise return a stale byte.
                SetRegister(index, result);
                SetZeroSign(result);
                EndInstruction();
                return 1;
            }

            case 0xDF: // GETC / RAMB / ROMB
                if (alt2 && alt1)
                {
                    _romBank = (byte)SourceValue;
                }
                else if (alt2)
                {
                    _ramBank = (byte)(SourceValue & 1);
                }
                else
                {
                    _colour = TranslateColour(_romBuffer);
                }

                EndInstruction();
                return 1;

            case >= 0xE0 and <= 0xEE: // DEC Rn
            {
                var index = opcode & 0x0F;
                var result = (ushort)(_r[index] - 1);
                SetRegister(index, result);
                SetZeroSign(result);
                EndInstruction();
                return 1;
            }

            case 0xEF: // GETB / GETBH / GETBL / GETBS
            {
                ushort result;
                if (alt1 && alt2)
                {
                    result = (ushort)(sbyte)_romBuffer;
                }
                else if (alt1)
                {
                    result = (ushort)((_romBuffer << 8) | (SourceValue & 0x00FF));
                }
                else if (alt2)
                {
                    result = (ushort)((SourceValue & 0xFF00) | _romBuffer);
                }
                else
                {
                    result = _romBuffer;
                }

                SetRegister(_destinationRegister, result);
                EndInstruction();
                return 1;
            }

            default: // $F0-$FF: IWT / LM / SM
            {
                var index = opcode & 0x0F;
                var low = FetchOpcode();
                var high = FetchOpcode();
                var immediate = (ushort)(low | (high << 8));
                if (alt1)
                {
                    SetLastRamAddress(immediate);
                    SetRegister(
                        index,
                        (ushort)(ReadRamByte(immediate) | (ReadRamByte((ushort)(immediate + 1)) << 8)));
                }
                else if (alt2)
                {
                    SetLastRamAddress(immediate);
                    WriteRamByte(immediate, (byte)_r[index]);
                    WriteRamByte((ushort)(immediate + 1), (byte)(_r[index] >> 8));
                }
                else
                {
                    SetRegister(index, immediate);
                }

                EndInstruction();
                return 1;
            }
        }
    }

    private ushort _lastRamAddress;

    private int Branch(byte opcode)
    {
        var offset = (sbyte)FetchOpcode();
        var zero = (_sfr & FlagZero) != 0;
        var carry = (_sfr & FlagCarry) != 0;
        var sign = (_sfr & FlagSign) != 0;
        var overflow = (_sfr & FlagOverflow) != 0;

        var take = opcode switch
        {
            0x05 => true,               // BRA
            0x06 => sign == overflow,   // BGE
            0x07 => sign != overflow,   // BLT
            0x08 => !zero,              // BNE
            0x09 => zero,               // BEQ
            0x0A => !sign,              // BPL
            0x0B => sign,               // BMI
            0x0C => !carry,             // BCC
            0x0D => carry,              // BCS
            0x0E => !overflow,          // BVC
            0x0F => overflow,           // BVS
            _ => false
        };

        if (take)
        {
            ScheduleJump((ushort)(_r[15] + offset));
        }

        EndInstruction();
        return 1;
    }

    private void Add(ushort left, ushort right, int carryIn)
    {
        var result = left + right + carryIn;
        _sfr = (result & 0x10000) != 0 ? _sfr | FlagCarry : _sfr & ~FlagCarry;
        var overflow = (~(left ^ right) & (left ^ result) & 0x8000) != 0;
        _sfr = overflow ? _sfr | FlagOverflow : _sfr & ~FlagOverflow;
        var value = (ushort)result;
        SetRegister(_destinationRegister, value);
        SetZeroSign(value);
    }

    private void Subtract(ushort left, ushort right, int borrow, bool compareOnly)
    {
        var result = left - right - borrow;
        _sfr = result >= 0 ? _sfr | FlagCarry : _sfr & ~FlagCarry;
        var overflow = ((left ^ right) & (left ^ result) & 0x8000) != 0;
        _sfr = overflow ? _sfr | FlagOverflow : _sfr & ~FlagOverflow;
        var value = (ushort)result;
        if (!compareOnly)
        {
            SetRegister(_destinationRegister, value);
        }

        SetZeroSign(value);
    }

    // --- Game Pak RAM ------------------------------------------------------

    /// <summary>
    /// Plot-buffer access. The screen base is <c>SCBR &lt;&lt; 10</c>, which reaches
    /// past 64 KiB, so these index Game Pak RAM directly rather than going
    /// through the 16-bit address and RAM bank register the load/store
    /// instructions use — the plot hardware is not banked.
    /// </summary>
    private byte ReadPlotByte(int offset) => _ram[offset % GamePakRamSize];

    private void WritePlotByte(int offset, byte value) => _ram[offset % GamePakRamSize] = value;

    private byte ReadRamByte(ushort address) =>
        _ram[(((_ramBank & 1) << 16) | address) % GamePakRamSize];

    private void WriteRamByte(ushort address, byte value) =>
        _ram[(((_ramBank & 1) << 16) | address) % GamePakRamSize] = value;

    /// <summary>
    /// Records the address a memory instruction worked from, which is what SBK
    /// later stores back to.
    /// </summary>
    /// <remarks>
    /// This is the <em>base</em> address of the access, so it must be set once
    /// per instruction rather than per byte: a word access touches base and
    /// base+1, and letting the second byte win leaves SBK writing one byte too
    /// high. Star Fox keeps its polygon loop counter in RAM and decrements it
    /// with LMS/DEC/SBK, so an off-by-one there meant the counter was never
    /// updated and the routine never terminated.
    /// </remarks>
    private void SetLastRamAddress(ushort address) => _lastRamAddress = address;

    // --- Plot hardware -----------------------------------------------------

    private int ColourDepth => (_screenMode & 3) switch
    {
        0 => 2,
        1 => 4,
        _ => 8
    };

    /// <summary>
    /// Loads COLR from a source byte, applying the two plot options that
    /// rewrite it: bit 2 swaps the nibbles, and bit 3 keeps the existing high
    /// nibble so only the low one is replaced.
    /// </summary>
    private byte TranslateColour(byte value)
    {
        if ((_plotOptions & 0x04) != 0)
        {
            value = (byte)((value >> 4) | (value << 4));
        }

        return (_plotOptions & 0x08) != 0
            ? (byte)((_colour & 0xF0) | (value & 0x0F))
            : value;
    }

    /// <summary>
    /// Decides whether a colour counts as transparent and so is not drawn.
    /// Plot option bit 0 disables the test entirely; what "zero" means depends
    /// on the colour depth, and in eight-bit mode on whether the chip is in
    /// object mode and whether only the low nibble is being written.
    /// </summary>
    private bool IsTransparent(byte colour)
    {
        if ((_plotOptions & 0x01) != 0)
        {
            return false;
        }

        if (ColourDepth != 8)
        {
            return (colour & 0x0F) == 0;
        }

        if ((_plotOptions & 0x10) != 0)
        {
            return colour == 0;
        }

        return colour == 0 || ((_plotOptions & 0x08) != 0 && (colour & 0x0F) == 0);
    }

    private void PlotPixel(ushort x, ushort y)
    {
        // Dithering alternates between the two nibbles of COLR on a checkerboard.
        var colour = (_plotOptions & 0x02) != 0 && ((x ^ y) & 1) != 0
            ? (byte)(_colour >> 4)
            : _colour;

        if (IsTransparent(colour))
        {
            return;
        }

        var offset = PixelOffset(x, y);
        if (offset != _pixelCacheOffset)
        {
            FlushPixelCache();
            _pixelCacheOffset = offset;
        }

        _pixelCacheColours[x & 7] = colour;
        _pixelCacheValidBits |= (byte)(1 << (x & 7));
    }

    private ushort ReadPixel(ushort x, ushort y)
    {
        FlushPixelCache();
        var offset = PixelOffset(x, y);
        var depth = ColourDepth;
        var result = 0;
        for (var plane = 0; plane < depth; plane++)
        {
            var b = ReadPlotByte(offset + ((plane & ~1) * 8) + (plane & 1));
            result |= ((b >> (7 - (x & 7))) & 1) << plane;
        }

        return (ushort)result;
    }

    /// <summary>
    /// Converts pixel coordinates to the byte offset of the containing 8x8
    /// character in the plot buffer. The buffer is a column-major grid of
    /// characters whose height comes from SCMR.
    /// </summary>
    private int PixelOffset(ushort x, ushort y)
    {
        var characterX = x >> 3;
        var characterY = y >> 3;
        var character = (characterX * (PlotBufferHeight >> 3)) + characterY;

        // Each row of a plane pair occupies two bytes, so the row term is
        // doubled; the plane pairs themselves are sixteen bytes apart and that
        // offset is added when the cache is flushed.
        return (_screenBase << 10) + (character * 8 * ColourDepth) + ((y & 7) * 2);
    }

    /// <summary>
    /// Plot buffer height from SCMR. The two height bits are deliberately not
    /// adjacent: HT0 is bit 2 and HT1 is bit 5, with the RAM and ROM access
    /// bits sitting between them.
    /// </summary>
    private int PlotBufferHeight =>
        (_plotOptions & 0x10) != 0
            ? 256 // object mode always uses the full height
            : (((_screenMode >> 4) & 0x02) | ((_screenMode >> 2) & 0x01)) switch
            {
                0 => 128,
                1 => 160,
                2 => 192,
                _ => 256
            };

    private void FlushPixelCache()
    {
        if (_pixelCacheOffset < 0 || _pixelCacheValidBits == 0)
        {
            _pixelCacheValidBits = 0;
            return;
        }

        var depth = ColourDepth;
        for (var plane = 0; plane < depth; plane++)
        {
            var planeOffset = _pixelCacheOffset + ((plane & ~1) * 8) + (plane & 1);
            var existing = ReadPlotByte(planeOffset);
            for (var bit = 0; bit < 8; bit++)
            {
                if ((_pixelCacheValidBits & (1 << bit)) == 0)
                {
                    continue;
                }

                var mask = 1 << (7 - bit);
                existing = (byte)(((_pixelCacheColours[bit] >> plane) & 1) != 0
                    ? existing | mask
                    : existing & ~mask);
            }

            WritePlotByte(planeOffset, existing);
        }

        _pixelCacheValidBits = 0;
    }
}
