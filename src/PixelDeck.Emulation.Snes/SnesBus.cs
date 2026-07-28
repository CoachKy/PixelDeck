namespace PixelDeck.Emulation.Snes;

[Flags]
public enum SnesButton : ushort
{
    None = 0,
    B = 1 << 0,
    Y = 1 << 1,
    Select = 1 << 2,
    Start = 1 << 3,
    Up = 1 << 4,
    Down = 1 << 5,
    Left = 1 << 6,
    Right = 1 << 7,
    A = 1 << 8,
    X = 1 << 9,
    L = 1 << 10,
    R = 1 << 11
}

internal sealed class SnesBus : I65816Bus
{
    private const int MasterClocksPerScanline = 1_364;
    private const int MasterClocksPerCpuCycle = 6;
    private const int AutomaticControllerReadMasterClocks = 4_224;

    private static readonly byte[][] DmaPatterns =
    [
        [0],
        [0, 1],
        [0, 0],
        [0, 0, 1, 1],
        [0, 1, 2, 3],
        [0, 1, 0, 1],
        [0, 0],
        [0, 0, 1, 1]
    ];

    private readonly SnesCartridge _cartridge;
    private readonly byte[] _wram = new byte[128 * 1024];
    private readonly byte[] _dmaRegisters = new byte[8 * 16];
    private readonly bool[] _hdmaTerminated = new bool[8];
    private readonly bool[] _hdmaDoTransfer = new bool[8];
    private readonly SnesApu _apu = new();
    private readonly SnesDsp1? _dsp1;
    private readonly SnesCx4? _cx4;
    private readonly Sa1? _sa1;
    private readonly Sdd1? _sdd1;
    private readonly SuperFx? _superFx;
    private uint _wramAddress;
    private byte _nmitimen;
    private byte _wrmpya;
    private ushort _wrdiv;
    private ushort _divisionQuotient;
    private ushort _multiplyOrRemainder;
    private ushort _horizontalTimer = 0x01FF;
    private ushort _verticalTimer = 0x01FF;
    private byte _hdmaEnable;
    private int _masterClockRemainder;
    private int _scanline;
    private bool _vblank;
    private bool _nmiFlag;
    private bool _nmiPending;
    private bool _irqFlag;
    private ushort _controllerOne;
    private ushort _controllerTwo;
    private ushort _controllerShiftOne;
    private ushort _controllerShiftTwo;
    private ushort _automaticControllerOne;
    private ushort _automaticControllerTwo;
    private ushort _pendingAutomaticControllerOne;
    private ushort _pendingAutomaticControllerTwo;
    private int _automaticControllerReadRemainingMasterClocks;
    private bool _controllerStrobe;
    private bool _hdmaInitialized;
    private byte _pio = 0xFF;
    private int _mathClockRemainder;
    private byte _multiplyCyclesRemaining;
    private byte _divisionCyclesRemaining;
    private uint _mathShift;
    private byte _openBus;
    private byte _memorySpeedControl;
    private bool _cpuInstructionTimingActive;
    private int _cpuInstructionAccessCount;
    private int _cpuInstructionAccessMasterClocks;
    private int _cpuInstructionDmaMasterClocks;
    private long _totalMasterClocks;

    public SnesBus(SnesCartridge cartridge)
    {
        _cartridge = cartridge;
        _dsp1 = cartridge.HasDsp1 ? new SnesDsp1() : null;
        _cx4 = cartridge.HasCx4 ? new SnesCx4(cartridge) : null;
        _sa1 = cartridge.HasSa1 ? new Sa1(cartridge) : null;
        _sdd1 = cartridge.HasSdd1 ? new Sdd1(cartridge) : null;
        _superFx = cartridge.HasSuperFx ? new SuperFx(cartridge) : null;
        Ppu = new SnesPpu();
        Ppu.IsPal = cartridge.Info.IsPal;
        Ppu.CounterLatchEnabled = (_pio & 0x80) != 0;
    }

    public SnesPpu Ppu { get; }

    public bool FrameReady { get; private set; }

    public bool IrqPending => _irqFlag;

    public ushort ApuOutputWord => _apu.OutputWord;

    public long ApuExecutedInstructions => _apu.ExecutedInstructions;

    public byte ApuFirstUnsupportedOpcode => _apu.FirstUnsupportedOpcode;

    public ushort ApuFirstUnsupportedAddress => _apu.FirstUnsupportedAddress;

    public int BufferedAudioSampleCount => _apu.BufferedSampleCount;

    public int ActiveAudioVoiceCount => _apu.ActiveVoiceCount;

    public long DroppedAudioSampleCount => _apu.DroppedSampleCount;

    public long ReadDsp1CommandCount(byte command) => _dsp1?.GetCommandExecutionCount(command) ?? 0;

    public long ReadCx4CommandCount(byte command) => _cx4?.GetCommandExecutionCount(command) ?? 0;

    public byte ReadDspRegister(byte address) => _apu.ReadDspRegister(address);

    public int ReadAudioSamples(Span<float> destination) => _apu.ReadSamples(destination);

    public void ClearAudioSamples() => _apu.ClearSamples();

    internal ushort AutomaticControllerOne => _automaticControllerOne;

    internal byte NmiTimerControl => _nmitimen;

    internal ushort VerticalIrqTarget => _verticalTimer;

    internal ushort HorizontalIrqTarget => _horizontalTimer;

    internal int CurrentScanline => _scanline;

    // --- Transition diagnostics -------------------------------------------
    // A game that blanks the screen and then waits is polling something. These
    // count the three things FF3 could be waiting on so a stalled transition
    // can be attributed without guessing.
    internal long HvbJoyReads { get; private set; }

    internal long HvbJoyAutoReadBusyReads { get; private set; }

    internal long CounterLatchCount { get; private set; }

    internal long DmaTransferCount { get; private set; }

    internal long HdmaEnableWrites { get; private set; }

    internal byte LastDmaChannelMask { get; private set; }

    internal bool HasSa1 => _sa1 is not null;

    internal long Sa1ExecutedInstructions => _sa1?.ExecutedInstructions ?? 0;

    internal uint Sa1ProgramAddress => _sa1?.ProgramAddress ?? 0;

    internal byte Sa1ControlRegister => _sa1?.ControlRegister ?? 0;

    internal long Sa1DmaCount => _sa1?.DmaCount ?? 0;

