using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace PixelDeck.Emulation.N64;

public sealed class N64Memory
{
    public const int MaximumVideoWidth = 640;
    public const int MaximumVideoHeight = 480;
    public const int RdramSize = 8 * 1024 * 1024;
    public const int SpMemorySize = 4 * 1024;

    /// <summary>
    /// Cartridge save RAM, mapped into PI domain 2 at 0x08000000. Titles such
    /// as Ocarina of Time probe it during boot and block on the DMA-complete
    /// interrupt, so the transfer must always signal completion even when the
    /// address falls outside anything the cartridge actually provides.
    /// </summary>
    public const int SramSize = 32 * 1024;
    public const int NtscCpuTicksPerField = 781_250;
    public const int PalCpuTicksPerField = 937_500;
    public const int CpuTicksPerSecond = 46_875_000;

    private const uint RdramMask = RdramSize - 1;
    private const uint CartRomBase = 0x10000000;
    private const uint SramBase = 0x08000000;
    private const uint AiStatusBusy = 0x40000000;
    private const uint AiStatusFull = 0x80000000;

    private readonly N64Cartridge _cartridge;
    private readonly N64ControllerState[] _controllers = new N64ControllerState[4];
    // Every port reports occupied by default. Reporting empty ports as absent is what hardware
    // does, and the machinery below implements it, but Super Mario 64 stalls shortly after
    // osContInit when ports 2-4 answer with the "no device" bit: its controller polling drops
    // roughly sevenfold and it never reaches gameplay. Until that interaction is understood the
    // default stays permissive, and SetControllerConnected is opt-in.
    private readonly bool[] _controllerConnected = [true, true, true, true];
    private readonly N64TlbEntry[] _tlb = new N64TlbEntry[32];
    private byte _tlbAsid;
    private uint _spMemoryAddress;
    private uint _spDramAddress;
    private uint _spStatus = 1;
    private uint _spSemaphore;
    private bool _rspStartRequested;
    private uint _dpcStart;
    private uint _dpcCurrent;
    private uint _dpcEnd;
    private uint _dpcStatus;
    private uint _siDramAddress;
    private uint _siStatus;
    private int _siDmaDirection;
    private int _siDmaTicksRemaining;
    private uint _piDramAddress;
    private uint _piCartAddress;
    private uint _viControl;
    private uint _viOrigin;
    private uint _viWidth = 320;
    private uint _viVerticalInterrupt = 0x3FF;
    private uint _viCurrent;
    private uint _viBurst;
    private uint _viVerticalSync;
    private uint _viHorizontalSync;
    private uint _viHorizontalSyncLeap;
    private uint _viHorizontalVideo;
    private uint _viVerticalVideo;
    private uint _viVerticalBurst;
    private uint _viXScale;
    private uint _viYScale;
    private long _viTicksInField;
    private long _viNextLineChangeTick;
    private uint _viField;
    private uint _viLastInterruptLine = uint.MaxValue;
    private uint _aiDramAddress;
    private uint _aiControl;
    private uint _aiDacRate;
    private uint _aiBitRate;
    private N64AiDma _aiCurrent;
    private N64AiDma _aiQueued;
    private readonly float[] _audioRing = new float[1 << 17];
    private readonly object _audioLock = new();
    private int _audioReadPosition;
    private int _audioWritePosition;
    private int _audioBufferedValues;

    public N64Memory(N64Cartridge cartridge)
    {
        _cartridge = cartridge;
        Eeprom = new byte[cartridge.SaveType == N64SaveType.Eeprom16Kbit
            ? 2 * 1024
            : 512];
        CpuTicksPerField = cartridge.VideoRegion == N64VideoRegion.Ntsc
            ? NtscCpuTicksPerField
            : PalCpuTicksPerField;
        Array.Copy(cartridge.Rom, 0x40, SpDmem, 0x40, 0xFC0);
    }

    public byte[] Rdram { get; } = new byte[RdramSize];

    public byte[] SpDmem { get; } = new byte[SpMemorySize];

    public byte[] SpImem { get; } = new byte[SpMemorySize];

    public byte[] PifRam { get; } = new byte[64];

    /// <summary>
    /// Cartridge EEPROM with the physical capacity declared by the game:
    /// 512 bytes for 4-Kbit devices or 2 KiB for 16-Kbit devices.
    /// </summary>
    public byte[] Eeprom { get; }

    public bool EepromDirty { get; private set; }

    public byte[] Sram { get; } = new byte[SramSize];

    public bool SramDirty { get; private set; }

    public void MarkSramFlushed() => SramDirty = false;

    public void LoadSram(ReadOnlySpan<byte> data)
    {
        Sram.AsSpan().Clear();
        data[..Math.Min(data.Length, Sram.Length)].CopyTo(Sram);
        SramDirty = false;
    }

    public uint MiInterrupt { get; private set; }

    public uint MiInterruptMask { get; private set; }

    public uint ViControl => _viControl;

    public uint ViOrigin => _viOrigin;

    public uint ViWidth => _viWidth;

    public uint ViCurrent => _viCurrent;

    public uint ViHorizontalVideo => _viHorizontalVideo;

    public uint ViVerticalVideo => _viVerticalVideo;

    public uint ViXScale => _viXScale;

    public uint ViYScale => _viYScale;

    /// <summary>
    /// The live output size derived from the video interface: VI_WIDTH is the
    /// frame-buffer stride and the visible height is the active scan window
    /// scaled by VI_Y_SCALE. Verified against four commercial cartridges —
    /// Super Mario 64, Quest 64, and Ocarina of Time program 320 wide while
    /// GoldenEye 007 programs 440.
    /// </summary>
    public int VideoWidth { get; private set; } = 320;

    public int VideoHeight { get; private set; } = 240;

    private void UpdateVideoResolution()
    {
        var verticalStart = (int)((_viVerticalVideo >> 16) & 0x3FF);
        var verticalEnd = (int)(_viVerticalVideo & 0x3FF);
        var verticalScale = (int)(_viYScale & 0xFFF);
        var width = (int)_viWidth;
        if (width <= 0 || verticalEnd <= verticalStart || verticalScale <= 0)
        {
            // The cartridge has not finished programming the video interface;
            // keep the last valid size rather than collapsing the image.
            return;
        }

        VideoWidth = Math.Clamp(width, 1, MaximumVideoWidth);
        VideoHeight = Math.Clamp(
            (verticalEnd - verticalStart) / 2 * verticalScale / 1024,
            1,
            MaximumVideoHeight);
    }

