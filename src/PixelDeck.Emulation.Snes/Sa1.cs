namespace PixelDeck.Emulation.Snes;

/// <summary>
/// The SA-1 coprocessor: a second 65C816 running at roughly three times the
/// console's clock, with its own RAM, memory mapper, timers, arithmetic unit
/// and DMA controller. Both processors share the cartridge ROM and a block of
/// backup RAM, and coordinate through the register file at $2200-$23FF.
/// </summary>
internal sealed class Sa1 : I65816Bus
{
    /// <summary>SA-1 internal RAM, visible to both processors at $3000-$37FF.</summary>
    public const int InternalRamSize = 2 * 1024;

    /// <summary>Backup work RAM, up to 256 KiB.</summary>
    public const int BackupRamSize = 256 * 1024;

    private readonly SnesCartridge _cartridge;
    private readonly byte[] _internalRam = new byte[InternalRamSize];
    private readonly byte[] _backupRam = new byte[BackupRamSize];
    private readonly Cpu65816 _cpu;

    // Bank mapping. Each of the four 1 MiB super-MMC slots selects which
    // 1 MiB block of ROM appears at banks $C0-$FF, and whether the
    // corresponding $00-$3F/$80-$BF window is remapped with it.
    private readonly byte[] _superMmcBanks = [0x00, 0x01, 0x02, 0x03];
    private readonly bool[] _superMmcRemap = new bool[4];

    // $2200 CCNT / $2209 SCNT: the two processors' control registers. Each can
    // request an interrupt, a reset, or a wait state on the other.
    private byte _sa1Control = 0x20;
    private byte _snesControl;
    private byte _sa1InterruptEnable;
    private byte _snesInterruptEnable;
    private byte _sa1InterruptFlags;
    private byte _snesInterruptFlags;

    // Vectors the SA-1 fetches on reset/NMI/IRQ, supplied by the S-CPU rather
    // than read from ROM, so the S-CPU controls where the SA-1 starts.
    private ushort _sa1ResetVector;
    private ushort _sa1NmiVector;
    private ushort _sa1IrqVector;
    private ushort _snesNmiVector;
    private ushort _snesIrqVector;

    // Backup RAM windowing: $2224/$2225 select which 8 KiB block appears at
    // $6000-$7FFF for the S-CPU and SA-1 respectively.
    private byte _snesBackupRamBlock;
    private byte _sa1BackupRamBlock;
    private bool _sa1BackupRamBitmapMode;

    // $2250-$2254: multiply, divide and multiply-accumulate.
    private byte _arithmeticControl;
    private short _arithmeticOperandA;
    private short _arithmeticOperandB;
    private long _arithmeticResult;

    // $2210-$2215: an H/V timer that counts the SA-1's own dots and lines, or
    // an 18-bit linear counter when bit 7 of $2210 is set. Either way it raises
    // the timer interrupt (flag $40) when it reaches the compare values.
    private byte _timerControl;
    private ushort _timerHorizontalCompare;
    private ushort _timerVerticalCompare;
    private ushort _timerHorizontalCounter;
    private ushort _timerVerticalCounter;
    private int _linearTimerCounter;

    // $2258-$225B: variable-length bit processing, used to walk compressed
    // bitstreams without the CPU doing the shifting.
    private byte _bitLength;
    private bool _bitAutoIncrement;
    private uint _bitAddress;
    private int _bitOffset;

    // $2230-$2239: the SA-1's own DMA controller.
    private byte _dmaControl;
    private byte _characterConversionControl;
    private uint _dmaSource;
    private uint _dmaDestination;
    private ushort _dmaTerminalCounter;

    /// <summary>Instructions the SA-1 may retire in one Clock call, so a
    /// runaway core cannot stall the console thread.</summary>
    private const int MaximumInstructionsPerClock = 512;

    /// <summary>Cycle debt is carried between calls but bounded, so a long
    /// halt cannot bank enough credit to run a whole frame at once.</summary>
    private const int MaximumCycleDebt = 128;

    /// <summary>Unspent budget is bounded too: if the instruction guard trips,
    /// the surplus is discarded rather than compounding into a backlog the
    /// coprocessor can never work off.</summary>
    private const int MaximumCycleSurplus = 1024;

    // Timer geometry: 341 dots per line, 262 lines per field, and an 18-bit
    // free-running counter in linear mode.
    private const int DotsPerLine = 341;
    private const int HvTimerPeriod = DotsPerLine * 262;
    private const int LinearTimerPeriod = 1 << 18;

    private int _cycleBudget;
    private int _contentionCycles;
    private bool _stopped = true;