    internal Sa1Snapshot Sa1State => _sa1?.Snapshot ?? default;

    internal bool HasSdd1 => _sdd1 is not null;

    internal long Sdd1DecompressionCount => _sdd1?.DecompressionCount ?? 0;

    internal long Sdd1CandidateTransfers => _sdd1?.CandidateTransfers ?? 0;

    internal IReadOnlyList<(uint Source, byte Header)> Sdd1Runs =>
        _sdd1?.Runs ?? [];

    internal string Sdd1HeaderSummary => _sdd1 is null
        ? "none"
        : $"modes[2bpp,4bpp,8bpp,mode7]={string.Join(",", _sdd1.HeaderModeCounts)} " +
          $"contexts={string.Join(",", _sdd1.HeaderContextCounts)}";

    internal bool HasSuperFx => _superFx is not null;

    internal long SuperFxExecutedInstructions => _superFx?.ExecutedInstructions ?? 0;

    internal bool SuperFxRunning => _superFx?.IsRunning ?? false;

    internal ushort SuperFxProgramCounter => _superFx?.ProgramCounter ?? 0;

    private byte ReadHvbJoy()
    {
        var autoReadBusy = _automaticControllerReadRemainingMasterClocks > 0;
        HvbJoyReads++;
        if (autoReadBusy)
        {
            HvbJoyAutoReadBusyReads++;
        }

        return (byte)(
            (_vblank ? 0x80 : 0) |
            (_masterClockRemainder is <= 2 or >= 1_096 ? 0x40 : 0) |
            (autoReadBusy ? 0x01 : 0) |
            (_openBus & 0x3E));
    }

    internal byte MemorySpeedControl => _memorySpeedControl;

    internal long TotalMasterClocks => _totalMasterClocks;

    public void BeginCpuInstructionTiming()
    {
        _cpuInstructionTimingActive = true;
        _cpuInstructionAccessCount = 0;
        _cpuInstructionAccessMasterClocks = 0;
        _cpuInstructionDmaMasterClocks = 0;
    }

    public CpuInstructionTiming EndCpuInstructionTiming(int minimumCpuCycles)
    {
        var cpuCycles = Math.Max(Math.Max(1, minimumCpuCycles), _cpuInstructionAccessCount);
        var internalCycles = cpuCycles - _cpuInstructionAccessCount;
        var masterClocks =
            _cpuInstructionAccessMasterClocks +
            (internalCycles * MasterClocksPerCpuCycle) +
            _cpuInstructionDmaMasterClocks;

        _cpuInstructionTimingActive = false;
        return new CpuInstructionTiming(cpuCycles, Math.Max(MasterClocksPerCpuCycle, masterClocks));
    }

    public byte CpuRead(uint address)
    {
        RecordCpuAccess(address);
        return Read(address);
    }

    public void CpuWrite(uint address, byte value)
    {
        RecordCpuAccess(address);
        Write(address, value);
    }

    internal int GetCpuAccessMasterClocks(uint address)
    {
        address &= 0xFFFFFF;
        var bank = (byte)(address >> 16);
        var offset = (ushort)address;

        if (bank is >= 0x40 and <= 0x7F)
        {
            return 8;
        }

        if (bank is >= 0xC0)
        {
            return (_memorySpeedControl & 0x01) != 0 ? 6 : 8;
        }

        if (offset < 0x2000)
        {
            return 8;
        }

        if (offset < 0x4000)
        {
            return 6;
        }

        if (offset < 0x4200)
        {
            return 12;
        }

        if (offset < 0x6000)
        {
            return 6;
        }

        if (offset < 0x8000)
        {
            return 8;
        }

        return bank >= 0x80 && (_memorySpeedControl & 0x01) != 0 ? 6 : 8;
    }

    public byte Read(uint address)
    {
        address &= 0xFFFFFF;
        var bank = (byte)(address >> 16);
        var offset = (ushort)address;

        if (TryReadDsp1(bank, offset, out var dsp1Value))
        {
            return LatchOpenBus(dsp1Value);
        }

        if (TryReadCx4(bank, offset, out var cx4Value))
        {
            return LatchOpenBus(cx4Value);
        }

        if (TryReadSa1(bank, offset, out var sa1Value))
        {
            return LatchOpenBus(sa1Value);
        }

        if (TryReadSdd1(bank, offset, out var sdd1Value))
        {
            return LatchOpenBus(sdd1Value);
        }

        if (TryReadSuperFx(bank, offset, out var superFxValue))
        {
            return LatchOpenBus(superFxValue);
        }

        if (bank is 0x7E or 0x7F)
        {
            return LatchOpenBus(_wram[((bank - 0x7E) << 16) | offset]);
        }

        if (IsSystemBank(bank))
        {
            if (offset < 0x2000)
            {
                return LatchOpenBus(_wram[offset]);
            }

            if (offset is >= 0x2100 and <= 0x21FF)
            {
                return LatchOpenBus(ReadPpuOrApu(offset));
            }

            if (offset is 0x4016 or 0x4017)
            {
                return LatchOpenBus(ReadController(offset == 0x4016 ? 1 : 2));
            }

            if (offset is >= 0x4210 and <= 0x421F)
            {
                return LatchOpenBus(ReadCpuRegister(offset));
            }

            if (offset is >= 0x4300 and <= 0x437F)
            {
                return LatchOpenBus(_dmaRegisters[offset - 0x4300]);
            }
        }

        return LatchOpenBus(_cartridge.Read(address));
    }

    public byte Peek(uint address)
    {
        address &= 0xFFFFFF;
        var bank = (byte)(address >> 16);
        var offset = (ushort)address;

        if (TryPeekDsp1(bank, offset, out var dsp1Value))
        {
            return dsp1Value;
        }

        if (TryPeekCx4(bank, offset, out var cx4Value))
        {
            return cx4Value;
        }

        if (bank is 0x7E or 0x7F)
        {
            return _wram[((bank - 0x7E) << 16) | offset];
        }

        if (IsSystemBank(bank) && offset < 0x2000)
        {
            return _wram[offset];
        }

        return _cartridge.Read(address);
    }

