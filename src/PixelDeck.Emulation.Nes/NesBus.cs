namespace PixelDeck.Emulation.Nes;

[Flags]
public enum NesButton : byte
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    Select = 1 << 2,
    Start = 1 << 3,
    Up = 1 << 4,
    Down = 1 << 5,
    Left = 1 << 6,
    Right = 1 << 7
}

internal sealed class NesBus
{
    private readonly byte[] _ram = new byte[2_048];
    private readonly Cartridge _cartridge;
    private readonly NesController _controllerOne = new();
    private readonly NesController _controllerTwo = new();
    private byte _oamDmaPage;
    private bool _oamDmaPending;
    private byte _cpuOpenBus;

    public NesBus(Cartridge cartridge, NesEmulationOptions? options = null)
    {
        _cartridge = cartridge;
        Ppu = new NesPpu(cartridge, options ?? new NesEmulationOptions());
        Apu = new NesApu(() => _cartridge.ExpansionAudioOutput);
        Scheduler = new NesCycleScheduler(
            Apu,
            Ppu,
            () => Apu.IrqPending,
            () => _cartridge.IrqPending,
            _cartridge.ClockCpuCycle);
    }

    public NesPpu Ppu { get; }

    public NesApu Apu { get; }

    public NesCycleScheduler Scheduler { get; }

    public bool IrqPending => _cartridge.IrqPending || Apu.IrqPending;

    public byte Read(ushort address)
    {
        byte value;
        var updateExternalOpenBus = true;
        if (address < 0x2000)
        {
            value = _ram[address & 0x07FF];
        }
        else if (address < 0x4000)
        {
            value = Ppu.CpuReadRegister((ushort)(address & 0x0007));
        }
        else
        {
            value = address switch
            {
                0x4015 => Apu.ReadStatus(_cpuOpenBus),
                0x4016 => _controllerOne.Read(_cpuOpenBus),
                0x4017 => _controllerTwo.Read(_cpuOpenBus),
                >= 0x4020 => _cartridge.CpuRead(address),
                _ => _cpuOpenBus
            };
            // $4015 is driven by the CPU's internal APU bus. It exposes the
            // existing external-bus value on bit 5 without replacing that
            // external latch with the returned status byte.
            updateExternalOpenBus = address != 0x4015;
        }

        if (updateExternalOpenBus)
        {
            _cpuOpenBus = value;
        }

        return value;
    }

    public byte ReadForDma(
        ushort address,
        bool enableInternalIoReads,
        ref ushort previousReadAddress)
    {
        if (!enableInternalIoReads)
        {
            var externalValue = ReadExternalForDma(address);
            previousReadAddress = address;
            return externalValue;
        }

        // When DMA halts a CPU read from $4000-$401F, the 2A03 can select an
        // internal APU/controller register from the DMA address's low five
        // bits while the cartridge sees the full external address.
        var internalAddress = (ushort)(0x4000 | (address & 0x001F));
        var isSameAddress = internalAddress == address;
        byte value;
        switch (internalAddress)
        {
            case 0x4015:
                value = Read(internalAddress);
                if (!isSameAddress)
                {
                    _ = ReadExternalForDma(address);
                }

                break;
            case 0x4016:
            case 0x4017:
                value = previousReadAddress == internalAddress
                    ? _cpuOpenBus
                    : Read(internalAddress);
                if (!isSameAddress)
                {
                    var externalValue = ReadExternalForDma(address);
                    const byte controllerOpenBusMask = 0xE0;
                    value = (byte)(
                        (externalValue & controllerOpenBusMask) |
                        ((value & ~controllerOpenBusMask) &
                         (externalValue & ~controllerOpenBusMask)));
                }

                break;
            default:
                value = ReadExternalForDma(address);
                break;
        }

        previousReadAddress = internalAddress;
        return value;
    }