    public Sa1(SnesCartridge cartridge)
    {
        _cartridge = cartridge;
        _cpu = new Cpu65816(this);
    }

    /// <summary>Instructions retired by the SA-1 core, for diagnostics.</summary>
    public long ExecutedInstructions { get; private set; }

    public uint ProgramAddress => _cpu.ProgramAddress;

    /// <summary>$2200 as last written by the S-CPU: bit 5 reset, bit 6 wait.</summary>
    public byte ControlRegister => _sa1Control;

    public long DmaCount { get; private set; }

    /// <summary>Register state behind an inter-processor stall, for diagnostics.</summary>
    internal Sa1Snapshot Snapshot => new(
        _sa1Control,
        _snesControl,
        _sa1InterruptEnable,
        _sa1InterruptFlags,
        _snesInterruptEnable,
        _snesInterruptFlags,
        _timerControl,
        _sa1ResetVector,
        _sa1NmiVector,
        _sa1IrqVector,
        _timerHorizontalCompare,
        _timerVerticalCompare,
        _superMmcRemap[0] ? _superMmcBanks[0] : (byte)0,
        _internalRam[0],
        _internalRam[1],
        _internalRam[2]);

    /// <summary>
    /// $220A enables three sources independently: bit 7 an interrupt from the
    /// S-CPU, bit 6 the timer, bit 5 DMA completion.
    /// </summary>
    public bool IrqPending => (_sa1InterruptFlags & _sa1InterruptEnable & 0xE0) != 0;

    public bool ConsumeNmi()
    {
        if ((_sa1InterruptFlags & _sa1InterruptEnable & 0x10) == 0)
        {
            return false;
        }

        _sa1InterruptFlags &= 0xEF;
        return true;
    }

    /// <summary>
    /// Runs the SA-1 alongside the S-CPU. The coprocessor's 10.74 MHz clock is
    /// three times the console's 3.58 MHz, so each S-CPU cycle buys three SA-1
    /// cycles. Instructions are charged against that budget rather than being
    /// run one per tick, and any left-over debt carries into the next call so
    /// long instructions do not silently execute for free.
    /// </summary>
    public void Clock(int snesCycles)
    {
        // $2200: bit 6 holds the SA-1 in a wait state, bit 5 in reset. Either
        // way it stops, but the timer keeps counting because it is driven by
        // the clock rather than the core.
        var halted = _stopped || (_sa1Control & 0x60) != 0;
        _cycleBudget += snesCycles * 3;

        if (halted)
        {
            AdvanceTimer(_cycleBudget);
            _cycleBudget = 0;
            return;
        }

        // A single pathological instruction should not be able to run the
        // budget arbitrarily negative, so cap the catch-up per call.
        var guard = 0;
        while (_cycleBudget > 0 && guard++ < MaximumInstructionsPerClock)
        {
            var cycles = Math.Max(1, _cpu.Step());
            ExecutedInstructions++;

            // Both processors share the ROM bus, internal RAM and backup RAM.
            // Whenever the SA-1 touched something the S-CPU also owns, the
            // arbiter costs it extra cycles rather than letting it run free.
            cycles += ConsumeContentionPenalty();

            _cycleBudget -= cycles;
            AdvanceTimer(cycles);
        }

        // Clamp both directions. Debt is capped so a halted core cannot bank
        // enough credit to run a whole frame at once, and surplus is capped so
        // that hitting the instruction guard drops the unspent budget instead
        // of accumulating it forever and leaving the SA-1 permanently behind.
        _cycleBudget = Math.Clamp(_cycleBudget, -MaximumCycleDebt, MaximumCycleSurplus);
    }

    /// <summary>
    /// Returns and clears the wait states accrued this instruction from
    /// accesses to memory the S-CPU also drives.
    /// </summary>
    private int ConsumeContentionPenalty()
    {
        var penalty = _contentionCycles;
        _contentionCycles = 0;
        return penalty;
    }