    public void Write(uint address, byte value)
    {
        _openBus = value;
        address &= 0xFFFFFF;
        var bank = (byte)(address >> 16);
        var offset = (ushort)address;

        if (TryWriteDsp1(bank, offset, value))
        {
            return;
        }

        if (TryWriteSa1(bank, offset, value))
        {
            return;
        }

        if (TryWriteSdd1(bank, offset, value))
        {
            return;
        }

        if (TryWriteSuperFx(bank, offset, value))
        {
            return;
        }

        if (TryWriteCx4(bank, offset, value))
        {
            return;
        }

        if (bank is 0x7E or 0x7F)
        {
            _wram[((bank - 0x7E) << 16) | offset] = value;
            return;
        }

        if (IsSystemBank(bank))
        {
            if (offset < 0x2000)
            {
                _wram[offset] = value;
                return;
            }

            if (offset is >= 0x2100 and <= 0x21FF)
            {
                WritePpuOrApu(offset, value);
                return;
            }

            if (offset == 0x4016)
            {
                WriteControllerStrobe(value);
                return;
            }

            if (offset is >= 0x4200 and <= 0x420D)
            {
                WriteCpuRegister(offset, value);
                return;
            }

            if (offset is >= 0x4300 and <= 0x437F)
            {
                _dmaRegisters[offset - 0x4300] = value;
                return;
            }
        }

        _cartridge.Write(address, value);
    }

    public void BeginFrame()
    {
        FrameReady = false;
        if (_scanline == 0)
        {
            Ppu.BeginFrame();
            InitializeHdma();
            PerformHdmaScanline();
        }
    }

    public void Clock(int cpuCycles)
    {
        AdvanceMasterClocks(Math.Max(1, cpuCycles) * MasterClocksPerCpuCycle);
    }

    internal void AdvanceMasterClocks(int masterClocks)
    {
        masterClocks = Math.Max(MasterClocksPerCpuCycle, masterClocks);
        _totalMasterClocks += masterClocks;
        _apu.ClockMasterClocks(masterClocks);
        _sa1?.Clock(masterClocks / MasterClocksPerCpuCycle);
        _superFx?.Clock(masterClocks / MasterClocksPerCpuCycle);
        while (masterClocks > 0)
        {
            var clocksToEndOfScanline = MasterClocksPerScanline - _masterClockRemainder;
            var elapsed = Math.Min(masterClocks, clocksToEndOfScanline);
            CheckHorizontalIrq(_masterClockRemainder, _masterClockRemainder + elapsed);
            AdvanceTimedCpuOperations(elapsed);
            _masterClockRemainder += elapsed;
            masterClocks -= elapsed;

            if (_masterClockRemainder < MasterClocksPerScanline)
            {
                continue;
            }

            _masterClockRemainder = 0;
            if (_scanline < SnesPpu.Height)
            {
                Ppu.RenderScanline(_scanline);
            }

            _scanline++;
            CheckVerticalIrqAtScanlineStart();

            if (_scanline < SnesPpu.Height)
            {
                PerformHdmaScanline();
            }

            if (_scanline == SnesPpu.Height + 1)
            {
                Ppu.BeginVBlank();
                _vblank = true;
                _nmiFlag = true;
                VblankCount++;
                if ((_nmitimen & 0x80) != 0)
                {
                    _nmiPending = true;
                    VblankNmiArmed++;
                }

                if ((_nmitimen & 0x01) != 0)
                {
                    LatchAutomaticControllers();
                }
            }

            var totalScanlines = _cartridge.Info.IsPal ? 312 : 262;
            if (_scanline >= totalScanlines)
            {
                _scanline = 0;
                _vblank = false;
                FrameReady = true;

                // Hardware clears RDNMI bit 7 at the end of VBlank as well as
                // on read, so the flag never survives into active display.
                _nmiFlag = false;

                // Scanline 0 is a legal V-count IRQ target, but the wrap
                // above bypasses the per-scanline check further up, so it
                // has to be evaluated again here. Final Fantasy III programs
                // $4209 to 0 and otherwise never receives its raster
                // interrupt, leaving its main loop polling RDNMI forever.
                CheckVerticalIrqAtScanlineStart();
            }
        }
    }

    private void RecordCpuAccess(uint address)
    {
        if (!_cpuInstructionTimingActive)
        {
            return;
        }

        _cpuInstructionAccessCount++;
        _cpuInstructionAccessMasterClocks += GetCpuAccessMasterClocks(address);
    }

    private void CheckHorizontalIrq(int previousMasterClock, int currentMasterClock)
    {
        var horizontalIrqEnabled = (_nmitimen & 0x10) != 0;
        var verticalIrqEnabled = (_nmitimen & 0x20) != 0;
        if (!horizontalIrqEnabled || (verticalIrqEnabled && _scanline != _verticalTimer))
        {
            return;
        }

        var triggerMasterClock = _horizontalTimer * 4;
        if (previousMasterClock <= triggerMasterClock && currentMasterClock > triggerMasterClock)
        {
            _irqFlag = true;
        }
    }

    private void CheckVerticalIrqAtScanlineStart()
    {
        var horizontalIrqEnabled = (_nmitimen & 0x10) != 0;
        var verticalIrqEnabled = (_nmitimen & 0x20) != 0;
        if (!verticalIrqEnabled)
        {
            return;
        }

        VerticalIrqScanlinesArmed++;
        if (_scanline == _verticalTimer &&
            (!horizontalIrqEnabled || _horizontalTimer == 0))
        {
            VerticalIrqMatches++;
            _irqFlag = true;
        }
    }

    public bool ConsumeNmi()
    {
        var pending = _nmiPending;
        _nmiPending = false;
        return pending;
    }

    public void SetControllerState(int player, SnesButton buttons)
    {
        if (player == 1)
        {
            Volatile.Write(ref _controllerOne, (ushort)buttons);
        }
        else if (player == 2)
        {
            Volatile.Write(ref _controllerTwo, (ushort)buttons);
        }
    }

    internal void SaveState(BinaryWriter writer) => SaveState(writer, stateVersion: 14);

