namespace PixelDeck.Emulation.Snes;

internal sealed class SnesApu
{
    private const int MasterClocksPerApuCycle = 21;

    private static readonly byte[] BootRom =
    [
        0xCD, 0xEF, 0xBD, 0xE8, 0x00, 0xC6, 0x1D, 0xD0,
        0xFC, 0x8F, 0xAA, 0xF4, 0x8F, 0xBB, 0xF5, 0x78,
        0xCC, 0xF4, 0xD0, 0xFB, 0x2F, 0x19, 0xEB, 0xF4,
        0xD0, 0xFC, 0x7E, 0xF4, 0xD0, 0x0B, 0xE4, 0xF5,
        0xCB, 0xF4, 0xD7, 0x00, 0xFC, 0xD0, 0xF3, 0xAB,
        0x01, 0x10, 0xEF, 0x7E, 0xF4, 0x10, 0xEB, 0xBA,
        0xF6, 0xDA, 0x00, 0xBA, 0xF4, 0xC4, 0xF4, 0xDD,
        0x5D, 0xD0, 0xDB, 0x1F, 0x00, 0x00, 0xC0, 0xFF
    ];

    private readonly byte[] _ram = new byte[64 * 1024];
    private readonly byte[] _cpuToApuPorts = new byte[4];
    private readonly byte[] _apuToCpuPorts = new byte[4];
    private readonly ApuTimer[] _timers = [new(128), new(128), new(16)];
    private readonly SnesDsp _dsp;
    private readonly Spc700 _cpu;
    private int _masterClockRemainder;
    private int _cpuCyclesRemaining = 7;
    private byte _dspAddress;
    private bool _bootRomReadable = true;

    public SnesApu()
    {
        _dsp = new SnesDsp(_ram);
        _cpu = new Spc700(Read, Write);
        _cpu.Reset();
    }

    public ushort OutputWord =>
        (ushort)(_apuToCpuPorts[0] | (_apuToCpuPorts[1] << 8));

    public long ExecutedInstructions => _cpu.ExecutedInstructions;

    public byte FirstUnsupportedOpcode => _cpu.FirstUnsupportedOpcode;

    public ushort FirstUnsupportedAddress => _cpu.FirstUnsupportedAddress;

    public int BufferedSampleCount => _dsp.BufferedSampleCount;

    public int ActiveVoiceCount => _dsp.ActiveVoiceCount;

    public long DroppedSampleCount => _dsp.DroppedSampleCount;

    public byte ReadDspRegister(byte address) => _dsp.ReadRegister(address);

    public byte ReadOutputPort(int port) => _apuToCpuPorts[port & 3];

    public void WriteInputPort(int port, byte value) => _cpuToApuPorts[port & 3] = value;

    public int ReadSamples(Span<float> destination) => _dsp.ReadSamples(destination);

    public void ClearSamples() => _dsp.ClearSamples();