    /// <summary>
    /// Advances the SA-1's timer by a whole block of cycles. In H/V mode it
    /// counts 341 dots per line and 262 lines per field, matching the console's
    /// own geometry; in linear mode it is a free-running 18-bit counter.
    /// </summary>
    /// <remarks>
    /// The counters are readable at $2302-$2305 whether or not the compare bits
    /// in $2210 are set, so they always advance; only the comparison is gated.
    /// Stepping one cycle at a time would run this tens of millions of times a
    /// second, so the compare is solved arithmetically instead: the block of
    /// cycles is a contiguous sweep, and the only question is whether it passes
    /// over a matching counter value.
    /// </remarks>
    private void AdvanceTimer(int cycles)
    {
        if (cycles <= 0)
        {
            return;
        }

        if ((_timerControl & 0x80) != 0)
        {
            var start = _linearTimerCounter;
            _linearTimerCounter = (_linearTimerCounter + cycles) & 0x3FFFF;
            if ((_timerControl & 0x03) != 0 &&
                SweepCrossesBand(
                    start,
                    cycles,
                    ((_timerVerticalCompare << 9) | _timerHorizontalCompare) & 0x3FFFF,
                    1,
                    LinearTimerPeriod))
            {
                _sa1InterruptFlags |= 0x40;
            }

            return;
        }

        var position = (_timerVerticalCounter * DotsPerLine) + _timerHorizontalCounter;
        var advanced = (position + cycles) % HvTimerPeriod;
        _timerHorizontalCounter = (ushort)(advanced % DotsPerLine);
        _timerVerticalCounter = (ushort)(advanced / DotsPerLine);

        // $2210 bit 0 compares the dot counter and bit 1 the line counter. With
        // only one of them enabled the match is a whole line or a whole field's
        // worth of dots rather than a single position.
        var crossed = (_timerControl & 0x03) switch
        {
            0x01 => SweepCrossesBand(
                position % DotsPerLine, cycles, _timerHorizontalCompare, 1, DotsPerLine),
            0x02 => SweepCrossesBand(
                position, cycles, _timerVerticalCompare * DotsPerLine, DotsPerLine, HvTimerPeriod),
            0x03 => SweepCrossesBand(
                position,
                cycles,
                (_timerVerticalCompare * DotsPerLine) + _timerHorizontalCompare,
                1,
                HvTimerPeriod),
            _ => false
        };

        if (crossed)
        {
            _sa1InterruptFlags |= 0x40;
        }
    }

    /// <summary>
    /// Tests whether the <paramref name="cycles"/> counter values following
    /// <paramref name="start"/> include any position inside the wrapping band
    /// [<paramref name="bandStart"/>, bandStart + <paramref name="bandLength"/>).
    /// </summary>
    private static bool SweepCrossesBand(int start, int cycles, int bandStart, int bandLength, int period)
    {
        if (cycles >= period || bandStart >= period)
        {
            return cycles >= period;
        }

        // Offset of the first swept value from the start of the band. The sweep
        // begins at start + 1 because the counter increments before comparing.
        var offset = (start + 1 - bandStart + period) % period;
        return offset < bandLength || cycles >= period - offset + 1;
    }

    /// <summary>
    /// Reads <see cref="_bitLength"/> bits from the bitstream at the current
    /// address, most significant bit first, advancing the pointer when the
    /// register file is in auto-increment mode.
    /// </summary>
    private ushort ReadVariableLengthBits()
    {
        var length = _bitLength == 0 ? 16 : _bitLength;
        var value = 0;
        for (var index = 0; index < length; index++)
        {
            var bitIndex = _bitOffset + index;
            var source = CpuRead((uint)(_bitAddress + (bitIndex >> 3)));
            var bit = (source >> (7 - (bitIndex & 7))) & 1;
            value = (value << 1) | bit;
        }

        if (_bitAutoIncrement)
        {
            AdvanceBitPointer(length);
        }

        return (ushort)value;
    }

    private void AdvanceBitPointer(int length)
    {
        _bitOffset += length;
        _bitAddress += (uint)(_bitOffset >> 3);
        _bitOffset &= 7;
    }

    /// <summary>
    /// Runs a normal SA-1 DMA. Source and destination each select ROM, backup
    /// RAM or internal RAM; character-conversion transfers are handled
    /// separately and are not implemented yet.
    /// </summary>
    private void RunDma()
    {
        if ((_dmaControl & 0x80) == 0)
        {
            return;
        }

        DmaCount++;
        if ((_dmaControl & 0x10) != 0)
        {
            RunCharacterConversionDma();
            return;
        }

        var length = _dmaTerminalCounter == 0 ? 0x10000 : _dmaTerminalCounter;
        var destinationIsBackupRam = (_dmaControl & 0x04) != 0;
        for (var index = 0u; index < length; index++)
        {
            var value = ReadDmaSource(_dmaSource + index);
            if (destinationIsBackupRam)
            {
                _backupRam[(_dmaDestination + index) % BackupRamSize] = value;
            }
            else
            {
                _internalRam[(_dmaDestination + index) % InternalRamSize] = value;
            }
        }

        // Terminating a transfer raises the DMA interrupt for whichever
        // processor asked for it.
        _sa1InterruptFlags |= 0x20;
        _snesInterruptFlags |= 0x20;
    }