    internal void SaveState(BinaryWriter writer, int stateVersion)
    {
        writer.Write(_wram);
        writer.Write(_dmaRegisters);
        writer.Write(_wramAddress);
        writer.Write(_nmitimen);
        writer.Write(_wrmpya);
        writer.Write(_wrdiv);
        writer.Write(_divisionQuotient);
        writer.Write(_multiplyOrRemainder);
        writer.Write(_horizontalTimer);
        writer.Write(_verticalTimer);
        writer.Write(_hdmaEnable);
        writer.Write(_masterClockRemainder);
        writer.Write(_scanline);
        writer.Write(_vblank);
        writer.Write(_nmiFlag);
        writer.Write(_nmiPending);
        writer.Write(_irqFlag);
        writer.Write(Volatile.Read(ref _controllerOne));
        writer.Write(Volatile.Read(ref _controllerTwo));
        writer.Write(_controllerShiftOne);
        writer.Write(_controllerShiftTwo);
        writer.Write(_automaticControllerOne);
        writer.Write(_automaticControllerTwo);
        if (stateVersion >= 11)
        {
            writer.Write(_pendingAutomaticControllerOne);
            writer.Write(_pendingAutomaticControllerTwo);
            writer.Write(_automaticControllerReadRemainingMasterClocks);
        }
        writer.Write(_controllerStrobe);
        writer.Write(_hdmaInitialized);
        if (stateVersion >= 11)
        {
            writer.Write(_pio);
            writer.Write(_mathClockRemainder);
            writer.Write(_multiplyCyclesRemaining);
            writer.Write(_divisionCyclesRemaining);
            writer.Write(_mathShift);
        }
        writer.Write(_openBus);
        if (stateVersion >= 13)
        {
            writer.Write(_memorySpeedControl);
            writer.Write(_totalMasterClocks);
        }
        foreach (var value in _hdmaTerminated) writer.Write(value);
        foreach (var value in _hdmaDoTransfer) writer.Write(value);
        _dsp1?.SaveState(writer);
        if (stateVersion >= 12)
        {
            _cx4?.SaveState(writer);
        }
        _apu.SaveState(writer);
        Ppu.SaveState(writer, stateVersion);
    }

    internal void LoadState(BinaryReader reader) => LoadState(reader, stateVersion: 14);

    internal void LoadState(BinaryReader reader, int stateVersion)
    {
        reader.ReadExactly(_wram);
        reader.ReadExactly(_dmaRegisters);
        _wramAddress = reader.ReadUInt32();
        _nmitimen = reader.ReadByte();
        _wrmpya = reader.ReadByte();
        _wrdiv = reader.ReadUInt16();
        _divisionQuotient = reader.ReadUInt16();
        _multiplyOrRemainder = reader.ReadUInt16();
        _horizontalTimer = reader.ReadUInt16();
        _verticalTimer = reader.ReadUInt16();
        _hdmaEnable = reader.ReadByte();
        _masterClockRemainder = reader.ReadInt32();
        _scanline = reader.ReadInt32();
        _vblank = reader.ReadBoolean();
        _nmiFlag = reader.ReadBoolean();
        _nmiPending = reader.ReadBoolean();
        _irqFlag = reader.ReadBoolean();
        Volatile.Write(ref _controllerOne, reader.ReadUInt16());
        Volatile.Write(ref _controllerTwo, reader.ReadUInt16());
        _controllerShiftOne = reader.ReadUInt16();
        _controllerShiftTwo = reader.ReadUInt16();
        _automaticControllerOne = reader.ReadUInt16();
        _automaticControllerTwo = reader.ReadUInt16();
        if (stateVersion >= 11)
        {
            _pendingAutomaticControllerOne = reader.ReadUInt16();
            _pendingAutomaticControllerTwo = reader.ReadUInt16();
            _automaticControllerReadRemainingMasterClocks = reader.ReadInt32();
        }
        else
        {
            _pendingAutomaticControllerOne = _automaticControllerOne;
            _pendingAutomaticControllerTwo = _automaticControllerTwo;
            _automaticControllerReadRemainingMasterClocks = 0;
        }
        _controllerStrobe = reader.ReadBoolean();
        _hdmaInitialized = reader.ReadBoolean();
        if (stateVersion >= 11)
        {
            _pio = reader.ReadByte();
            _mathClockRemainder = reader.ReadInt32();
            _multiplyCyclesRemaining = reader.ReadByte();
            _divisionCyclesRemaining = reader.ReadByte();
            _mathShift = reader.ReadUInt32();
        }
        else
        {
            _pio = 0xFF;
            _mathClockRemainder = 0;
            _multiplyCyclesRemaining = 0;
            _divisionCyclesRemaining = 0;
            _mathShift = 0;
        }
        _openBus = reader.ReadByte();
        if (stateVersion >= 13)
        {
            _memorySpeedControl = reader.ReadByte();
            _totalMasterClocks = reader.ReadInt64();
        }
        else
        {
            _memorySpeedControl = 0;
            _totalMasterClocks = 0;
        }
        for (var index = 0; index < 8; index++) _hdmaTerminated[index] = reader.ReadBoolean();
        for (var index = 0; index < 8; index++) _hdmaDoTransfer[index] = reader.ReadBoolean();
        _dsp1?.LoadState(reader);
        if (stateVersion >= 12)
        {
            _cx4?.LoadState(reader);
        }
        _apu.LoadState(reader);
        Ppu.LoadState(reader, stateVersion);
        Ppu.IsPal = _cartridge.Info.IsPal;
        Ppu.CounterLatchEnabled = (_pio & 0x80) != 0;
        FrameReady = false;
    }

    /// <summary>
    /// The S-CPU's view of the SA-1: the shared register file at $2200-$23FF,
    /// the coprocessor's internal RAM at $3000-$37FF, the backup-RAM window at
    /// $6000-$7FFF, and the linear backup RAM in banks $40-$4F.
    /// </summary>
    /// <summary>
    /// The Super FX's register file and instruction cache at $3000-$34FF, plus
    /// the Game Pak RAM it shares with the S-CPU at $6000-$7FFF and banks
    /// $70-$71. While the GSU owns a resource the S-CPU reads open bus.
    /// </summary>
    private bool TryReadSuperFx(byte bank, ushort address, out byte value)
    {
        if (_superFx is null)
        {
            value = 0;
            return false;
        }

        if (bank is 0x70 or 0x71)
        {
            value = _superFx.OwnsRam
                ? _openBus
                : _superFx.ReadRam((uint)(((bank - 0x70) << 16) | address));
            return true;
        }

        if (!IsSystemBank(bank))
        {
            value = 0;
            return false;
        }

        if (address is >= 0x3000 and <= 0x34FF)
        {
            value = _superFx.ReadRegister(address);
            return true;
        }

        if (address is >= 0x6000 and <= 0x7FFF)
        {
            value = _superFx.OwnsRam ? _openBus : _superFx.ReadRam((uint)(address - 0x6000));
            return true;
        }

        if (address >= 0x8000 && _superFx.OwnsRom)
        {
            value = _openBus;
            return true;
        }

        value = 0;
        return false;
    }