    public void ClockMasterClocks(int masterClocks)
    {
        _masterClockRemainder += masterClocks;
        while (_masterClockRemainder >= MasterClocksPerApuCycle)
        {
            _masterClockRemainder -= MasterClocksPerApuCycle;
            ClockCycle();
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(_ram);
        writer.Write(_cpuToApuPorts);
        writer.Write(_apuToCpuPorts);
        writer.Write(_masterClockRemainder);
        writer.Write(_cpuCyclesRemaining);
        writer.Write(_dspAddress);
        writer.Write(_bootRomReadable);
        foreach (var timer in _timers)
        {
            timer.SaveState(writer);
        }

        _dsp.SaveState(writer);
        _cpu.SaveState(writer);
    }

    public void LoadState(BinaryReader reader)
    {
        reader.ReadExactly(_ram);
        reader.ReadExactly(_cpuToApuPorts);
        reader.ReadExactly(_apuToCpuPorts);
        _masterClockRemainder = reader.ReadInt32();
        _cpuCyclesRemaining = reader.ReadInt32();
        _dspAddress = reader.ReadByte();
        _bootRomReadable = reader.ReadBoolean();
        foreach (var timer in _timers)
        {
            timer.LoadState(reader);
        }

        _dsp.LoadState(reader);
        _cpu.LoadState(reader);
    }

    private void ClockCycle()
    {
        if (_cpuCyclesRemaining <= 0)
        {
            _cpuCyclesRemaining = Math.Max(1, _cpu.Step());
        }

        _cpuCyclesRemaining--;
        foreach (var timer in _timers)
        {
            timer.Clock();
        }

        _dsp.ClockCycle();
    }

    private byte Read(ushort address)
    {
        return address switch
        {
            0x00F0 or 0x00F1 or 0x00FA or 0x00FB or 0x00FC => 0,
            0x00F2 => _dspAddress,
            0x00F3 => _dsp.ReadRegister(_dspAddress),
            >= 0x00F4 and <= 0x00F7 => _cpuToApuPorts[address - 0xF4],
            0x00F8 or 0x00F9 => _ram[address],
            >= 0x00FD and <= 0x00FF => _timers[address - 0xFD].ReadCounter(),
            >= 0xFFC0 when _bootRomReadable => BootRom[address - 0xFFC0],
            _ => _ram[address]
        };
    }

    private void Write(ushort address, byte value)
    {
        switch (address)
        {
            case 0x00F0:
                break;
            case 0x00F1:
                for (var index = 0; index < _timers.Length; index++)
                {
                    _timers[index].SetEnabled((value & (1 << index)) != 0);
                }

                if ((value & 0x10) != 0)
                {
                    _cpuToApuPorts[0] = 0;
                    _cpuToApuPorts[1] = 0;
                }

                if ((value & 0x20) != 0)
                {
                    _cpuToApuPorts[2] = 0;
                    _cpuToApuPorts[3] = 0;
                }

                _bootRomReadable = (value & 0x80) != 0;
                break;
            case 0x00F2:
                _dspAddress = value;
                break;
            case 0x00F3:
                if (_dspAddress < 0x80)
                {
                    _dsp.WriteRegister(_dspAddress, value);
                }
                break;
            case >= 0x00F4 and <= 0x00F7:
                _apuToCpuPorts[address - 0xF4] = value;
                break;
            case 0x00F8:
            case 0x00F9:
                break;
            case >= 0x00FA and <= 0x00FC:
                _timers[address - 0xFA].Target = value;
                break;
        }

        _ram[address] = value;
    }

    private sealed class ApuTimer
    {
        private readonly int _period;
        private int _cyclesRemaining;
        private byte _divider;
        private byte _counter;
        private bool _enabled;

        public ApuTimer(int period)
        {
            _period = period;
            _cyclesRemaining = period;
        }

        public byte Target { get; set; }

        public void SetEnabled(bool enabled)
        {
            if (!_enabled && enabled)
            {
                _divider = 0;
                _counter = 0;
            }

            _enabled = enabled;
        }

        public void Clock()
        {
            if (--_cyclesRemaining > 0)
            {
                return;
            }

            _cyclesRemaining = _period;
            if (!_enabled)
            {
                return;
            }

            _divider++;
            if (Target == 0 ? _divider == 0 : _divider == Target)
            {
                _divider = 0;
                _counter = (byte)((_counter + 1) & 0x0F);
            }
        }

        public byte ReadCounter()
        {
            var value = _counter;
            _counter = 0;
            return value;
        }

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(_cyclesRemaining);
            writer.Write(_divider);
            writer.Write(_counter);
            writer.Write(_enabled);
            writer.Write(Target);
        }

        public void LoadState(BinaryReader reader)
        {
            _cyclesRemaining = reader.ReadInt32();
            _divider = reader.ReadByte();
            _counter = reader.ReadByte();
            _enabled = reader.ReadBoolean();
            Target = reader.ReadByte();
        }
    }
}