    public uint SpStatus => _spStatus;

    public int CpuTicksPerField { get; }

    public long VerticalInterruptsRaised { get; private set; }

    public long AudioDmasCompleted { get; private set; }

    public long DroppedAudioSampleCount { get; private set; }

    public long ControllerPolls { get; private set; }

    public uint LastControllerStateWord { get; private set; }

    public long NonNeutralControllerPolls { get; private set; }

    public long SiDmasStarted { get; private set; }

    public long SiDmasCompleted { get; private set; }

    public byte[] LastPifWrite { get; } = new byte[64];

    public long ControllerStatusQueries { get; private set; }

    public long ControllerReadCommands { get; private set; }

    public uint TranslateVirtualAddress(uint address)
    {
        if (address is >= 0x80000000 and <= 0xBFFFFFFF)
        {
            return address & 0x1FFFFFFF;
        }

        foreach (var entry in _tlb)
        {
            if (entry.TryTranslate(address, _tlbAsid, out var physicalAddress))
            {
                return physicalAddress;
            }
        }

        return address;
    }

    /// <summary>
    /// Translation as the CPU sees it: KSEG0/KSEG1 map directly, everything
    /// else requires a TLB entry. A miss reports Refill (vector 0x80000000)
    /// while a matched entry whose valid bit is clear reports Invalid
    /// (general vector) — demand-paging games such as GoldenEye rely on that
    /// distinction. Debug and DMA callers keep using the pass-through
    /// <see cref="TranslateVirtualAddress"/> instead.
    /// </summary>
    public N64TlbFault TranslateCpuAddress(uint address, out uint physicalAddress)
    {
        if (address is >= 0x80000000 and <= 0xBFFFFFFF)
        {
            physicalAddress = address & 0x1FFFFFFF;
            return N64TlbFault.None;
        }

        var matchedInvalid = false;
        foreach (var entry in _tlb)
        {
            switch (entry.Lookup(address, _tlbAsid, out physicalAddress))
            {
                case N64TlbLookup.Valid:
                    return N64TlbFault.None;
                case N64TlbLookup.Invalid:
                    matchedInvalid = true;
                    break;
            }
        }

        physicalAddress = 0;
        return matchedInvalid ? N64TlbFault.Invalid : N64TlbFault.Refill;
    }

    internal void WriteTlbEntry(
        int index,
        uint pageMask,
        uint entryHi,
        uint entryLo0,
        uint entryLo1)
    {
        _tlb[index & 31] = new N64TlbEntry(pageMask, entryHi, entryLo0, entryLo1);
    }

    internal N64TlbEntry ReadTlbEntry(int index) => _tlb[index & 31];

    internal int ProbeTlb(uint entryHi)
    {
        for (var index = 0; index < _tlb.Length; index++)
        {
            if (_tlb[index].Matches(entryHi))
            {
                return index;
            }
        }

        return -1;
    }

    internal void SetTlbAsid(byte asid) => _tlbAsid = asid;

    /// <summary>
    /// Resolves a physical address to the backing array of a directly mapped
    /// region when the whole <paramref name="length"/>-byte access fits inside
    /// it. This is the single definition of the direct memory map; accesses
    /// that straddle a region boundary return null and take the byte path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[]? TryGetDirectRegion(uint physicalAddress, uint length, bool writable, out int offset)
    {
        var end = (ulong)physicalAddress + length;
        if (end <= RdramSize)
        {
            offset = (int)physicalAddress;
            return Rdram;
        }

        if (physicalAddress >= 0x04000000 && end <= 0x04001000)
        {
            offset = (int)(physicalAddress & 0xFFF);
            return SpDmem;
        }

        if (physicalAddress >= 0x04001000 && end <= 0x04002000)
        {
            offset = (int)(physicalAddress & 0xFFF);
            return SpImem;
        }

        if (!writable && physicalAddress >= CartRomBase && end - CartRomBase <= (ulong)_cartridge.Rom.Length)
        {
            offset = (int)(physicalAddress - CartRomBase);
            return _cartridge.Rom;
        }

        if (physicalAddress >= 0x1FC007C0 && end <= 0x1FC00800)
        {
            offset = (int)(physicalAddress - 0x1FC007C0);
            return PifRam;
        }

        offset = 0;
        return null;
    }

    public byte ReadByte(uint virtualAddress) =>
        ReadBytePhysical(TranslateVirtualAddress(virtualAddress));

    public byte ReadBytePhysical(uint address)
    {
        var region = TryGetDirectRegion(address, 1, writable: false, out var offset);
        if (region is not null)
        {
            return region[offset];
        }

        return (byte)(ReadIoWord(address & ~3u) >> (int)((3 - (address & 3)) * 8));
    }

    public ushort ReadUInt16(uint address) =>
        ReadUInt16Physical(TranslateVirtualAddress(address));

    public ushort ReadUInt16Physical(uint physical)
    {
        var region = TryGetDirectRegion(physical, 2, writable: false, out var offset);
        if (region is not null)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(region.AsSpan(offset, 2));
        }