    private bool TryWriteSuperFx(byte bank, ushort address, byte value)
    {
        if (_superFx is null)
        {
            return false;
        }

        if (bank is 0x70 or 0x71)
        {
            if (!_superFx.OwnsRam)
            {
                _superFx.WriteRam((uint)(((bank - 0x70) << 16) | address), value);
            }

            return true;
        }

        if (!IsSystemBank(bank))
        {
            return false;
        }

        if (address is >= 0x3000 and <= 0x34FF)
        {
            _superFx.WriteRegister(address, value);
            return true;
        }

        if (address is >= 0x6000 and <= 0x7FFF)
        {
            if (!_superFx.OwnsRam)
            {
                _superFx.WriteRam((uint)(address - 0x6000), value);
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// The S-DD1's own registers at $4800-$4807, plus every ROM read, which has
    /// to pass through the chip's bank mapper rather than the plain LoROM
    /// projection the cartridge would otherwise apply.
    /// </summary>
    private bool TryReadSdd1(byte bank, ushort address, out byte value)
    {
        if (_sdd1 is null)
        {
            value = 0;
            return false;
        }

        if (IsSystemBank(bank) && address is >= 0x4800 and <= 0x4807)
        {
            value = _sdd1.ReadRegister(address);
            return true;
        }

        if (bank >= 0xC0 || (IsSystemBank(bank) && address >= 0x8000))
        {
            value = _sdd1.ReadRom(((uint)bank << 16) | address);
            return true;
        }

        value = 0;
        return false;
    }

    private bool TryWriteSdd1(byte bank, ushort address, byte value)
    {
        if (_sdd1 is null || !IsSystemBank(bank) || address is < 0x4800 or > 0x4807)
        {
            return false;
        }

        _sdd1.WriteRegister(address, value);
        return true;
    }

    private bool TryReadSa1(byte bank, ushort address, out byte value)
    {
        if (_sa1 is null)
        {
            value = 0;
            return false;
        }

        if (bank is >= 0x40 and <= 0x4F)
        {
            value = _sa1.ReadBackupRam((uint)(((bank - 0x40) << 16) | address));
            return true;
        }

        // Banks $C0-$FF are ROM through the super MMC for the S-CPU too, not
        // the plain LoROM projection the cartridge mapper would apply.
        if (bank >= 0xC0)
        {
            value = _sa1.ReadCartridgeRom(bank, address);
            return true;
        }

        if (!IsSystemBank(bank))
        {
            value = 0;
            return false;
        }

        // The redirected interrupt vectors have to be tested before the ROM
        // window below, because they live inside it.
        if (bank == 0x00 && address >= 0xFFEA && _sa1.TryReadSnesVector(address, out value))
        {
            return true;
        }

        switch (address)
        {
            case >= 0x2200 and <= 0x23FF:
                value = _sa1.ReadRegister(address);
                return true;
            case >= 0x3000 and <= 0x37FF:
                value = _sa1.ReadInternalRam((ushort)(address - 0x3000));
                return true;
            case >= 0x6000 and <= 0x7FFF:
                value = _sa1.ReadSnesBackupRamWindow(address);
                return true;
            case >= 0x8000:
                value = _sa1.ReadCartridgeRom(bank, address);
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private bool TryWriteSa1(byte bank, ushort address, byte value)
    {
        if (_sa1 is null)
        {
            return false;
        }

        if (bank is >= 0x40 and <= 0x4F)
        {
            _sa1.WriteBackupRam((uint)(((bank - 0x40) << 16) | address), value);
            return true;
        }

        if (!IsSystemBank(bank))
        {
            return false;
        }

        switch (address)
        {
            case >= 0x2200 and <= 0x23FF:
                _sa1.WriteRegister(address, value);
                return true;
            case >= 0x3000 and <= 0x37FF:
                _sa1.WriteInternalRam((ushort)(address - 0x3000), value);
                return true;
            case >= 0x6000 and <= 0x7FFF:
                _sa1.WriteSnesBackupRamWindow(address, value);
                return true;
            default:
                return false;
        }
    }

    private bool TryReadCx4(byte bank, ushort address, out byte value)
    {
        value = 0;
        if (_cx4 is null || !TryDecodeCx4Address(bank, address))
        {
            return false;
        }

        value = _cx4.Read(address);
        return true;
    }

    private bool TryPeekCx4(byte bank, ushort address, out byte value)
    {
        value = 0;
        if (_cx4 is null || !TryDecodeCx4Address(bank, address))
        {
            return false;
        }

        value = _cx4.Peek(address);
        return true;
    }

    private bool TryWriteCx4(byte bank, ushort address, byte value)
    {
        if (_cx4 is null || !TryDecodeCx4Address(bank, address))
        {
            return false;
        }

        _cx4.Write(address, value);
        return true;
    }

    private static bool TryDecodeCx4Address(byte bank, ushort address) =>
        (bank <= 0x3F || bank is >= 0x80 and <= 0xBF) &&
        address is >= 0x6000 and < 0x8000;

    private bool TryReadDsp1(byte bank, ushort address, out byte value)
    {
        value = 0;
        if (_dsp1 is null || !TryDecodeDsp1Address(bank, address, out var isStatus))
        {
            return false;
        }

        value = isStatus ? _dsp1.ReadStatus() : _dsp1.ReadData();
        return true;
    }

    private bool TryWriteDsp1(byte bank, ushort address, byte value)
    {
        if (_dsp1 is null || !TryDecodeDsp1Address(bank, address, out var isStatus))
        {
            return false;
        }

        if (!isStatus)
        {
            _dsp1.WriteData(value);
        }

        return true;
    }

    private bool TryPeekDsp1(byte bank, ushort address, out byte value)
    {
        value = 0;
        if (_dsp1 is null || !TryDecodeDsp1Address(bank, address, out var isStatus))
        {
            return false;
        }

        value = isStatus ? _dsp1.ReadStatus() : _dsp1.PeekData();
        return true;
    }

    private bool TryDecodeDsp1Address(byte bank, ushort address, out bool isStatus)
    {
        isStatus = false;
        if (_cartridge.Info.MapMode == SnesMapMode.HiRom)
        {
            if (bank is not (<= 0x1F or >= 0x80 and <= 0x9F) ||
                address is < 0x6000 or >= 0x8000)
            {
                return false;
            }

            isStatus = address >= 0x7000;
            return true;
        }

        if (_cartridge.Info.RomSize > 0x100000)
        {
            if (bank is not (>= 0x60 and <= 0x6F or >= 0xE0 and <= 0xEF) ||
                address >= 0x8000)
            {
                return false;
            }

            isStatus = address >= 0x4000;
            return true;
        }

        if (bank is not (>= 0x20 and <= 0x3F or >= 0xA0 and <= 0xBF) ||
            address < 0x8000)
        {
            return false;
        }

        isStatus = address >= 0xC000;
        return true;
    }

    private byte ReadPpuOrApu(ushort address)
    {
        if (address is >= 0x2140 and <= 0x217F)
        {
            return _apu.ReadOutputPort((address - 0x2140) & 3);
        }

        if (address == 0x2137)
        {
            if ((_pio & 0x80) != 0)
            {
                LatchPpuCounters();
            }

            return _openBus;
        }

        return address switch
        {
            0x2180 => _wram[_wramAddress++ & 0x1FFFF],
            _ => Ppu.ReadRegister(address)
        };
    }

    private void WritePpuOrApu(ushort address, byte value)
    {
        if (address is >= 0x2140 and <= 0x217F)
        {
            var port = (address - 0x2140) & 3;
            _apu.WriteInputPort(port, value);
            return;
        }

        switch (address)
        {
            case 0x2180:
                _wram[_wramAddress++ & 0x1FFFF] = value;
                break;
            case 0x2181:
                _wramAddress = (_wramAddress & 0x1FF00) | value;
                break;
            case 0x2182:
                _wramAddress = (_wramAddress & 0x100FF) | ((uint)value << 8);
                break;
            case 0x2183:
                _wramAddress = (_wramAddress & 0x0FFFF) | ((uint)(value & 1) << 16);
                break;
            default:
                Ppu.WriteRegister(address, value, _scanline);
                break;
        }
    }

    private byte ReadCpuRegister(ushort address)
    {
        return address switch
        {
            0x4210 => ReadNmiFlag(),
            0x4211 => ReadIrqFlag(),
            0x4212 => ReadHvbJoy(),
            0x4213 => _pio,
            0x4214 => (byte)_divisionQuotient,
            0x4215 => (byte)(_divisionQuotient >> 8),
            0x4216 => (byte)_multiplyOrRemainder,
            0x4217 => (byte)(_multiplyOrRemainder >> 8),
            0x4218 => (byte)_automaticControllerOne,
            0x4219 => (byte)(_automaticControllerOne >> 8),
            0x421A => (byte)_automaticControllerTwo,
            0x421B => (byte)(_automaticControllerTwo >> 8),
            _ => 0
        };
    }

    private void WriteCpuRegister(ushort address, byte value)
    {
        switch (address)
        {
            case 0x4200:
                var nmiWasEnabled = (_nmitimen & 0x80) != 0;
                _nmitimen = value;
                if ((value & 0x30) == 0)
                {
                    _irqFlag = false;
                }
                // Enabling NMI during VBlank only creates the hardware's
                // immediate edge while RDNMI is still asserted. Software
                // commonly reads $4210, briefly disables NMI in its handler,
                // then restores $4200; retriggering after that acknowledgement
                // recursively nests the handler and eventually exhausts the
                // native-mode stack.
                if (!nmiWasEnabled &&
                    (value & 0x80) != 0 &&
                    _vblank &&
                    _nmiFlag)
                {
                    _nmiPending = true;
                }
                if ((value & 0x01) == 0)
                {
                    _automaticControllerReadRemainingMasterClocks = 0;
                }
                break;
            case 0x4201:
                if ((_pio & 0x80) != 0 && (value & 0x80) == 0)
                {
                    LatchPpuCounters();
                }
                _pio = value;
                Ppu.CounterLatchEnabled = (value & 0x80) != 0;
                break;
            case 0x4202:
                _wrmpya = value;
                break;
            case 0x4203:
                BeginMultiplication(value);
                break;
            case 0x4204:
                _wrdiv = (ushort)((_wrdiv & 0xFF00) | value);
                break;
            case 0x4205:
                _wrdiv = (ushort)((_wrdiv & 0x00FF) | (value << 8));
                break;
            case 0x4206:
                BeginDivision(value);
                break;
            case 0x4207:
                _horizontalTimer = (ushort)((_horizontalTimer & 0x0100) | value);
                break;
            case 0x4208:
                _horizontalTimer = (ushort)((_horizontalTimer & 0x00FF) | ((value & 1) << 8));
                break;
            case 0x4209:
                _verticalTimer = (ushort)((_verticalTimer & 0x0100) | value);
                break;
            case 0x420A:
                _verticalTimer = (ushort)((_verticalTimer & 0x00FF) | ((value & 1) << 8));
                break;
            case 0x420B:
                DmaTransferCount++;
                LastDmaChannelMask = value;
                PerformDma(value);
                break;
            case 0x420C:
                HdmaEnableWrites++;
                _hdmaEnable = value;
                break;
            case 0x420D:
                _memorySpeedControl = (byte)(value & 0x01);
                break;
        }
    }

    private void PerformDma(byte enabledChannels)
    {
        var dmaMasterClocks = enabledChannels == 0 ? 0 : 8;
        for (var channel = 0; channel < 8; channel++)
        {
            if ((enabledChannels & (1 << channel)) == 0)
            {
                continue;
            }

            var registerBase = channel * 16;
            var control = _dmaRegisters[registerBase];
            var pattern = DmaPatterns[control & 7];
            var bAddress = _dmaRegisters[registerBase + 1];
            var aAddress = (ushort)(_dmaRegisters[registerBase + 2] | (_dmaRegisters[registerBase + 3] << 8));
            var aBank = _dmaRegisters[registerBase + 4];
            var transferSize = _dmaRegisters[registerBase + 5] | (_dmaRegisters[registerBase + 6] << 8);
            if (transferSize == 0) transferSize = 0x10000;
            dmaMasterClocks += 8 + (transferSize * 8);
            var fixedAddress = (control & 0x08) != 0;
            var decrement = (control & 0x10) != 0;
            var ppuToCpu = (control & 0x80) != 0;

            // An S-DD1 transfer looks like an ordinary ROM-to-PPU DMA; the chip
            // substitutes decompressed bytes for the raw ROM contents.
            var sdd1Transfer = !ppuToCpu &&
                _sdd1?.TryBeginTransfer(channel, ((uint)aBank << 16) | aAddress) == true;

            for (var index = 0; index < transferSize; index++)
            {
                var aBusAddress = ((uint)aBank << 16) | aAddress;
                var bBusAddress = (ushort)(0x2100 + bAddress + pattern[index % pattern.Length]);
                if (ppuToCpu)
                {
                    Write(aBusAddress, Read(bBusAddress));
                }
                else if (sdd1Transfer)
                {
                    Write(bBusAddress, _sdd1!.ReadTransferByte());
                }
                else
                {
                    Write(bBusAddress, Read(aBusAddress));
                }

                if (!fixedAddress)
                {
                    aAddress = decrement ? (ushort)(aAddress - 1) : (ushort)(aAddress + 1);
                }
            }

            if (sdd1Transfer)
            {
                _sdd1!.EndTransfer();
            }

            _dmaRegisters[registerBase + 2] = (byte)aAddress;
            _dmaRegisters[registerBase + 3] = (byte)(aAddress >> 8);
            _dmaRegisters[registerBase + 5] = 0;
            _dmaRegisters[registerBase + 6] = 0;
        }

        if (_cpuInstructionTimingActive)
        {
            _cpuInstructionDmaMasterClocks += dmaMasterClocks;
        }
    }

    private void InitializeHdma()
    {
        _hdmaInitialized = true;
        for (var channel = 0; channel < 8; channel++)
        {
            var registerBase = channel * 16;
            _dmaRegisters[registerBase + 8] = _dmaRegisters[registerBase + 2];
            _dmaRegisters[registerBase + 9] = _dmaRegisters[registerBase + 3];
            _dmaRegisters[registerBase + 10] = 0;
            _hdmaTerminated[channel] = (_hdmaEnable & (1 << channel)) == 0;
            _hdmaDoTransfer[channel] = false;
        }
    }

    private void PerformHdmaScanline()
    {
        if (!_hdmaInitialized || _hdmaEnable == 0)
        {
            return;
        }

        for (var channel = 0; channel < 8; channel++)
        {
            if ((_hdmaEnable & (1 << channel)) == 0 || _hdmaTerminated[channel])
            {
                continue;
            }

            var registerBase = channel * 16;
            var lineCounter = _dmaRegisters[registerBase + 10];
            if ((lineCounter & 0x7F) == 0)
            {
                if (!ReloadHdmaLineDescriptor(channel))
                {
                    continue;
                }

                lineCounter = _dmaRegisters[registerBase + 10];
            }

            if (_hdmaDoTransfer[channel])
            {
                TransferHdmaChannel(channel);
            }

            var remainingLines = (lineCounter & 0x7F) == 0
                ? 128
                : lineCounter & 0x7F;
            remainingLines--;
            _dmaRegisters[registerBase + 10] =
                (byte)((lineCounter & 0x80) | (remainingLines & 0x7F));
            _hdmaDoTransfer[channel] = (lineCounter & 0x80) != 0;
        }
    }

    private bool ReloadHdmaLineDescriptor(int channel)
    {
        var registerBase = channel * 16;
        var tableBank = _dmaRegisters[registerBase + 4];
        var tableAddress = (ushort)(
            _dmaRegisters[registerBase + 8] |
            (_dmaRegisters[registerBase + 9] << 8));
        var descriptor = Read(((uint)tableBank << 16) | tableAddress++);
        _dmaRegisters[registerBase + 8] = (byte)tableAddress;
        _dmaRegisters[registerBase + 9] = (byte)(tableAddress >> 8);
        _dmaRegisters[registerBase + 10] = descriptor;
        if (descriptor == 0)
        {
            _hdmaTerminated[channel] = true;
            _hdmaDoTransfer[channel] = false;
            return false;
        }

        var indirect = (_dmaRegisters[registerBase] & 0x40) != 0;
        if (indirect)
        {
            var low = Read(((uint)tableBank << 16) | tableAddress++);
            var high = Read(((uint)tableBank << 16) | tableAddress++);
            _dmaRegisters[registerBase + 5] = low;
            _dmaRegisters[registerBase + 6] = high;
            _dmaRegisters[registerBase + 8] = (byte)tableAddress;
            _dmaRegisters[registerBase + 9] = (byte)(tableAddress >> 8);
        }

        _hdmaDoTransfer[channel] = true;
        return true;
    }

    /// <summary>
    /// Per-scanline HDMA writes counted by destination PPU register, so an
    /// effect that renders wrong can be traced to the registers driving it.
    /// </summary>
    internal long[] HdmaWritesByRegister { get; } = new long[256];

    private void TransferHdmaChannel(int channel)
    {
        var registerBase = channel * 16;
        var control = _dmaRegisters[registerBase];
        var pattern = DmaPatterns[control & 7];
        var bAddress = _dmaRegisters[registerBase + 1];
        var indirect = (control & 0x40) != 0;
        var dataBank = indirect
            ? _dmaRegisters[registerBase + 7]
            : _dmaRegisters[registerBase + 4];
        var dataAddressOffset = indirect ? 5 : 8;
        var dataAddress = (ushort)(
            _dmaRegisters[registerBase + dataAddressOffset] |
            (_dmaRegisters[registerBase + dataAddressOffset + 1] << 8));
        var ppuToCpu = (control & 0x80) != 0;

        for (var index = 0; index < pattern.Length; index++)
        {
            var aBusAddress = ((uint)dataBank << 16) | dataAddress++;
            var bBusAddress = (ushort)(0x2100 + bAddress + pattern[index]);
            if (ppuToCpu)
            {
                Write(aBusAddress, Read(bBusAddress));
            }
            else
            {
                HdmaWritesByRegister[bBusAddress & 0xFF]++;
                Write(bBusAddress, Read(aBusAddress));
            }
        }

        _dmaRegisters[registerBase + dataAddressOffset] = (byte)dataAddress;
        _dmaRegisters[registerBase + dataAddressOffset + 1] = (byte)(dataAddress >> 8);
    }

    private void LatchAutomaticControllers()
    {
        _automaticControllerOne = 0;
        _automaticControllerTwo = 0;
        _pendingAutomaticControllerOne =
            EncodeAutomaticController(Volatile.Read(ref _controllerOne));
        _pendingAutomaticControllerTwo =
            EncodeAutomaticController(Volatile.Read(ref _controllerTwo));
        _automaticControllerReadRemainingMasterClocks =
            AutomaticControllerReadMasterClocks;
    }

    private static ushort EncodeAutomaticController(ushort serialButtons)
    {
        ushort result = 0;
        for (var button = 0; button < 12; button++)
        {
            if ((serialButtons & (1 << button)) != 0)
            {
                // The first serial bit (B) is the most significant bit of
                // the 16-bit automatic result. On the little-endian S-CPU,
                // $4218 therefore exposes A/X/L/R and $4219 exposes
                // B/Y/Select/Start/Up/Down/Left/Right.
                result |= (ushort)(1 << (15 - button));
            }
        }

        return result;
    }

    private void WriteControllerStrobe(byte value)
    {
        _controllerStrobe = (value & 1) != 0;
        if (_controllerStrobe)
        {
            _controllerShiftOne = Volatile.Read(ref _controllerOne);
            _controllerShiftTwo = Volatile.Read(ref _controllerTwo);
        }
    }

    private byte ReadController(int player)
    {
        ref var shift = ref player == 1 ? ref _controllerShiftOne : ref _controllerShiftTwo;
        if (_controllerStrobe)
        {
            shift = player == 1 ? Volatile.Read(ref _controllerOne) : Volatile.Read(ref _controllerTwo);
        }

        var value = (byte)(shift & 1);
        if (!_controllerStrobe)
        {
            shift = (ushort)((shift >> 1) | 0x8000);
        }

        return player == 2
            ? (byte)((_openBus & 0xE0) | 0x1C | value)
            : (byte)((_openBus & 0xFC) | value);
    }

    private void AdvanceTimedCpuOperations(int masterClocks)
    {
        if (_automaticControllerReadRemainingMasterClocks > 0)
        {
            _automaticControllerReadRemainingMasterClocks -= masterClocks;
            if (_automaticControllerReadRemainingMasterClocks <= 0)
            {
                _automaticControllerReadRemainingMasterClocks = 0;
                _automaticControllerOne = _pendingAutomaticControllerOne;
                _automaticControllerTwo = _pendingAutomaticControllerTwo;
            }
        }

        if (_multiplyCyclesRemaining == 0 && _divisionCyclesRemaining == 0)
        {
            _mathClockRemainder = 0;
            return;
        }

        _mathClockRemainder += masterClocks;
        while (_mathClockRemainder >= MasterClocksPerCpuCycle &&
               (_multiplyCyclesRemaining > 0 || _divisionCyclesRemaining > 0))
        {
            _mathClockRemainder -= MasterClocksPerCpuCycle;
            StepMathUnit();
        }
    }

    private void BeginMultiplication(byte multiplier)
    {
        _multiplyOrRemainder = 0;
        if (_multiplyCyclesRemaining > 0 || _divisionCyclesRemaining > 0)
        {
            return;
        }

        _divisionQuotient = (ushort)((multiplier << 8) | _wrmpya);
        _mathShift = multiplier;
        _mathClockRemainder = 0;
        _multiplyCyclesRemaining = 8;
    }

    private void BeginDivision(byte divisor)
    {
        _multiplyOrRemainder = _wrdiv;
        if (_multiplyCyclesRemaining > 0 || _divisionCyclesRemaining > 0)
        {
            return;
        }

        _mathShift = (uint)divisor << 16;
        _mathClockRemainder = 0;
        _divisionCyclesRemaining = 16;
    }

    private void StepMathUnit()
    {
        if (_multiplyCyclesRemaining > 0)
        {
            _multiplyCyclesRemaining--;
            if ((_divisionQuotient & 1) != 0)
            {
                _multiplyOrRemainder =
                    (ushort)(_multiplyOrRemainder + _mathShift);
            }

            _divisionQuotient >>= 1;
            _mathShift <<= 1;
        }

        if (_divisionCyclesRemaining > 0)
        {
            _divisionCyclesRemaining--;
            _divisionQuotient <<= 1;
            _mathShift >>= 1;
            if (_multiplyOrRemainder >= _mathShift)
            {
                _multiplyOrRemainder =
                    (ushort)(_multiplyOrRemainder - _mathShift);
                _divisionQuotient |= 1;
            }
        }
    }

    private void LatchPpuCounters()
    {
        CounterLatchCount++;
        Ppu.LatchCounters(
            (ushort)(_masterClockRemainder / 4),
            (ushort)_scanline);
    }

    private byte ReadNmiFlag()
    {
        // A game that polls RDNMI in its main loop while its NMI handler also
        // acknowledges it can starve: the handler consumes the flag before the
        // poll observes it. Counting both cases separates "the poll never sees
        // the flag" from "the poll works and the stall is downstream".
        RdnmiReads++;
        if (_nmiFlag)
        {
            RdnmiReadsWithFlagSet++;
        }

        if ((_nmitimen & 0x80) != 0)
        {
            RdnmiReadsWhileNmiEnabled++;
        }

        var value = (byte)((_nmiFlag ? 0x82 : 0x02) | (_openBus & 0x70));
        _nmiFlag = false;
        return value;
    }

    internal long VerticalIrqScanlinesArmed { get; private set; }

    internal long VerticalIrqMatches { get; private set; }

    internal long VblankCount { get; private set; }

    internal long VblankNmiArmed { get; private set; }

    internal long RdnmiReads { get; private set; }

    internal long RdnmiReadsWithFlagSet { get; private set; }

    internal long RdnmiReadsWhileNmiEnabled { get; private set; }

    private byte ReadIrqFlag()
    {
        var value = (byte)((_irqFlag ? 0x80 : 0) | (_openBus & 0x7F));
        _irqFlag = false;
        return value;
    }

    private byte LatchOpenBus(byte value)
    {
        _openBus = value;
        return value;
    }

    private static bool IsSystemBank(byte bank) => bank <= 0x3F || bank is >= 0x80 and <= 0xBF;
}

internal readonly record struct CpuInstructionTiming(int CpuCycles, int MasterClocks);