    /// <summary>
    /// Character-conversion DMA. Games store sprite and tile graphics as
    /// linear bitmaps because that is cheap to generate, but the PPU wants
    /// planar 8x8 tiles. This converts one to the other on the way through:
    /// for each 8x8 tile, each row's pixels are split into bitplane pairs.
    /// $2231 selects the colour depth (0 = 8bpp, 1 = 4bpp, 2 = 2bpp) and the
    /// source bitmap width.
    /// </summary>
    private void RunCharacterConversionDma()
    {
        var colourMode = _characterConversionControl & 0x03;
        var bitsPerPixel = colourMode switch
        {
            0 => 8,
            1 => 4,
            _ => 2
        };

        // Bits 2-4 give the bitmap width as a power of two, in 8-pixel tiles.
        var tilesPerRow = 1 << ((_characterConversionControl >> 2) & 0x07);
        var bytesPerTile = bitsPerPixel * 8;
        var length = _dmaTerminalCounter == 0 ? 0x10000 : _dmaTerminalCounter;
        var tileCount = Math.Max(1, length / bytesPerTile);
        Span<byte> pixels = stackalloc byte[8];

        for (var tile = 0; tile < tileCount; tile++)
        {
            var tileX = tile % tilesPerRow;
            var tileY = tile / tilesPerRow;

            for (var row = 0; row < 8; row++)
            {
                // Gather the eight source pixels for this tile row out of the
                // linear bitmap.
                for (var column = 0; column < 8; column++)
                {
                    var pixelX = (tileX * 8) + column;
                    var pixelY = (tileY * 8) + row;
                    var bitIndex = ((pixelY * tilesPerRow * 8) + pixelX) * bitsPerPixel;
                    pixels[column] = ReadBitmapPixel(bitIndex, bitsPerPixel);
                }

                // Emit them as bitplane pairs: planes 0/1 interleaved per row,
                // then planes 2/3 sixteen bytes later, and so on.
                for (var plane = 0; plane < bitsPerPixel; plane += 2)
                {
                    byte low = 0;
                    byte high = 0;
                    for (var column = 0; column < 8; column++)
                    {
                        var pixel = pixels[column];
                        low |= (byte)(((pixel >> plane) & 1) << (7 - column));
                        high |= (byte)(((pixel >> (plane + 1)) & 1) << (7 - column));
                    }

                    var planeOffset = (tile * bytesPerTile) + (plane * 8) + (row * 2);
                    WriteConvertedByte(planeOffset, low);
                    WriteConvertedByte(planeOffset + 1, high);
                }
            }
        }

        _sa1InterruptFlags |= 0x20;
        _snesInterruptFlags |= 0x20;
    }

    private byte ReadBitmapPixel(int bitIndex, int bitsPerPixel)
    {
        var value = 0;
        for (var bit = 0; bit < bitsPerPixel; bit++)
        {
            var absolute = bitIndex + bit;
            var source = ReadDmaSource((uint)(_dmaSource + (absolute >> 3)));
            value = (value << 1) | ((source >> (7 - (absolute & 7))) & 1);
        }

        return (byte)value;
    }

    private void WriteConvertedByte(int offset, byte value)
    {
        if ((_dmaControl & 0x04) != 0)
        {
            _backupRam[(_dmaDestination + (uint)offset) % BackupRamSize] = value;
            return;
        }

        _internalRam[(_dmaDestination + (uint)offset) % InternalRamSize] = value;
    }

    private byte ReadDmaSource(uint address) => (_dmaControl & 0x03) switch
    {
        0 => ReadRom((byte)(address >> 16), (ushort)address),
        1 => _backupRam[address % BackupRamSize],
        _ => _internalRam[address % InternalRamSize]
    };

    // --- S-CPU facing register file ($2200-$23FF) -------------------------

    /// <summary>
    /// $2200-$23FF is one shared register file. Hardware nominally assigns each
    /// register to one processor, but both sides address the whole file, and
    /// dropping a write because it arrived from the "wrong" side deadlocks the
    /// pair: Super Mario RPG programs the SA-1 timer from SA-1 code, and losing
    /// that write leaves the coprocessor idling on an interrupt that can never
    /// arrive while the S-CPU spins waiting for it.
    /// </summary>
    /// <remarks>
    /// The two switches cover disjoint address ranges, so dispatching to both
    /// is equivalent to one combined switch.
    /// </remarks>
    public void WriteRegister(ushort address, byte value)
    {
        WriteSnesRegister(address, value);
        WriteSa1Register(address, value);
    }