        return (ushort)((ReadBytePhysical(physical) << 8) | ReadBytePhysical(physical + 1));
    }

    public uint ReadUInt32(uint address) =>
        ReadUInt32Physical(TranslateVirtualAddress(address));

    public uint ReadUInt32Physical(uint physical)
    {
        var region = TryGetDirectRegion(physical, 4, writable: false, out var offset);
        if (region is not null)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(region.AsSpan(offset, 4));
        }

        return ((uint)ReadBytePhysical(physical) << 24) |
               ((uint)ReadBytePhysical(physical + 1) << 16) |
               ((uint)ReadBytePhysical(physical + 2) << 8) |
               ReadBytePhysical(physical + 3);
    }

    public ulong ReadUInt64(uint address) =>
        ReadUInt64Physical(TranslateVirtualAddress(address));

    public ulong ReadUInt64Physical(uint physical)
    {
        var region = TryGetDirectRegion(physical, 8, writable: false, out var offset);
        if (region is not null)
        {
            return BinaryPrimitives.ReadUInt64BigEndian(region.AsSpan(offset, 8));
        }

        return ((ulong)ReadUInt32Physical(physical) << 32) | ReadUInt32Physical(physical + 4);
    }

    public void WriteByte(uint virtualAddress, byte value) =>
        WriteBytePhysical(TranslateVirtualAddress(virtualAddress), value);

    public void WriteBytePhysical(uint address, byte value)
    {
        var region = TryGetDirectRegion(address, 1, writable: true, out var offset);
        if (region is not null)
        {
            region[offset] = value;
            return;
        }

        var alignedAddress = address & ~3u;
        var shift = (int)((3 - (address & 3)) * 8);
        var mask = 0xFFu << shift;
        WriteIoWord(alignedAddress, (ReadIoWord(alignedAddress) & ~mask) | ((uint)value << shift), mask);
    }

    public void WriteUInt16(uint address, ushort value) =>
        WriteUInt16Physical(TranslateVirtualAddress(address), value);

    public void WriteUInt16Physical(uint physical, ushort value)
    {
        var region = TryGetDirectRegion(physical, 2, writable: true, out var offset);
        if (region is not null)
        {
            BinaryPrimitives.WriteUInt16BigEndian(region.AsSpan(offset, 2), value);
            return;
        }

        WriteBytePhysical(physical, (byte)(value >> 8));
        WriteBytePhysical(physical + 1, (byte)value);
    }

    public void WriteUInt32(uint address, uint value) =>
        WriteUInt32Physical(TranslateVirtualAddress(address), value);

    public void WriteUInt32Physical(uint physical, uint value)
    {
        var region = TryGetDirectRegion(physical, 4, writable: true, out var offset);
        if (region is not null)
        {
            BinaryPrimitives.WriteUInt32BigEndian(region.AsSpan(offset, 4), value);
            return;
        }

        if (IsDirectMemory(physical))
        {
            // The access straddles a direct-region boundary; fall back to
            // byte writes against the same physical addresses.
            WriteBytePhysical(physical, (byte)(value >> 24));
            WriteBytePhysical(physical + 1, (byte)(value >> 16));
            WriteBytePhysical(physical + 2, (byte)(value >> 8));
            WriteBytePhysical(physical + 3, (byte)value);
            return;
        }

        WriteIoWord(physical, value, uint.MaxValue);
    }

    public void WriteUInt64(uint address, ulong value) =>
        WriteUInt64Physical(TranslateVirtualAddress(address), value);

    public void WriteUInt64Physical(uint physical, ulong value)
    {
        var region = TryGetDirectRegion(physical, 8, writable: true, out var offset);
        if (region is not null)
        {
            BinaryPrimitives.WriteUInt64BigEndian(region.AsSpan(offset, 8), value);
            return;
        }

        WriteUInt32Physical(physical, (uint)(value >> 32));
        WriteUInt32Physical(physical + 4, (uint)value);
    }

    public void AdvanceCpuTicks(int ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        AdvanceAudio(ticks);
        AdvanceSi(ticks);
        _viTicksInField += ticks;
        while (_viTicksInField >= CpuTicksPerField)
        {
            _viTicksInField -= CpuTicksPerField;
            _viField ^= (_viControl >> 6) & 1;
            _viLastInterruptLine = uint.MaxValue;
            _viNextLineChangeTick = 0;
        }

        // UpdateViCurrent only observes state at line granularity, so skip it
        // until the field ticks cross the next line boundary. The cache is
        // zeroed whenever anything that feeds the computation changes.
        if (_viTicksInField < _viNextLineChangeTick)
        {
            return;
        }

        UpdateViCurrent();
    }

    public bool CpuInterruptPending => (MiInterrupt & MiInterruptMask) != 0;

    public void SetControllerState(int port, N64ControllerState state)
    {
        if (port is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        _controllers[port - 1] = state;
    }

    public N64ControllerState GetControllerState(int port)
    {
        if (port is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        return _controllers[port - 1];
    }

    /// <summary>
    /// Marks a controller port as occupied. Ports reported as empty answer PIF probes with the
    /// "no device" error bit, which is how games decide whether a player can join.
    /// </summary>
    public void SetControllerConnected(int port, bool connected)
    {
        if (port is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        _controllerConnected[port - 1] = connected;
        if (!connected)
        {
            _controllers[port - 1] = N64ControllerState.Neutral;
        }
    }

    public bool IsControllerConnected(int port)
    {
        if (port is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        return _controllerConnected[port - 1];
    }

    public void LoadEeprom(ReadOnlySpan<byte> data)
    {
        Eeprom.AsSpan().Clear();
        data[..Math.Min(data.Length, Eeprom.Length)].CopyTo(Eeprom);
        EepromDirty = false;
    }

    public void MarkEepromFlushed() => EepromDirty = false;

    public bool TryBeginRspTask(out N64RspTask task)
    {
        if (!_rspStartRequested)
        {
            task = default;
            return false;
        }

        _rspStartRequested = false;
        task = new N64RspTask(
            ReadSpUInt32(0xFC0),
            ReadSpUInt32(0xFC4),
            ReadSpUInt32(0xFC8),
            ReadSpUInt32(0xFCC),
            ReadSpUInt32(0xFD0),
            ReadSpUInt32(0xFD4),
            ReadSpUInt32(0xFD8),
            ReadSpUInt32(0xFDC),
            ReadSpUInt32(0xFE0),
            ReadSpUInt32(0xFE4),
            ReadSpUInt32(0xFE8),
            ReadSpUInt32(0xFEC),
            ReadSpUInt32(0xFF0),
            ReadSpUInt32(0xFF4),
            ReadSpUInt32(0xFF8),
            ReadSpUInt32(0xFFC));
        return true;
    }

    public void CompleteRspTask()
    {
        const uint taskDoneSignal = 1u << 9;
        _spStatus |= 3 | taskDoneSignal;
        if ((_spStatus & 0x40) != 0)
        {
            MiInterrupt |= 1;
        }
    }

    public void CompleteDisplayProcessor() => MiInterrupt |= 1u << 5;

    internal void SaveState(BinaryWriter writer)
    {
        writer.Write(_piDramAddress);
        writer.Write(_piCartAddress);
        writer.Write(_viControl);
        writer.Write(_viOrigin);
        writer.Write(_viWidth);
        writer.Write(_viVerticalInterrupt);
        writer.Write(_viCurrent);
        writer.Write(_viBurst);
        writer.Write(_viVerticalSync);
        writer.Write(_viHorizontalSync);
        writer.Write(_viHorizontalSyncLeap);
        writer.Write(_viHorizontalVideo);
        writer.Write(_viVerticalVideo);
        writer.Write(_viVerticalBurst);
        writer.Write(_viXScale);
        writer.Write(_viYScale);
        writer.Write(_viTicksInField);
        writer.Write(_viField);
        writer.Write(_viLastInterruptLine);
        writer.Write(VerticalInterruptsRaised);
        writer.Write(_aiDramAddress);
        writer.Write(_aiControl);
        writer.Write(_aiDacRate);
        writer.Write(_aiBitRate);
        WriteAudioDma(writer, _aiCurrent);
        WriteAudioDma(writer, _aiQueued);
        writer.Write(AudioDmasCompleted);
        writer.Write(MiInterrupt);
        writer.Write(MiInterruptMask);
        writer.Write(_siDramAddress);
        writer.Write(_siStatus);
        writer.Write(_siDmaDirection);
        writer.Write(_siDmaTicksRemaining);
        writer.Write(ControllerPolls);
        writer.Write(LastControllerStateWord);
        writer.Write(_spMemoryAddress);
        writer.Write(_spDramAddress);
        writer.Write(_spStatus);
        writer.Write(_spSemaphore);
        writer.Write(_rspStartRequested);
        writer.Write(_dpcStart);
        writer.Write(_dpcCurrent);
        writer.Write(_dpcEnd);
        writer.Write(_dpcStatus);
        foreach (var entry in _tlb)
        {
            writer.Write(entry.PageMask);
            writer.Write(entry.EntryHi);
            writer.Write(entry.EntryLo0);
            writer.Write(entry.EntryLo1);
        }

        writer.Write(Rdram);
        writer.Write(SpDmem);
        writer.Write(SpImem);
        writer.Write(PifRam);
        writer.Write(Eeprom);
        writer.Write(EepromDirty);
        writer.Write(Sram);
        writer.Write(SramDirty);
        foreach (var controller in _controllers)
        {
            writer.Write((ushort)controller.Buttons);
            writer.Write(controller.StickX);
            writer.Write(controller.StickY);
        }
    }

    internal void LoadState(BinaryReader reader)
    {
        _piDramAddress = reader.ReadUInt32();
        _piCartAddress = reader.ReadUInt32();
        _viControl = reader.ReadUInt32();
        _viOrigin = reader.ReadUInt32();
        _viWidth = reader.ReadUInt32();
        _viVerticalInterrupt = reader.ReadUInt32();
        _viCurrent = reader.ReadUInt32();
        _viBurst = reader.ReadUInt32();
        _viVerticalSync = reader.ReadUInt32();
        _viHorizontalSync = reader.ReadUInt32();
        _viHorizontalSyncLeap = reader.ReadUInt32();
        _viHorizontalVideo = reader.ReadUInt32();
        _viVerticalVideo = reader.ReadUInt32();
        _viVerticalBurst = reader.ReadUInt32();
        _viXScale = reader.ReadUInt32();
        _viYScale = reader.ReadUInt32();
        _viTicksInField = reader.ReadInt64();
        _viField = reader.ReadUInt32();
        _viLastInterruptLine = reader.ReadUInt32();
        _viNextLineChangeTick = 0;
        VerticalInterruptsRaised = reader.ReadInt64();
        _aiDramAddress = reader.ReadUInt32();
        _aiControl = reader.ReadUInt32();
        _aiDacRate = reader.ReadUInt32();
        _aiBitRate = reader.ReadUInt32();
        _aiCurrent = ReadAudioDma(reader);
        _aiQueued = ReadAudioDma(reader);
        AudioDmasCompleted = reader.ReadInt64();
        MiInterrupt = reader.ReadUInt32();
        MiInterruptMask = reader.ReadUInt32();
        _siDramAddress = reader.ReadUInt32();
        _siStatus = reader.ReadUInt32();
        _siDmaDirection = reader.ReadInt32();
        _siDmaTicksRemaining = reader.ReadInt32();
        ControllerPolls = reader.ReadInt64();
        LastControllerStateWord = reader.ReadUInt32();
        _spMemoryAddress = reader.ReadUInt32();
        _spDramAddress = reader.ReadUInt32();
        _spStatus = reader.ReadUInt32();
        _spSemaphore = reader.ReadUInt32();
        _rspStartRequested = reader.ReadBoolean();
        _dpcStart = reader.ReadUInt32();
        _dpcCurrent = reader.ReadUInt32();
        _dpcEnd = reader.ReadUInt32();
        _dpcStatus = reader.ReadUInt32();
        for (var index = 0; index < _tlb.Length; index++)
        {
            _tlb[index] = new N64TlbEntry(
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadUInt32());
        }

        reader.ReadExactly(Rdram);
        reader.ReadExactly(SpDmem);
        reader.ReadExactly(SpImem);
        reader.ReadExactly(PifRam);
        reader.ReadExactly(Eeprom);
        EepromDirty = reader.ReadBoolean();
        reader.ReadExactly(Sram);
        SramDirty = reader.ReadBoolean();
        for (var index = 0; index < _controllers.Length; index++)
        {
            _controllers[index] = new N64ControllerState(
                (N64Button)reader.ReadUInt16(),
                reader.ReadSByte(),
                reader.ReadSByte());
        }

        UpdateVideoResolution();
    }

    private static bool IsDirectMemory(uint address) =>
        address < RdramSize ||
        address is >= 0x04000000 and < 0x04002000 ||
        address is >= 0x1FC007C0 and < 0x1FC00800;

    private uint ReadIoWord(uint address) => address switch
    {
        0x04300000 => 0x02020102,
        0x04300004 => 0x01010101,
        0x04300008 => MiInterrupt,
        0x0430000C => MiInterruptMask,
        0x04040000 => _spMemoryAddress,
        0x04040004 => _spDramAddress,
        0x04040008 => 0,
        0x0404000C => 0,
        0x04040010 => _spStatus,
        0x04040014 => 0,
        0x04040018 => 0,
        0x0404001C => ReadSpSemaphore(),
        0x04100000 => _dpcStart,
        0x04100004 => _dpcEnd,
        0x04100008 => _dpcCurrent,
        0x0410000C => _dpcStatus,
        0x04400000 => _viControl,
        0x04400004 => _viOrigin,
        0x04400008 => _viWidth,
        0x0440000C => _viVerticalInterrupt,
        0x04400010 => _viCurrent,
        0x04400014 => _viBurst,
        0x04400018 => _viVerticalSync,
        0x0440001C => _viHorizontalSync,
        0x04400020 => _viHorizontalSyncLeap,
        0x04400024 => _viHorizontalVideo,
        0x04400028 => _viVerticalVideo,
        0x0440002C => _viVerticalBurst,
        0x04400030 => _viXScale,
        0x04400034 => _viYScale,
        0x04500000 => _aiDramAddress,
        0x04500004 => GetAudioRemainingLength(),
        0x04500008 => _aiControl,
        0x0450000C => GetAudioStatus(),
        0x04500010 => _aiDacRate,
        0x04500014 => _aiBitRate,
        0x04600000 => _piDramAddress,
        0x04600004 => _piCartAddress,
        0x04600010 => 0,
        0x0470000C => 0x00000014,
        0x04800000 => _siDramAddress,
        0x04800018 => _siStatus,
        _ => 0
    };

    private void WriteIoWord(uint address, uint value, uint mask)
    {
        switch (address)
        {
            case 0x04300000:
                if ((value & (1u << 11)) != 0)
                {
                    MiInterrupt &= ~(1u << 5);
                }

                break;
            case 0x04040000:
                _spMemoryAddress = Combine(_spMemoryAddress, value, mask) & 0x1FFF;
                break;
            case 0x04040004:
                _spDramAddress = Combine(_spDramAddress, value, mask) & 0x00FFFFFF;
                break;
            case 0x04040008:
                TransferSpDma(value, fromRdram: true);
                break;
            case 0x0404000C:
                TransferSpDma(value, fromRdram: false);
                break;
            case 0x04040010:
                WriteSpStatus(value);
                break;
            case 0x0404001C:
                _spSemaphore = 0;
                break;
            case 0x04100000:
                _dpcStart = Combine(_dpcStart, value, mask) & 0x00FFFFF8;
                _dpcCurrent = _dpcStart;
                break;
            case 0x04100004:
                _dpcEnd = Combine(_dpcEnd, value, mask) & 0x00FFFFF8;
                _dpcCurrent = _dpcEnd;
                MiInterrupt |= 1u << 5;
                break;
            case 0x0410000C:
                WriteDpcStatus(value);
                break;
            case 0x04300008:
                MiInterrupt &= ~value;
                break;
            case 0x0430000C:
                ApplyMiInterruptMask(value);
                break;
            case 0x04400000:
                _viControl = Combine(_viControl, value, mask);
                break;
            case 0x04400004:
                _viOrigin = Combine(_viOrigin, value, mask) & 0x00FFFFFF;
                break;
            case 0x04400008:
                _viWidth = Combine(_viWidth, value, mask) & 0xFFF;
                UpdateVideoResolution();
                break;
            case 0x0440000C:
                _viVerticalInterrupt = Combine(_viVerticalInterrupt, value, mask) & 0x3FF;
                _viNextLineChangeTick = 0;
                break;
            case 0x04400010:
                MiInterrupt &= ~(1u << 3);
                break;
            case 0x04400014:
                _viBurst = Combine(_viBurst, value, mask) & 0x03FFFFFF;
                break;
            case 0x04400018:
                _viVerticalSync = Combine(_viVerticalSync, value, mask) & 0x3FF;
                UpdateViCurrent();
                break;
            case 0x0440001C:
                _viHorizontalSync = Combine(_viHorizontalSync, value, mask) & 0x0FFF;
                break;
            case 0x04400020:
                _viHorizontalSyncLeap = Combine(_viHorizontalSyncLeap, value, mask);
                break;
            case 0x04400024:
                _viHorizontalVideo = Combine(_viHorizontalVideo, value, mask) & 0x03FF03FF;
                break;
            case 0x04400028:
                _viVerticalVideo = Combine(_viVerticalVideo, value, mask) & 0x03FF03FF;
                UpdateVideoResolution();
                break;
            case 0x0440002C:
                _viVerticalBurst = Combine(_viVerticalBurst, value, mask) & 0x03FF03FF;
                break;
            case 0x04400030:
                _viXScale = Combine(_viXScale, value, mask) & 0x0FFF0FFF;
                break;
            case 0x04400034:
                _viYScale = Combine(_viYScale, value, mask) & 0x0FFF0FFF;
                UpdateVideoResolution();
                break;
            case 0x04500000:
                _aiDramAddress = Combine(_aiDramAddress, value, mask) & 0x00FFFFF8;
                break;
            case 0x04500004:
                QueueAudioDma(Combine(0, value, mask) & 0x0003FFF8);
                break;
            case 0x04500008:
                _aiControl = Combine(_aiControl, value, mask) & 1;
                break;
            case 0x0450000C:
                MiInterrupt &= ~(1u << 2);
                break;
            case 0x04500010:
                _aiDacRate = Combine(_aiDacRate, value, mask) & 0x3FFF;
                break;
            case 0x04500014:
                _aiBitRate = Combine(_aiBitRate, value, mask) & 0xF;
                break;
            case 0x04600000:
                _piDramAddress = Combine(_piDramAddress, value, mask) & 0x00FFFFFF;
                break;
            case 0x04600004:
                _piCartAddress = Combine(_piCartAddress, value, mask);
                break;
            case 0x04600008:
                CopyRdramToCartridge((value & 0x00FFFFFF) + 1);
                break;
            case 0x0460000C:
                CopyCartridgeToRdram((value & 0x00FFFFFF) + 1);
                break;
            case 0x04600010:
                if ((value & 2) != 0)
                {
                    MiInterrupt &= ~(1u << 4);
                }

                break;
            case 0x04800000:
                _siDramAddress = Combine(_siDramAddress, value, mask) & 0x00FFFFFF;
                break;
            case 0x04800004:
                StartSiDma(pifToRdram: true);
                break;
            case 0x04800010:
                StartSiDma(pifToRdram: false);
                break;
            case 0x04800018:
                MiInterrupt &= ~(1u << 1);
                _siStatus &= ~0x1000u;
                break;
        }
    }

    private void CopyCartridgeToRdram(uint length)
    {
        if (_piCartAddress >= SramBase && _piCartAddress < CartRomBase)
        {
            var saveOffset = _piCartAddress - SramBase;
            for (var index = 0u; index < length; index++)
            {
                var source = saveOffset + index;
                Rdram[(_piDramAddress + index) & RdramMask] =
                    source < SramSize ? Sram[source] : (byte)0;
            }
        }
        else if (_piCartAddress >= CartRomBase)
        {
            var sourceOffset = _piCartAddress - CartRomBase;
            for (var index = 0u; index < length; index++)
            {
                var source = sourceOffset + index;
                var destination = (_piDramAddress + index) & RdramMask;
                Rdram[destination] = source < _cartridge.Rom.Length
                    ? _cartridge.Rom[source]
                    : (byte)0xFF;
            }
        }

        // The completion interrupt is raised for every transfer, including
        // addresses this cartridge does not back. libultra blocks on the
        // DMA-complete message, so skipping it deadlocks the game.
        _piDramAddress = (_piDramAddress + length + 7) & ~7u;
        _piCartAddress += length + 1;
        MiInterrupt |= 1 << 4;
    }

    private void TransferSpDma(uint descriptor, bool fromRdram)
    {
        var length = (descriptor & 0xFFF) + 1;
        var count = ((descriptor >> 12) & 0xFF) + 1;
        var skip = (descriptor >> 20) & 0xFFF;
        for (var block = 0u; block < count; block++)
        {
            for (var index = 0u; index < length; index++)
            {
                var spAddress = (_spMemoryAddress + index) & 0x1FFF;
                var dramAddress = (_spDramAddress + index) & RdramMask;
                if (fromRdram)
                {
                    WriteSpByte(spAddress, Rdram[dramAddress]);
                }
                else
                {
                    Rdram[dramAddress] = ReadSpByte(spAddress);
                }
            }

            _spMemoryAddress = (_spMemoryAddress + length) & 0x1FFF;
            _spDramAddress = (_spDramAddress + length + skip) & 0x00FFFFFF;
        }
    }

    private void WriteSpStatus(uint value)
    {
        if ((value & (1u << 0)) != 0)
        {
            _spStatus &= ~1u;
            _rspStartRequested = true;
        }

        if ((value & (1u << 1)) != 0) _spStatus |= 1;
        if ((value & (1u << 2)) != 0) _spStatus &= ~2u;
        if ((value & (1u << 3)) != 0) MiInterrupt &= ~1u;
        if ((value & (1u << 4)) != 0) MiInterrupt |= 1;
        if ((value & (1u << 5)) != 0) _spStatus &= ~(1u << 5);
        if ((value & (1u << 6)) != 0) _spStatus |= 1u << 5;
        if ((value & (1u << 7)) != 0) _spStatus &= ~(1u << 6);
        if ((value & (1u << 8)) != 0) _spStatus |= 1u << 6;
        for (var signal = 0; signal < 8; signal++)
        {
            var clear = 1u << (9 + (signal * 2));
            var set = clear << 1;
            var statusBit = 1u << (7 + signal);
            if ((value & clear) != 0) _spStatus &= ~statusBit;
            if ((value & set) != 0) _spStatus |= statusBit;
        }
    }

    private void WriteDpcStatus(uint value)
    {
        if ((value & 1) != 0) _dpcStatus &= ~1u;
        if ((value & 2) != 0) _dpcStatus |= 1;
        if ((value & 4) != 0) _dpcStatus &= ~2u;
        if ((value & 8) != 0) _dpcStatus |= 2;
        if ((value & 0x10) != 0) _dpcStatus &= ~4u;
        if ((value & 0x20) != 0) _dpcStatus |= 4;
        if ((value & 0x40) != 0)
        {
            _dpcCurrent = _dpcStart;
        }
    }

    private uint ReadSpSemaphore()
    {
        var previous = _spSemaphore;
        _spSemaphore = 1;
        return previous;
    }

    private byte ReadSpByte(uint address) =>
        address < 0x1000 ? SpDmem[address] : SpImem[address & 0xFFF];

    private void WriteSpByte(uint address, byte value)
    {
        if (address < 0x1000) SpDmem[address] = value;
        else SpImem[address & 0xFFF] = value;
    }

    private uint ReadSpUInt32(int offset) =>
        ((uint)SpDmem[offset] << 24) |
        ((uint)SpDmem[offset + 1] << 16) |
        ((uint)SpDmem[offset + 2] << 8) |
        SpDmem[offset + 3];

    private void CopyRdramToCartridge(uint length)
    {
        if (_piCartAddress >= SramBase && _piCartAddress < CartRomBase)
        {
            var saveOffset = _piCartAddress - SramBase;
            for (var index = 0u; index < length; index++)
            {
                var destination = saveOffset + index;
                if (destination < SramSize)
                {
                    Sram[destination] = Rdram[(_piDramAddress + index) & RdramMask];
                }
            }

            SramDirty = true;
        }

        _piDramAddress = (_piDramAddress + length + 7) & ~7u;
        _piCartAddress += length + 1;
        MiInterrupt |= 1 << 4;
    }

    private void ApplyMiInterruptMask(uint value)
    {
        for (var interrupt = 0; interrupt < 6; interrupt++)
        {
            var clearBit = 1u << (interrupt * 2);
            var setBit = clearBit << 1;
            if ((value & clearBit) != 0)
            {
                MiInterruptMask &= ~(1u << interrupt);
            }

            if ((value & setBit) != 0)
            {
                MiInterruptMask |= 1u << interrupt;
            }
        }
    }

    private void CopyRdramToPifRam()
    {
        for (var index = 0; index < PifRam.Length; index++)
        {
            PifRam[index] = Rdram[(_siDramAddress + (uint)index) & RdramMask];
        }

        PifRam.CopyTo(LastPifWrite, 0);
        ProcessPifCommands();
        CompleteSiDma();
    }

    private void CopyPifRamToRdram()
    {
        ProcessPifCommands();
        for (var index = 0; index < PifRam.Length; index++)
        {
            Rdram[(_siDramAddress + (uint)index) & RdramMask] = PifRam[index];
        }

        CompleteSiDma();
    }

    private void CompleteSiDma()
    {
        _siStatus &= ~1u;
        _siStatus |= 0x1000;
        MiInterrupt |= 1u << 1;
        SiDmasCompleted++;
    }

    private void StartSiDma(bool pifToRdram)
    {
        if (_siDmaDirection != 0)
        {
            _siStatus |= 8;
            return;
        }

        _siDmaDirection = pifToRdram ? 1 : 2;
        _siDmaTicksRemaining = 256;
        _siStatus |= 1;
        SiDmasStarted++;
    }

    private void AdvanceSi(int ticks)
    {
        if (_siDmaDirection == 0)
        {
            return;
        }

        _siDmaTicksRemaining -= ticks;
        if (_siDmaTicksRemaining > 0)
        {
            return;
        }

        var direction = _siDmaDirection;
        _siDmaDirection = 0;
        _siDmaTicksRemaining = 0;
        if (direction == 1)
        {
            CopyPifRamToRdram();
        }
        else
        {
            CopyRdramToPifRam();
        }
    }

    private void ProcessPifCommands()
    {
        var offset = 0;
        var channel = 0;
        while (offset < 63 && channel <= 4)
        {
            var transmitDescriptor = PifRam[offset++];
            if (transmitDescriptor == 0xFE)
            {
                break;
            }

            if (transmitDescriptor == 0xFF)
            {
                continue;
            }

            if (transmitDescriptor == 0)
            {
                channel++;
                continue;
            }

            if ((transmitDescriptor & 0xC0) != 0 || offset >= 63)
            {
                continue;
            }

            var receiveDescriptorOffset = offset;
            var receiveDescriptor = PifRam[offset++];
            var transmitLength = transmitDescriptor & 0x3F;
            var receiveLength = receiveDescriptor & 0x3F;
            if (offset + transmitLength + receiveLength > 63)
            {
                PifRam[receiveDescriptorOffset] |= 0x40;
                break;
            }

            var commandOffset = offset;
            var responseOffset = offset + transmitLength;
            if (channel < 4)
            {
                ProcessControllerCommand(
                    channel,
                    commandOffset,
                    transmitLength,
                    responseOffset,
                    receiveLength,
                    receiveDescriptorOffset);
            }
            else
            {
                ProcessEepromCommand(
                    commandOffset,
                    transmitLength,
                    responseOffset,
                    receiveLength,
                    receiveDescriptorOffset);
            }

            offset += transmitLength + receiveLength;
            channel++;
        }

        PifRam[63] = 0;
    }

    private void ProcessControllerCommand(
        int channel,
        int commandOffset,
        int transmitLength,
        int responseOffset,
        int receiveLength,
        int receiveDescriptorOffset)
    {
        if (transmitLength == 0)
        {
            PifRam[receiveDescriptorOffset] |= 0x80;
            return;
        }

        // An empty port answers every probe with the "no device" bit rather than a phantom pad.
        if (!_controllerConnected[channel])
        {
            PifRam[receiveDescriptorOffset] |= 0x80;
            return;
        }

        switch (PifRam[commandOffset])
        {
            case 0x00:
            case 0xFF:
                ControllerStatusQueries++;
                if (receiveLength >= 3)
                {
                    PifRam[responseOffset] = 0x05;
                    PifRam[responseOffset + 1] = 0x00;
                    PifRam[responseOffset + 2] = 0x00;
                }

                break;
            case 0x01:
                ControllerReadCommands++;
                if (receiveLength >= 4)
                {
                    var state = _controllers[channel].ToPifWord();
                    if (channel == 0)
                    {
                        ControllerPolls++;
                        LastControllerStateWord = state;
                        if (state != 0)
                        {
                            NonNeutralControllerPolls++;
                        }
                    }

                    PifRam[responseOffset] = (byte)(state >> 24);
                    PifRam[responseOffset + 1] = (byte)(state >> 16);
                    PifRam[responseOffset + 2] = (byte)(state >> 8);
                    PifRam[responseOffset + 3] = (byte)state;
                }

                break;
            default:
                PifRam[receiveDescriptorOffset] |= 0x80;
                break;
        }
    }

    private void ProcessEepromCommand(
        int commandOffset,
        int transmitLength,
        int responseOffset,
        int receiveLength,
        int receiveDescriptorOffset)
    {
        if (transmitLength == 0)
        {
            PifRam[receiveDescriptorOffset] |= 0x80;
            return;
        }

        switch (PifRam[commandOffset])
        {
            case 0x00:
            case 0xFF:
                if (receiveLength >= 3)
                {
                    PifRam[responseOffset] = 0x00;
                    PifRam[responseOffset + 1] =
                        _cartridge.SaveType == N64SaveType.Eeprom16Kbit
                            ? (byte)0xC0
                            : (byte)0x80;
                    PifRam[responseOffset + 2] = 0x00;
                }

                break;
            case 0x04 when transmitLength >= 2 && receiveLength >= 8:
            {
                var block = GetEepromBlock(PifRam[commandOffset + 1]);
                Eeprom.AsSpan(block * 8, 8).CopyTo(PifRam.AsSpan(responseOffset, 8));
                break;
            }
            case 0x05 when transmitLength >= 10:
            {
                var block = GetEepromBlock(PifRam[commandOffset + 1]);
                PifRam.AsSpan(commandOffset + 2, 8).CopyTo(Eeprom.AsSpan(block * 8, 8));
                EepromDirty = true;
                if (receiveLength > 0)
                {
                    PifRam[responseOffset] = 0;
                }

                break;
            }
            default:
                PifRam[receiveDescriptorOffset] |= 0x80;
                break;
        }
    }

    private int GetEepromBlock(byte address) =>
        _cartridge.SaveType == N64SaveType.Eeprom16Kbit
            ? address
            : address & 0x3F;

    private static uint Combine(uint original, uint value, uint mask) =>
        (original & ~mask) | (value & mask);

    private void QueueAudioDma(uint length)
    {
        if (length == 0 || (_aiControl & 1) == 0)
        {
            return;
        }

        var dma = new N64AiDma(
            _aiDramAddress,
            length,
            CalculateAudioDmaTicks(length),
            CalculateAudioDmaTicks(length));
        if (!_aiCurrent.Active)
        {
            _aiCurrent = dma;
        }
        else if (!_aiQueued.Active)
        {
            _aiQueued = dma;
        }
        else
        {
            return;
        }

        CaptureAudioDmaSamples(dma.Address, dma.Length);
    }

    private void CaptureAudioDmaSamples(uint address, uint length)
    {
        var offset = (int)(address & RdramMask);
        var values = (int)Math.Min(length, (uint)(RdramSize - offset)) / 2;
        lock (_audioLock)
        {
            for (var index = 0; index < values; index++)
            {
                if (_audioBufferedValues == _audioRing.Length)
                {
                    DroppedAudioSampleCount += values - index;
                    return;
                }

                var sample = BinaryPrimitives.ReadInt16BigEndian(Rdram.AsSpan(offset + (index * 2), 2));
                _audioRing[_audioWritePosition] = sample * (1f / 32768f);
                _audioWritePosition = (_audioWritePosition + 1) & (_audioRing.Length - 1);
                _audioBufferedValues++;
            }
        }
    }

    /// <summary>
    /// Reads buffered stereo audio (interleaved left/right floats) captured
    /// from completed audio-interface DMAs. Safe to call from an audio thread.
    /// </summary>
    public int ReadAudioSamples(Span<float> destination)
    {
        lock (_audioLock)
        {
            var count = Math.Min(destination.Length, _audioBufferedValues);
            for (var index = 0; index < count; index++)
            {
                destination[index] = _audioRing[_audioReadPosition];
                _audioReadPosition = (_audioReadPosition + 1) & (_audioRing.Length - 1);
            }

            _audioBufferedValues -= count;
            return count;
        }
    }

    public void ClearAudioSamples()
    {
        lock (_audioLock)
        {
            _audioReadPosition = 0;
            _audioWritePosition = 0;
            _audioBufferedValues = 0;
        }
    }

    public int BufferedAudioSampleCount
    {
        get
        {
            lock (_audioLock)
            {
                return _audioBufferedValues;
            }
        }
    }

    private long CalculateAudioDmaTicks(uint length)
    {
        const int bytesPerStereoSample = 4;
        var videoClock = _cartridge.VideoRegion == N64VideoRegion.Ntsc
            ? 48_681_812L
            : 49_656_530L;
        var sampleRate = Math.Max(1L, videoClock / (_aiDacRate + 1L));
        var samples = Math.Max(1L, length / bytesPerStereoSample);
        return Math.Max(1L, ((samples * CpuTicksPerSecond) + sampleRate - 1) / sampleRate);
    }

    private uint GetAudioStatus() =>
        (_aiCurrent.Active ? AiStatusBusy : 0) |
        (_aiQueued.Active ? AiStatusFull : 0);

    private uint GetAudioRemainingLength()
    {
        if (!_aiCurrent.Active || _aiCurrent.TotalTicks <= 0)
        {
            return 0;
        }

        return (uint)Math.Clamp(
            (_aiCurrent.Length * _aiCurrent.RemainingTicks) / _aiCurrent.TotalTicks,
            0,
            _aiCurrent.Length) & ~7u;
    }

    private void AdvanceAudio(int ticks)
    {
        var remainingTicks = (long)ticks;
        while (_aiCurrent.Active && remainingTicks > 0)
        {
            if (remainingTicks < _aiCurrent.RemainingTicks)
            {
                _aiCurrent = _aiCurrent with
                {
                    RemainingTicks = _aiCurrent.RemainingTicks - remainingTicks
                };
                return;
            }

            remainingTicks -= _aiCurrent.RemainingTicks;
            _aiCurrent = _aiQueued;
            _aiQueued = default;
            AudioDmasCompleted++;
            MiInterrupt |= 1u << 2;
        }
    }

    private static void WriteAudioDma(BinaryWriter writer, N64AiDma dma)
    {
        writer.Write(dma.Address);
        writer.Write(dma.Length);
        writer.Write(dma.TotalTicks);
        writer.Write(dma.RemainingTicks);
    }

    private static N64AiDma ReadAudioDma(BinaryReader reader) =>
        new(
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadInt64(),
            reader.ReadInt64());

    private void UpdateViCurrent()
    {
        var linesPerField = (_viVerticalSync & 0x3FF) + 1;
        if (linesPerField <= 1)
        {
            linesPerField = _cartridge.VideoRegion == N64VideoRegion.Ntsc ? 525u : 625u;
        }

        var line = (uint)((_viTicksInField * linesPerField) / CpuTicksPerField);
        if (line >= linesPerField)
        {
            line = linesPerField - 1;
        }

        _viNextLineChangeTick = (((line + 1L) * CpuTicksPerField) + linesPerField - 1) / linesPerField;
        _viCurrent = (line & ~1u) | _viField;
        var interruptLine = _viVerticalInterrupt & 0x3FE;
        var currentLine = _viCurrent & 0x3FE;
        if (currentLine == interruptLine && _viLastInterruptLine != currentLine)
        {
            MiInterrupt |= 1u << 3;
            VerticalInterruptsRaised++;
            _viLastInterruptLine = currentLine;
        }
        else if (currentLine != _viLastInterruptLine)
        {
            _viLastInterruptLine = uint.MaxValue;
        }
    }
}

public readonly record struct N64RspTask(
    uint Type,
    uint Flags,
    uint BootMicrocodePointer,
    uint BootMicrocodeSize,
    uint MicrocodePointer,
    uint MicrocodeSize,
    uint MicrocodeDataPointer,
    uint MicrocodeDataSize,
    uint DramStackPointer,
    uint DramStackSize,
    uint OutputBufferPointer,
    uint OutputBufferSizePointer,
    uint DataPointer,
    uint DataSize,
    uint YieldDataPointer,
    uint YieldDataSize);

internal readonly record struct N64TlbEntry(
    uint PageMask,
    uint EntryHi,
    uint EntryLo0,
    uint EntryLo1)
{
    private int PageSize =>
        checked((int)(((PageMask & 0x01FFE000) >> 13) + 1) * 0x1000);

    private bool Global => (EntryLo0 & EntryLo1 & 1) != 0;

    public bool Matches(uint entryHi)
    {
        var pairSize = (uint)(PageSize * 2);
        var addressMask = ~(pairSize - 1);
        return (EntryHi & addressMask) == (entryHi & addressMask) &&
               (Global || (EntryHi & 0xFF) == (entryHi & 0xFF));
    }

    public bool TryTranslate(uint address, byte currentAsid, out uint physicalAddress) =>
        Lookup(address, currentAsid, out physicalAddress) == N64TlbLookup.Valid;

    public N64TlbLookup Lookup(uint address, byte currentAsid, out uint physicalAddress)
    {
        var pageSize = (uint)PageSize;
        var pairSize = pageSize * 2;
        var addressMask = ~(pairSize - 1);
        if ((EntryHi & addressMask) != (address & addressMask) ||
            (!Global && (EntryHi & 0xFF) != currentAsid))
        {
            physicalAddress = 0;
            return N64TlbLookup.NoMatch;
        }

        var offsetInPair = address & (pairSize - 1);
        var entryLo = offsetInPair < pageSize ? EntryLo0 : EntryLo1;
        if ((entryLo & 2) == 0)
        {
            physicalAddress = 0;
            return N64TlbLookup.Invalid;
        }

        var physicalPage = (entryLo & 0x3FFFFFC0) << 6;
        physicalAddress = physicalPage | (offsetInPair & (pageSize - 1));
        return N64TlbLookup.Valid;
    }
}

public enum N64TlbFault
{
    None,
    Refill,
    Invalid
}

internal enum N64TlbLookup
{
    NoMatch,
    Invalid,
    Valid
}

internal readonly record struct N64AiDma(
    uint Address,
    uint Length,
    long TotalTicks,
    long RemainingTicks)
{
    public bool Active => Length != 0 && RemainingTicks > 0;
}