    public void Write(ushort address, byte value)
    {
        var previousOpenBus = _cpuOpenBus;
        _cpuOpenBus = value;
        _cartridge.ClockCpuWrite();
        if (address < 0x2000)
        {
            _ram[address & 0x07FF] = value;
            return;
        }

        if (address < 0x4000)
        {
            Ppu.CpuWriteRegister((ushort)(address & 0x0007), value, previousOpenBus);
            return;
        }

        switch (address)
        {
            case >= 0x4000 and <= 0x4013:
                Apu.WriteRegister(address, value);
                break;
            case 0x4014:
                _oamDmaPage = value;
                _oamDmaPending = true;
                break;
            case 0x4015:
                Apu.WriteRegister(address, value);
                break;
            case 0x4016:
                _controllerOne.WriteStrobe(value);
                _controllerTwo.WriteStrobe(value);
                break;
            case 0x4017:
                Apu.WriteRegister(address, value);
                break;
            case >= 0x4020:
                _cartridge.CpuWrite(address, value);
                break;
        }
    }

    public bool TryTakeOamDma(out byte page)
    {
        if (!_oamDmaPending)
        {
            page = 0;
            return false;
        }

        page = _oamDmaPage;
        _oamDmaPending = false;
        return true;
    }

    public void WriteOamDmaByte(byte value) => Ppu.WriteOamDmaByte(value);

    internal void SaveState(BinaryWriter writer)
    {
        writer.Write(_ram);
        writer.Write(_oamDmaPage);
        writer.Write(_oamDmaPending);
        writer.Write(_cpuOpenBus);
        _controllerOne.SaveState(writer);
        _controllerTwo.SaveState(writer);
        Scheduler.SaveState(writer);
        Apu.SaveState(writer);
        Ppu.SaveState(writer);
    }

    internal void LoadState(BinaryReader reader)
    {
        reader.ReadExactly(_ram);
        _oamDmaPage = reader.ReadByte();
        _oamDmaPending = reader.ReadBoolean();
        _cpuOpenBus = reader.ReadByte();
        _controllerOne.LoadState(reader);
        _controllerTwo.LoadState(reader);
        Scheduler.LoadState(reader);
        Apu.LoadState(reader);
        Ppu.LoadState(reader);
    }

    public void SetControllerState(int player, NesButton buttons)
    {
        if (player == 1)
        {
            _controllerOne.SetState(buttons);
        }
        else if (player == 2)
        {
            _controllerTwo.SetState(buttons);
        }
    }

    private byte ReadExternalForDma(ushort address)
    {
        // The APU/controller registers are internal to the 2A03 and do not
        // answer a DMA unit's external-bus read.
        return address is >= 0x4000 and <= 0x401F
            ? _cpuOpenBus
            : Read(address);
    }

}

internal sealed class NesController
{
    private byte _state;
    private byte _shiftRegister;
    private bool _strobe;

    public void SetState(NesButton buttons) => Volatile.Write(ref _state, (byte)buttons);

    public void WriteStrobe(byte value)
    {
        _strobe = (value & 1) != 0;
        if (_strobe)
        {
            _shiftRegister = Volatile.Read(ref _state);
        }
    }

    public byte Read(byte openBus)
    {
        if (_strobe)
        {
            _shiftRegister = Volatile.Read(ref _state);
        }

        // A standard NES controller only drives data bit zero. The upper
        // three pins float and retain their prior CPU-bus values; the middle
        // four pins are grounded on the NES-001 controller ports.
        var value = (byte)((openBus & 0xE0) | (_shiftRegister & 1));
        if (!_strobe)
        {
            _shiftRegister = (byte)((_shiftRegister >> 1) | 0x80);
        }

        return value;
    }

    internal void SaveState(BinaryWriter writer)
    {
        writer.Write(Volatile.Read(ref _state));
        writer.Write(_shiftRegister);
        writer.Write(_strobe);
    }

    internal void LoadState(BinaryReader reader)
    {
        Volatile.Write(ref _state, reader.ReadByte());
        _shiftRegister = reader.ReadByte();
        _strobe = reader.ReadBoolean();
    }
}