    private void WriteSnesRegister(ushort address, byte value)
    {
        switch (address)
        {
            case 0x2200:
                var wasStopped = (_sa1Control & 0x20) != 0;
                _sa1Control = value;
                if (wasStopped && (value & 0x20) == 0)
                {
                    ResetSa1();
                }

                // CCNT is "IRrNmmmm": bit 7 raises an IRQ on the SA-1 and bit 4
                // an NMI, both on behalf of the S-CPU.
                if ((value & 0x80) != 0)
                {
                    _sa1InterruptFlags |= 0x80;
                }

                if ((value & 0x10) != 0)
                {
                    _sa1InterruptFlags |= 0x10;
                }

                break;
            case 0x2201:
                _snesInterruptEnable = value;
                break;
            case 0x2202:
                // Writing a set bit acknowledges that interrupt source.
                _snesInterruptFlags &= (byte)~value;
                break;
            case 0x2203:
                _sa1ResetVector = (ushort)((_sa1ResetVector & 0xFF00) | value);
                break;
            case 0x2204:
                _sa1ResetVector = (ushort)((_sa1ResetVector & 0x00FF) | (value << 8));
                break;
            case 0x2205:
                _sa1NmiVector = (ushort)((_sa1NmiVector & 0xFF00) | value);
                break;
            case 0x2206:
                _sa1NmiVector = (ushort)((_sa1NmiVector & 0x00FF) | (value << 8));
                break;
            case 0x2207:
                _sa1IrqVector = (ushort)((_sa1IrqVector & 0xFF00) | value);
                break;
            case 0x2208:
                _sa1IrqVector = (ushort)((_sa1IrqVector & 0x00FF) | (value << 8));
                break;
            case >= 0x2220 and <= 0x2223:
                var slot = address - 0x2220;
                _superMmcBanks[slot] = (byte)(value & 0x07);
                _superMmcRemap[slot] = (value & 0x80) != 0;
                break;
            case 0x2224:
                _snesBackupRamBlock = (byte)(value & 0x1F);
                break;
            case 0x2210:
                _timerControl = value;
                break;
            case 0x2211:
                _timerHorizontalCounter = 0;
                _timerVerticalCounter = 0;
                _linearTimerCounter = 0;
                break;
            case 0x2212:
                _timerHorizontalCompare = (ushort)((_timerHorizontalCompare & 0x0100) | value);
                break;
            case 0x2213:
                _timerHorizontalCompare = (ushort)((_timerHorizontalCompare & 0x00FF) | ((value & 1) << 8));
                break;
            case 0x2214:
                _timerVerticalCompare = (ushort)((_timerVerticalCompare & 0x0100) | value);
                break;
            case 0x2215:
                _timerVerticalCompare = (ushort)((_timerVerticalCompare & 0x00FF) | ((value & 1) << 8));
                break;
        }
    }

    public byte ReadRegister(ushort address) => address switch
    {
        // $2300 SFR: interrupt status the S-CPU polls.
        0x2300 => (byte)(_snesInterruptFlags | (_snesControl & 0x0F)),
        0x2301 => (byte)(_sa1InterruptFlags | (_sa1Control & 0x0F)),
        0x2302 => (byte)_timerHorizontalCounter,
        0x2303 => (byte)(_timerHorizontalCounter >> 8),
        0x2304 => (byte)_timerVerticalCounter,
        0x2305 => (byte)(_timerVerticalCounter >> 8),
        0x2306 => (byte)_arithmeticResult,
        0x2307 => (byte)(_arithmeticResult >> 8),
        0x2308 => (byte)(_arithmeticResult >> 16),
        0x2309 => (byte)(_arithmeticResult >> 24),
        0x230A => (byte)(_arithmeticResult >> 32),
        // $230B is the version code; real SA-1 boards report 0.
        0x230B => 0x00,
        0x230C => (byte)ReadVariableLengthBits(),
        0x230D => (byte)(ReadVariableLengthBits() >> 8),
        _ => 0x00
    };

    /// <summary>
    /// The 8 KiB backup-RAM window the S-CPU sees at $6000-$7FFF, and the
    /// linear view at banks $40-$4F.
    /// </summary>
    public byte ReadBackupRam(uint offset) => _backupRam[offset % BackupRamSize];

    public void WriteBackupRam(uint offset, byte value) =>
        _backupRam[offset % BackupRamSize] = value;

    public byte ReadSnesBackupRamWindow(ushort offset) =>
        _backupRam[((_snesBackupRamBlock * 0x2000) + (offset & 0x1FFF)) % BackupRamSize];

    public void WriteSnesBackupRamWindow(ushort offset, byte value) =>
        _backupRam[((_snesBackupRamBlock * 0x2000) + (offset & 0x1FFF)) % BackupRamSize] = value;

