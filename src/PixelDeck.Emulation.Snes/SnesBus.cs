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

internal sealed class SnesBus
{
    private const int MasterClocksPerScanline = 1_364;

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
    private readonly SnesApu _apu = new();
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
    private bool _controllerStrobe;

    public SnesBus(SnesCartridge cartridge)
    {
        _cartridge = cartridge;
        Ppu = new SnesPpu();
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

    public byte ReadDspRegister(byte address) => _apu.ReadDspRegister(address);

    public int ReadAudioSamples(Span<float> destination) => _apu.ReadSamples(destination);

    public void ClearAudioSamples() => _apu.ClearSamples();

    public byte Read(uint address)
    {
        address &= 0xFFFFFF;
        var bank = (byte)(address >> 16);
        var offset = (ushort)address;

        if (bank is 0x7E or 0x7F)
        {
            return _wram[((bank - 0x7E) << 16) | offset];
        }

        if (IsSystemBank(bank))
        {
            if (offset < 0x2000)
            {
                return _wram[offset];
            }

            if (offset is >= 0x2100 and <= 0x21FF)
            {
                return ReadPpuOrApu(offset);
            }

            if (offset is 0x4016 or 0x4017)
            {
                return ReadController(offset == 0x4016 ? 1 : 2);
            }

            if (offset is >= 0x4210 and <= 0x421F)
            {
                return ReadCpuRegister(offset);
            }

            if (offset is >= 0x4300 and <= 0x437F)
            {
                return _dmaRegisters[offset - 0x4300];
            }
        }

        return _cartridge.Read(address);
    }

    public void Write(uint address, byte value)
    {
        address &= 0xFFFFFF;
        var bank = (byte)(address >> 16);
        var offset = (ushort)address;

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

    public void BeginFrame() => FrameReady = false;

    public void Clock(int cpuCycles)
    {
        var masterClocks = Math.Max(1, cpuCycles) * 6;
        _apu.ClockMasterClocks(masterClocks);
        while (masterClocks > 0)
        {
            var clocksToEndOfScanline = MasterClocksPerScanline - _masterClockRemainder;
            var elapsed = Math.Min(masterClocks, clocksToEndOfScanline);
            CheckHorizontalIrq(_masterClockRemainder, _masterClockRemainder + elapsed);
            _masterClockRemainder += elapsed;
            masterClocks -= elapsed;

            if (_masterClockRemainder < MasterClocksPerScanline)
            {
                continue;
            }

            _masterClockRemainder = 0;
            _scanline++;
            CheckVerticalIrqAtScanlineStart();

            if (_scanline == SnesPpu.Height + 1)
            {
                _vblank = true;
                _nmiFlag = true;
                if ((_nmitimen & 0x80) != 0)
                {
                    _nmiPending = true;
                }

                LatchAutomaticControllers();
            }

            var totalScanlines = _cartridge.Info.IsPal ? 312 : 262;
            if (_scanline >= totalScanlines)
            {
                _scanline = 0;
                _vblank = false;
                Ppu.RenderFrame();
                FrameReady = true;
            }
        }
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
        if (verticalIrqEnabled &&
            _scanline == _verticalTimer &&
            (!horizontalIrqEnabled || _horizontalTimer == 0))
        {
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

    internal void SaveState(BinaryWriter writer)
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
        writer.Write(_controllerStrobe);
        _apu.SaveState(writer);
        Ppu.SaveState(writer);
    }

    internal void LoadState(BinaryReader reader)
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
        _controllerStrobe = reader.ReadBoolean();
        _apu.LoadState(reader);
        Ppu.LoadState(reader);
        FrameReady = false;
    }

    private byte ReadPpuOrApu(ushort address)
    {
        if (address is >= 0x2140 and <= 0x217F)
        {
            return _apu.ReadOutputPort((address - 0x2140) & 3);
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
                Ppu.WriteRegister(address, value);
                break;
        }
    }

    private byte ReadCpuRegister(ushort address)
    {
        return address switch
        {
            0x4210 => ReadNmiFlag(),
            0x4211 => ReadIrqFlag(),
            0x4212 => (byte)((_vblank ? 0x80 : 0) | (_masterClockRemainder >= 1_096 ? 0x40 : 0)),
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
                if (!nmiWasEnabled && (value & 0x80) != 0 && _vblank)
                {
                    _nmiPending = true;
                }
                break;
            case 0x4202:
                _wrmpya = value;
                break;
            case 0x4203:
                _multiplyOrRemainder = (ushort)(_wrmpya * value);
                break;
            case 0x4204:
                _wrdiv = (ushort)((_wrdiv & 0xFF00) | value);
                break;
            case 0x4205:
                _wrdiv = (ushort)((_wrdiv & 0x00FF) | (value << 8));
                break;
            case 0x4206:
                if (value == 0)
                {
                    _divisionQuotient = 0xFFFF;
                    _multiplyOrRemainder = _wrdiv;
                }
                else
                {
                    _divisionQuotient = (ushort)(_wrdiv / value);
                    _multiplyOrRemainder = (ushort)(_wrdiv % value);
                }
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
                PerformDma(value);
                break;
            case 0x420C:
                _hdmaEnable = value;
                break;
        }
    }

    private void PerformDma(byte enabledChannels)
    {
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
            var fixedAddress = (control & 0x08) != 0;
            var decrement = (control & 0x10) != 0;
            var ppuToCpu = (control & 0x80) != 0;

            for (var index = 0; index < transferSize; index++)
            {
                var aBusAddress = ((uint)aBank << 16) | aAddress;
                var bBusAddress = (ushort)(0x2100 + bAddress + pattern[index % pattern.Length]);
                if (ppuToCpu)
                {
                    Write(aBusAddress, Read(bBusAddress));
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

            _dmaRegisters[registerBase + 2] = (byte)aAddress;
            _dmaRegisters[registerBase + 3] = (byte)(aAddress >> 8);
            _dmaRegisters[registerBase + 5] = 0;
            _dmaRegisters[registerBase + 6] = 0;
        }
    }

    private void LatchAutomaticControllers()
    {
        _automaticControllerOne = EncodeAutomaticController(Volatile.Read(ref _controllerOne));
        _automaticControllerTwo = EncodeAutomaticController(Volatile.Read(ref _controllerTwo));
    }

    private static ushort EncodeAutomaticController(ushort serialButtons)
    {
        ushort result = 0;
        for (var button = 0; button < 12; button++)
        {
            if ((serialButtons & (1 << button)) != 0)
            {
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

        return value;
    }

    private byte ReadNmiFlag()
    {
        var value = (byte)(_nmiFlag ? 0x82 : 0x02);
        _nmiFlag = false;
        return value;
    }

    private byte ReadIrqFlag()
    {
        var value = (byte)(_irqFlag ? 0x80 : 0);
        _irqFlag = false;
        return value;
    }

    private static bool IsSystemBank(byte bank) => bank <= 0x3F || bank is >= 0x80 and <= 0xBF;
}