    /// <summary>
    /// The S-CPU's ROM reads also pass through the super MMC, so both
    /// processors see the same four 1 MiB windows.
    /// </summary>
    public byte ReadCartridgeRom(byte bank, ushort offset) => ReadRom(bank, offset);

    /// <summary>
    /// $2209 SCNT bit 6 redirects the S-CPU's IRQ vector to $220E/$220F and
    /// bit 4 its NMI vector to $220C/$220D, letting SA-1 titles hand their
    /// console-side interrupts to a different handler than the ROM declares.
    /// </summary>
    public bool TryReadSnesVector(ushort offset, out byte value)
    {
        var vector = offset switch
        {
            0xFFEA or 0xFFEB or 0xFFFA or 0xFFFB when (_snesControl & 0x10) != 0 => _snesNmiVector,
            0xFFEE or 0xFFEF or 0xFFFE or 0xFFFF when (_snesControl & 0x40) != 0 => _snesIrqVector,
            _ => (ushort?)null
        };

        if (vector is null)
        {
            value = 0;
            return false;
        }

        value = (offset & 1) == 0 ? (byte)vector : (byte)(vector >> 8);
        return true;
    }

    public byte ReadInternalRam(ushort offset) => _internalRam[offset % InternalRamSize];

    public void WriteInternalRam(ushort offset, byte value) =>
        _internalRam[offset % InternalRamSize] = value;

    // --- SA-1 facing bus --------------------------------------------------

    public byte CpuRead(uint address)
    {
        address &= 0xFFFFFF;
        var bank = (byte)(address >> 16);
        var offset = (ushort)address;

        ChargeContention(bank, offset);

        // The SA-1 does not use the cartridge's own interrupt vectors. The
        // S-CPU supplies them through $2203-$2208 so it controls where the
        // coprocessor starts and which handlers it runs; the vector fetch is
        // redirected here rather than reaching ROM.
        if (bank == 0x00 && offset >= 0xFFE0 && TryReadVector(offset, out var vectorByte))
        {
            return vectorByte;
        }

        if (bank is >= 0x40 and <= 0x4F)
        {
            return _backupRam[(((bank - 0x40) << 16) | offset) % BackupRamSize];
        }

        if (IsSystemBank(bank))
        {
            // The SA-1 has no access to console WRAM, so its internal RAM is
            // mirrored into the low 2 KiB where the 65C816 keeps its direct
            // page and stack. Without this the coprocessor's stack writes go
            // nowhere and every subroutine return lands on garbage.
            if (offset <= 0x07FF)
            {
                return _internalRam[offset % InternalRamSize];
            }

            if (offset is >= 0x3000 and <= 0x37FF)
            {
                return _internalRam[(offset - 0x3000) % InternalRamSize];
            }

            if (offset is >= 0x2200 and <= 0x23FF)
            {
                return ReadRegister(offset);
            }

            if (offset is >= 0x6000 and <= 0x7FFF)
            {
                return _backupRam[
                    ((_sa1BackupRamBlock * 0x2000) + (offset & 0x1FFF)) % BackupRamSize];
            }
        }

        return ReadRom(bank, offset);
    }

    public void CpuWrite(uint address, byte value)
    {
        address &= 0xFFFFFF;
        var bank = (byte)(address >> 16);
        var offset = (ushort)address;

        ChargeContention(bank, offset);

        if (bank is >= 0x40 and <= 0x4F)
        {
            _backupRam[(((bank - 0x40) << 16) | offset) % BackupRamSize] = value;
            return;
        }

        if (!IsSystemBank(bank))
        {
            return;
        }

        if (offset <= 0x07FF)
        {
            _internalRam[offset % InternalRamSize] = value;
            return;
        }

        if (offset is >= 0x3000 and <= 0x37FF)
        {
            _internalRam[(offset - 0x3000) % InternalRamSize] = value;
            return;
        }

        if (offset is >= 0x6000 and <= 0x7FFF)
        {
            _backupRam[((_sa1BackupRamBlock * 0x2000) + (offset & 0x1FFF)) % BackupRamSize] = value;
            return;
        }

        if (offset is >= 0x2200 and <= 0x23FF)
        {
            WriteRegister(offset, value);
        }
    }

    /// <summary>
    /// Charges the SA-1 for touching memory the S-CPU also drives. ROM and
    /// backup RAM are genuinely shared, so the arbiter can stall the SA-1
    /// there; internal RAM is dual-ported and only conflicts when the S-CPU is
    /// actively using it. Cycle counts are the documented worst case rather
    /// than a bus-accurate model.
    /// </summary>
    private void ChargeContention(byte bank, ushort offset)
    {
        if (bank is >= 0x40 and <= 0x4F)
        {
            _contentionCycles += SharedRamWaitCycles;
            return;
        }

        if (IsSystemBank(bank) && offset is >= 0x6000 and <= 0x7FFF)
        {
            _contentionCycles += SharedRamWaitCycles;
            return;
        }

        // Cartridge ROM: the S-CPU wins arbitration, so the SA-1 waits.
        if (bank >= 0xC0 || (IsSystemBank(bank) && offset >= 0x8000))
        {
            _contentionCycles += SharedRomWaitCycles;
        }
    }

    private const int SharedRomWaitCycles = 1;
    private const int SharedRamWaitCycles = 2;

    // The SA-1 core is clocked by Clock(), so it does not participate in the
    // S-CPU's access-speed accounting.
    public void BeginCpuInstructionTiming()
    {
    }

    public CpuInstructionTiming EndCpuInstructionTiming(int minimumCpuCycles) =>
        new(Math.Max(1, minimumCpuCycles), Math.Max(1, minimumCpuCycles));

    private void WriteSa1Register(ushort address, byte value)
    {
        switch (address)
        {
            case 0x2209:
                _snesControl = value;
                if ((value & 0x80) != 0)
                {
                    _snesInterruptFlags |= 0x80;
                }

                break;
            case 0x220A:
                _sa1InterruptEnable = value;
                break;
            case 0x220B:
                _sa1InterruptFlags &= (byte)~value;
                break;
            case 0x220C:
                _snesNmiVector = (ushort)((_snesNmiVector & 0xFF00) | value);
                break;
            case 0x220D:
                _snesNmiVector = (ushort)((_snesNmiVector & 0x00FF) | (value << 8));
                break;
            case 0x220E:
                _snesIrqVector = (ushort)((_snesIrqVector & 0xFF00) | value);
                break;
            case 0x220F:
                _snesIrqVector = (ushort)((_snesIrqVector & 0x00FF) | (value << 8));
                break;
            case 0x2225:
                _sa1BackupRamBlock = (byte)(value & 0x1F);
                _sa1BackupRamBitmapMode = (value & 0x80) != 0;
                break;
            case 0x2250:
                _arithmeticControl = value;
                if ((value & 0x02) != 0)
                {
                    _arithmeticResult = 0;
                }

                break;
            case 0x2251:
                _arithmeticOperandA = (short)((_arithmeticOperandA & 0xFF00) | value);
                break;
            case 0x2252:
                _arithmeticOperandA = (short)((_arithmeticOperandA & 0x00FF) | (value << 8));
                break;
            case 0x2253:
                _arithmeticOperandB = (short)((_arithmeticOperandB & 0xFF00) | value);
                break;
            case 0x2254:
                _arithmeticOperandB = (short)((_arithmeticOperandB & 0x00FF) | (value << 8));
                RunArithmetic();
                break;
            case 0x2230:
                _dmaControl = value;
                break;
            case 0x2231:
                _characterConversionControl = value;
                break;
            case 0x2232:
                _dmaSource = (_dmaSource & 0xFFFF00) | value;
                break;
            case 0x2233:
                _dmaSource = (_dmaSource & 0xFF00FF) | ((uint)value << 8);
                break;
            case 0x2234:
                _dmaSource = (_dmaSource & 0x00FFFF) | ((uint)value << 16);
                break;
            case 0x2235:
                _dmaDestination = (_dmaDestination & 0xFFFF00) | value;
                break;
            case 0x2236:
                _dmaDestination = (_dmaDestination & 0xFF00FF) | ((uint)value << 8);
                // Writing the middle byte starts an internal-RAM transfer.
                if ((_dmaControl & 0x04) == 0)
                {
                    RunDma();
                }

                break;
            case 0x2237:
                _dmaDestination = (_dmaDestination & 0x00FFFF) | ((uint)value << 16);
                // Writing the high byte starts a backup-RAM transfer.
                if ((_dmaControl & 0x04) != 0)
                {
                    RunDma();
                }

                break;
            case 0x2238:
                _dmaTerminalCounter = (ushort)((_dmaTerminalCounter & 0xFF00) | value);
                break;
            case 0x2239:
                _dmaTerminalCounter = (ushort)((_dmaTerminalCounter & 0x00FF) | (value << 8));
                break;
            case 0x2258:
                _bitLength = (byte)(value & 0x0F);
                _bitAutoIncrement = (value & 0x80) != 0;
                break;
            case 0x2259:
                _bitAddress = (_bitAddress & 0xFFFF00) | value;
                break;
            case 0x225A:
                _bitAddress = (_bitAddress & 0xFF00FF) | ((uint)value << 8);
                break;
            case 0x225B:
                _bitAddress = (_bitAddress & 0x00FFFF) | ((uint)value << 16);
                _bitOffset = 0;
                break;
        }
    }

    /// <summary>
    /// $2250 selects multiply, divide, or multiply-accumulate. The operation
    /// runs when the high byte of operand B is written.
    /// </summary>
    private void RunArithmetic()
    {
        switch (_arithmeticControl & 0x01)
        {
            case 0 when (_arithmeticControl & 0x02) == 0:
                _arithmeticResult = _arithmeticOperandA * _arithmeticOperandB;
                break;
            case 0:
                _arithmeticResult += _arithmeticOperandA * _arithmeticOperandB;
                break;
            default:
                if (_arithmeticOperandB == 0)
                {
                    _arithmeticResult = 0;
                    break;
                }

                var quotient = _arithmeticOperandA / (ushort)_arithmeticOperandB;
                var remainder = _arithmeticOperandA % (ushort)_arithmeticOperandB;
                _arithmeticResult = (uint)((remainder << 16) | (quotient & 0xFFFF));
                break;
        }
    }

    private void ResetSa1()
    {
        _stopped = false;
        _cpu.Reset();
    }

    private byte ReadRom(byte bank, ushort offset)
    {
        var rom = _cartridge.RomSpan;
        if (rom.Length == 0)
        {
            return 0;
        }

        // Banks $C0-$FF expose four 1 MiB windows selected by the super MMC,
        // one whole 64 KiB bank at a time.
        if (bank >= 0xC0)
        {
            var linear = (ResolveMmcBlock((bank - 0xC0) >> 4) << 20) |
                         ((bank & 0x0F) << 16) | offset;
            return rom[linear % rom.Length];
        }

        // $00-$3F / $80-$BF map the upper half of each bank, LoROM style, and
        // are windowed by the same four MMC slots: $00-$1F, $20-$3F, $80-$9F
        // and $A0-$BF in that order.
        if (offset < 0x8000)
        {
            return 0;
        }

        var slot = ((bank & 0x80) >> 6) | ((bank & 0x20) >> 5);
        var address = (ResolveMmcBlock(slot) << 20) |
                      ((bank & 0x1F) << 15) | (offset & 0x7FFF);
        return rom[address % rom.Length];
    }

    /// <summary>
    /// Resolves which 1 MiB ROM block an MMC slot exposes. $2220-$2223 bit 7
    /// enables the remap; without it the slot keeps its identity block.
    /// </summary>
    private int ResolveMmcBlock(int slot)
    {
        slot &= 3;
        return _superMmcRemap[slot] ? _superMmcBanks[slot] : slot;
    }

    /// <summary>
    /// Maps the 65C816 vector addresses onto the SA-1's own vector registers.
    /// Both the native and emulation-mode vectors are covered because the core
    /// resets into emulation mode.
    /// </summary>
    private bool TryReadVector(ushort offset, out byte value)
    {
        var vector = offset switch
        {
            // Native mode: NMI $FFEA, reset $FFEC, IRQ $FFEE.
            0xFFEA or 0xFFEB => _sa1NmiVector,
            0xFFEE or 0xFFEF => _sa1IrqVector,
            // Emulation mode: NMI $FFFA, reset $FFFC, IRQ/BRK $FFFE.
            0xFFFA or 0xFFFB => _sa1NmiVector,
            0xFFFC or 0xFFFD => _sa1ResetVector,
            0xFFFE or 0xFFFF => _sa1IrqVector,
            _ => (ushort?)null
        };

        if (vector is null)
        {
            value = 0;
            return false;
        }

        value = (offset & 1) == 0 ? (byte)vector : (byte)(vector >> 8);
        return true;
    }

    private static bool IsSystemBank(byte bank) =>
        bank <= 0x3F || bank is >= 0x80 and <= 0xBF;
}

internal readonly record struct Sa1Snapshot(
    byte Sa1Control,
    byte SnesControl,
    byte Sa1InterruptEnable,
    byte Sa1InterruptFlags,
    byte SnesInterruptEnable,
    byte SnesInterruptFlags,
    byte TimerControl,
    ushort ResetVector,
    ushort NmiVector,
    ushort IrqVector,
    ushort TimerHorizontalCompare,
    ushort TimerVerticalCompare,
    byte MmcBank0,
    byte InternalRam0,
    byte InternalRam1,
    byte InternalRam2);
