using System.Numerics;
using System.Runtime.CompilerServices;

namespace PixelDeck.Emulation.Nes;

internal sealed class NesPpu
{
    public const int Width = 256;
    public const int Height = 240;
    private const long OpenBusDecayPpuCycles = 5_000_000;
    private const long OamDecayPpuCycles = 9_000;

    private static readonly uint[] SystemPalette =
    [
        0xFF666666, 0xFF002A88, 0xFF1412A7, 0xFF3B00A4, 0xFF5C007E, 0xFF6E0040, 0xFF6C0700, 0xFF561D00,
        0xFF333500, 0xFF0B4800, 0xFF005200, 0xFF004F08, 0xFF00404D, 0xFF000000, 0xFF000000, 0xFF000000,
        0xFFADADAD, 0xFF155FD9, 0xFF4240FF, 0xFF7527FE, 0xFFA01ACC, 0xFFB71E7B, 0xFFB53120, 0xFF994E00,
        0xFF6B6D00, 0xFF388700, 0xFF0C9300, 0xFF008F32, 0xFF007C8D, 0xFF000000, 0xFF000000, 0xFF000000,
        0xFFFFFEFF, 0xFF64B0FF, 0xFF9290FF, 0xFFC676FF, 0xFFF36AFF, 0xFFFE6ECC, 0xFFFE8170, 0xFFEA9E22,
        0xFFBCBE00, 0xFF88D800, 0xFF5CE430, 0xFF45E082, 0xFF48CDDE, 0xFF4F4F4F, 0xFF000000, 0xFF000000,
        0xFFFFFEFF, 0xFFC0DFFF, 0xFFD3D2FF, 0xFFE8C8FF, 0xFFFBC2FF, 0xFFFEC4EA, 0xFFFECCC5, 0xFFF7D8A5,
        0xFFE4E594, 0xFFCFEF96, 0xFFBDF4AB, 0xFFB3F3CC, 0xFFB5EBF2, 0xFFB8B8B8, 0xFF000000, 0xFF000000
    ];

    private readonly Cartridge _cartridge;
    private readonly byte[] _nametableRam;
    private readonly byte[] _paletteRam = new byte[32];
    private readonly byte[] _oam = new byte[256];
    private readonly byte[] _secondaryOam = new byte[32];
    private readonly int[] _secondaryOamSpriteIndices = new int[8];
    private readonly byte[] _secondaryOamSpriteZero = new byte[8];
    private readonly int[] _selectedSprites = new int[64];
    private readonly int[] _evaluatedSprites = new int[64];
    private readonly byte[] _selectedSpriteY = new byte[64];
    private readonly byte[] _selectedSpriteTile = new byte[64];
    private readonly byte[] _selectedSpriteX = new byte[64];
    private readonly byte[] _selectedSpriteZero = new byte[64];
    private readonly byte[] _spritePatternLow = new byte[64];
    private readonly byte[] _spritePatternHigh = new byte[64];
    private readonly byte[] _spriteXCounter = new byte[64];
    private readonly byte[] _spriteAttributes = new byte[64];
    private readonly ulong[] _enhancedSpriteScanlineMask = new ulong[Height];
    private readonly long[] _oamRowLastRefresh = new long[32];
    private readonly bool _removeSpriteLimit;
    private readonly bool _enableOamDecay;
    private readonly NesPpuRevision _ppuRevision;
    private readonly NesOamCorruptionMode _oamCorruptionMode;

    private byte _control;
    private byte _mask;
    private byte _pendingMask;
    private bool _hasPendingMask;
    private byte _status;
    private byte _oamAddress;
    private byte _readBuffer;
    private byte _openBus;
    private readonly long[] _openBusBitExpiry = new long[8];
    private long _openBusClock;
    private ushort _vramAddress;
    private ushort _temporaryAddress;
    private byte _fineX;
    private bool _writeToggle;
    private bool _oddFrame;
    private bool _suppressVblank;
    private int _nmiPpuDelay;
    private int _nmiInstructionDelay;
    private bool _nmiVblankEdgePending;
    private ushort _backgroundPatternShiftLow;
    private ushort _backgroundPatternShiftHigh;
    private ushort _backgroundAttributeShiftLow;
    private ushort _backgroundAttributeShiftHigh;
    private byte _nextBackgroundTile;
    private byte _nextBackgroundAttribute;
    private byte _nextBackgroundPatternLow;
    private byte _nextBackgroundPatternHigh;
    private ushort _ppuBusAddress;
    private int _selectedSpriteCount;
    private int _evaluatedSpriteCount;
    private int _secondarySpriteCount;
    private int _spriteEvaluationTargetScanline;
    private byte _oamCopyBuffer;
    private byte _secondaryOamAddress;
    private byte _spriteAddressHigh;
    private byte _spriteAddressLow;
    private byte _overflowBugCounter;
    private bool _spriteInRange;
    private bool _spriteZeroAdded;
    private bool _oamCopyDone;
    private bool _enhancedSpriteCacheDirty = true;

    public NesPpu(Cartridge cartridge, NesEmulationOptions? options = null)
    {
        options ??= new NesEmulationOptions();
        _cartridge = cartridge;
        _removeSpriteLimit = options.RemoveSpriteLimit;
        _enableOamDecay = options.EnableOamDecay;
        _ppuRevision = options.PpuRevision;
        _oamCorruptionMode = options.OamCorruptionMode;
        _nametableRam = new byte[cartridge.Mirroring == NametableMirroring.FourScreen ? 4_096 : 2_048];
    }

    public uint[] FrameBuffer { get; } = new uint[Width * Height];

    public int Scanline { get; private set; } = 261;

    public int Cycle { get; private set; }

    public bool FrameReady { get; private set; }

    public bool NmiRequested { get; private set; }

    private bool RenderingEnabled => (_mask & 0x18) != 0;

    private bool RenderingOamBusActive =>
        RenderingEnabled && (Scanline is >= 0 and < 240 || Scanline == 261);

    public void BeginFrame() => FrameReady = false;

    public bool ConsumeNmi()
    {
        if (!NmiRequested)
        {
            return false;
        }

        if (_nmiInstructionDelay > 0)
        {
            _nmiInstructionDelay--;
            return false;
        }

        if (_nmiVblankEdgePending)
        {
            _nmiVblankEdgePending = false;
            if (Scanline == 241 && Cycle <= 6)
            {
                return false;
            }
        }

        var requested = NmiRequested;
        NmiRequested = false;
        return requested;
    }

    public void AcknowledgeNmi()
    {
        NmiRequested = false;
        _nmiInstructionDelay = 0;
        _nmiVblankEdgePending = false;
    }

    public void Tick()
    {
        var executedCycle = Cycle;
        _openBusClock++;
        if (!RenderingOamBusActive)
        {
            // During forced blank and vblank, OAM1 remains selected on every
            // PPU dot. That continuously refreshes the selected eight-byte
            // DRAM row while all other rows remain free to decay.
            AccessOamRow(_oamAddress >> 3);
        }

        ClockPpuBusAddress();

        if (_nmiPpuDelay > 0 && --_nmiPpuDelay == 0 &&
            (_status & 0x80) != 0 && (_control & 0x80) != 0)
        {
            NmiRequested = true;
            _nmiInstructionDelay = 0;
            _nmiVblankEdgePending = true;
        }

        if (Scanline is >= 0 and < 240)
        {
            if (RenderingEnabled && Cycle is >= 2 and <= 257)
            {
                ShiftBackgroundRegisters();
            }

            if (Cycle is >= 1 and <= 256)
            {
                RenderPixel(Scanline, Cycle - 1);
                if (RenderingEnabled)
                {
                    AdvanceSpriteRegisters();
                }
            }

            if (RenderingEnabled)
            {
                ClockBackgroundPipeline();
                ClockSpritePipeline(Scanline + 1, evaluateSprites: true);
                if (Cycle == 257)
                {
                    CopyHorizontalPosition();
                }
            }
        }
        else if (Scanline == 241 && Cycle == 1)
        {
            if (_suppressVblank)
            {
                _suppressVblank = false;
                _status &= 0x7F;
                NmiRequested = false;
                _nmiPpuDelay = 0;
                _nmiInstructionDelay = 0;
                _nmiVblankEdgePending = false;
            }
            else
            {
                _status |= 0x80;
                if ((_control & 0x80) != 0)
                {
                    _nmiPpuDelay = 2;
                }
            }
        }
        else if (Scanline == 261)
        {
            if (Cycle == 1)
            {
                _status &= 0x1F;
            }

            if (RenderingEnabled)
            {
                ApplyPreRenderOamAddressBug();

                if (Cycle is >= 322 and <= 337)
                {
                    ShiftBackgroundRegisters();
                }

                ClockBackgroundPipeline();
                ClockSpritePipeline(0, evaluateSprites: false);
                if (Cycle == 257)
                {
                    CopyHorizontalPosition();
                }

                if (Cycle is >= 280 and <= 304)
                {
                    CopyVerticalPosition();
                }
            }
        }

        Cycle++;
        var skipOddFrameClock = Scanline == 261 && Cycle == 340 && _oddFrame && RenderingEnabled;
        if (_hasPendingMask)
        {
            var renderingWasEnabled = RenderingEnabled;
            _mask = _pendingMask;
            _hasPendingMask = false;
            HandleRenderingStateChange(renderingWasEnabled, RenderingEnabled, executedCycle);
        }

        if (skipOddFrameClock)
        {
            Cycle = 0;
            Scanline = 0;
            _oddFrame = false;
            return;
        }

        if (Cycle <= 340)
        {
            return;
        }

        Cycle = 0;
        Scanline++;
        if (Scanline == 240)
        {
            FrameReady = true;
        }

        if (Scanline > 261)
        {
            Scanline = 0;
            _oddFrame = !_oddFrame;
        }
    }

    public byte CpuReadRegister(ushort register)
    {
        return register switch
        {
            2 => ReadStatus(),
            4 => ReadOamData(),
            7 => ReadPpuData(),
            _ => GetOpenBus()
        };
    }

    public void CpuWriteRegister(ushort register, byte value, byte cpuOpenBus = 0)
    {
        DriveOpenBus(value, 0xFF);

        switch (register)
        {
            case 0:
                var nmiWasEnabled = (_control & 0x80) != 0;
                if ((_control & 0x20) != (value & 0x20))
                {
                    _enhancedSpriteCacheDirty = true;
                }

                _control = value;
                _temporaryAddress = (ushort)((_temporaryAddress & 0xF3FF) | ((value & 0x03) << 10));
                if (!nmiWasEnabled &&
                    (value & 0x80) != 0 &&
                    (_status & 0x80) != 0 &&
                    !(Scanline == 261 && Cycle == 1))
                {
                    NmiRequested = true;
                    _nmiInstructionDelay = 1;
                    _nmiVblankEdgePending = false;
                }
                else if (nmiWasEnabled && (value & 0x80) == 0)
                {
                    if (CanSuppressPendingVblankNmi)
                    {
                        NmiRequested = false;
                        _nmiPpuDelay = 0;
                        _nmiInstructionDelay = 0;
                        _nmiVblankEdgePending = false;
                    }
                }
                break;
            case 1:
                // Rendering-enable changes reach the PPU pipeline one dot after
                // the CPU write. Keeping the old mask through the next Tick is
                // also required for the pre-render odd-frame skip boundary.
                _pendingMask = value;
                _hasPendingMask = true;
                break;
            case 3:
                HandleOamAddressWrite(value, cpuOpenBus);
                _oamAddress = value;
                break;
            case 4:
                WriteOamData(value);
                break;
            case 5:
                WriteScroll(value);
                break;
            case 6:
                WriteAddress(value);
                break;
            case 7:
                WriteMemory(_vramAddress, value);
                IncrementVramAddress();
                break;
        }
    }

    public void WriteOamDma(ReadOnlySpan<byte> page)
    {
        for (var index = 0; index < 256; index++)
        {
            WriteOamDmaByte(page[index]);
        }
    }

    public void WriteOamDmaByte(byte value)
    {
        WriteOamData(value);
    }

    internal void SaveState(BinaryWriter writer)
    {
        writer.Write(_nametableRam);
        writer.Write(_paletteRam);
        writer.Write(_oam);
        writer.Write(_secondaryOam);
        writer.Write(_secondaryOamSpriteIndices.Length);
        foreach (var sprite in _secondaryOamSpriteIndices) writer.Write(sprite);
        writer.Write(_secondaryOamSpriteZero);
        writer.Write(_selectedSprites.Length);
        foreach (var sprite in _selectedSprites) writer.Write(sprite);
        writer.Write(_evaluatedSprites.Length);
        foreach (var sprite in _evaluatedSprites) writer.Write(sprite);
        writer.Write(_selectedSpriteY);
        writer.Write(_selectedSpriteTile);
        writer.Write(_selectedSpriteX);
        writer.Write(_selectedSpriteZero);
        writer.Write(_spritePatternLow);
        writer.Write(_spritePatternHigh);
        writer.Write(_spriteXCounter);
        writer.Write(_spriteAttributes);
        writer.Write((int)_ppuRevision);
        writer.Write(_enableOamDecay);
        writer.Write((int)_oamCorruptionMode);
        writer.Write(_oamRowLastRefresh.Length);
        foreach (var refresh in _oamRowLastRefresh) writer.Write(refresh);
        writer.Write(FrameBuffer.Length);
        foreach (var pixel in FrameBuffer) writer.Write(pixel);
        writer.Write(_control);
        writer.Write(_mask);
        writer.Write(_pendingMask);
        writer.Write(_hasPendingMask);
        writer.Write(_status);
        writer.Write(_oamAddress);
        writer.Write(_readBuffer);
        writer.Write(_openBus);
        writer.Write(_openBusClock);
        foreach (var expiry in _openBusBitExpiry) writer.Write(expiry);
        writer.Write(_vramAddress);
        writer.Write(_temporaryAddress);
        writer.Write(_fineX);
        writer.Write(_writeToggle);
        writer.Write(_oddFrame);
        writer.Write(_suppressVblank);
        writer.Write(_nmiPpuDelay);
        writer.Write(_nmiInstructionDelay);
        writer.Write(_nmiVblankEdgePending);
        writer.Write(_backgroundPatternShiftLow);
        writer.Write(_backgroundPatternShiftHigh);
        writer.Write(_backgroundAttributeShiftLow);
        writer.Write(_backgroundAttributeShiftHigh);
        writer.Write(_nextBackgroundTile);
        writer.Write(_nextBackgroundAttribute);
        writer.Write(_nextBackgroundPatternLow);
        writer.Write(_nextBackgroundPatternHigh);
        writer.Write(_ppuBusAddress);
        writer.Write(_selectedSpriteCount);
        writer.Write(_evaluatedSpriteCount);
        writer.Write(_secondarySpriteCount);
        writer.Write(_spriteEvaluationTargetScanline);
        writer.Write(_oamCopyBuffer);
        writer.Write(_secondaryOamAddress);
        writer.Write(_spriteAddressHigh);
        writer.Write(_spriteAddressLow);
        writer.Write(_overflowBugCounter);
        writer.Write(_spriteInRange);
        writer.Write(_spriteZeroAdded);
        writer.Write(_oamCopyDone);
        writer.Write(Scanline);
        writer.Write(Cycle);
        writer.Write(FrameReady);
        writer.Write(NmiRequested);
    }

    internal void LoadState(BinaryReader reader)
    {
        reader.BaseStream.ReadExactly(_nametableRam);
        reader.BaseStream.ReadExactly(_paletteRam);
        reader.BaseStream.ReadExactly(_oam);
        reader.BaseStream.ReadExactly(_secondaryOam);
        ReadInt32Array(reader, _secondaryOamSpriteIndices);
        reader.BaseStream.ReadExactly(_secondaryOamSpriteZero);
        ReadInt32Array(reader, _selectedSprites);
        ReadInt32Array(reader, _evaluatedSprites);
        reader.BaseStream.ReadExactly(_selectedSpriteY);
        reader.BaseStream.ReadExactly(_selectedSpriteTile);
        reader.BaseStream.ReadExactly(_selectedSpriteX);
        reader.BaseStream.ReadExactly(_selectedSpriteZero);
        reader.BaseStream.ReadExactly(_spritePatternLow);
        reader.BaseStream.ReadExactly(_spritePatternHigh);
        reader.BaseStream.ReadExactly(_spriteXCounter);
        reader.BaseStream.ReadExactly(_spriteAttributes);
        if ((NesPpuRevision)reader.ReadInt32() != _ppuRevision ||
            reader.ReadBoolean() != _enableOamDecay ||
            (NesOamCorruptionMode)reader.ReadInt32() != _oamCorruptionMode)
        {
            throw new InvalidDataException(
                "The save state was created with different NES PPU accuracy settings.");
        }

        EnsureArrayLength(reader, _oamRowLastRefresh.Length);
        for (var index = 0; index < _oamRowLastRefresh.Length; index++)
        {
            _oamRowLastRefresh[index] = reader.ReadInt64();
        }

        ReadUInt32Array(reader, FrameBuffer);
        _control = reader.ReadByte();
        _mask = reader.ReadByte();
        _pendingMask = reader.ReadByte();
        _hasPendingMask = reader.ReadBoolean();
        _status = reader.ReadByte();
        _oamAddress = reader.ReadByte();
        _readBuffer = reader.ReadByte();
        _openBus = reader.ReadByte();
        _openBusClock = reader.ReadInt64();
        for (var index = 0; index < _openBusBitExpiry.Length; index++)
        {
            _openBusBitExpiry[index] = reader.ReadInt64();
        }
        _vramAddress = reader.ReadUInt16();
        _temporaryAddress = reader.ReadUInt16();
        _fineX = reader.ReadByte();
        _writeToggle = reader.ReadBoolean();
        _oddFrame = reader.ReadBoolean();
        _suppressVblank = reader.ReadBoolean();
        _nmiPpuDelay = reader.ReadInt32();
        _nmiInstructionDelay = reader.ReadInt32();
        _nmiVblankEdgePending = reader.ReadBoolean();
        _backgroundPatternShiftLow = reader.ReadUInt16();
        _backgroundPatternShiftHigh = reader.ReadUInt16();
        _backgroundAttributeShiftLow = reader.ReadUInt16();
        _backgroundAttributeShiftHigh = reader.ReadUInt16();
        _nextBackgroundTile = reader.ReadByte();
        _nextBackgroundAttribute = reader.ReadByte();
        _nextBackgroundPatternLow = reader.ReadByte();
        _nextBackgroundPatternHigh = reader.ReadByte();
        _ppuBusAddress = reader.ReadUInt16();
        _selectedSpriteCount = reader.ReadInt32();
        _evaluatedSpriteCount = reader.ReadInt32();
        _secondarySpriteCount = reader.ReadInt32();
        _spriteEvaluationTargetScanline = reader.ReadInt32();
        _oamCopyBuffer = reader.ReadByte();
        _secondaryOamAddress = reader.ReadByte();
        _spriteAddressHigh = reader.ReadByte();
        _spriteAddressLow = reader.ReadByte();
        _overflowBugCounter = reader.ReadByte();
        _spriteInRange = reader.ReadBoolean();
        _spriteZeroAdded = reader.ReadBoolean();
        _oamCopyDone = reader.ReadBoolean();
        Scanline = reader.ReadInt32();
        Cycle = reader.ReadInt32();
        FrameReady = reader.ReadBoolean();
        NmiRequested = reader.ReadBoolean();
        _enhancedSpriteCacheDirty = true;
    }

    private static void ReadUInt32Array(BinaryReader reader, uint[] values)
    {
        EnsureArrayLength(reader, values.Length);
        for (var index = 0; index < values.Length; index++) values[index] = reader.ReadUInt32();
    }

    private static void ReadInt32Array(BinaryReader reader, int[] values)
    {
        EnsureArrayLength(reader, values.Length);
        for (var index = 0; index < values.Length; index++) values[index] = reader.ReadInt32();
    }

    private static void EnsureArrayLength(BinaryReader reader, int expectedLength)
    {
        if (reader.ReadInt32() != expectedLength)
        {
            throw new InvalidDataException("The save state contains an incompatible video buffer.");
        }
    }

    private byte ReadStatus()
    {
        if (Scanline == 241 && Cycle == 1)
        {
            _suppressVblank = true;
        }

        var value = (byte)((_status & 0xE0) | (GetOpenBus() & 0x1F));
        _status &= 0x7F;
        if (CanSuppressPendingVblankNmi)
        {
            NmiRequested = false;
            _nmiPpuDelay = 0;
            _nmiInstructionDelay = 0;
            _nmiVblankEdgePending = false;
        }

        _writeToggle = false;
        DriveOpenBus(value, 0xE0);
        return value;
    }

    private bool CanSuppressPendingVblankNmi =>
        (NmiRequested || _nmiPpuDelay > 0) && Scanline == 241 && Cycle <= 3;

    private byte ReadOamData()
    {
        byte value;
        if (RenderingOamBusActive)
        {
            if (Cycle is >= 257 and <= 320)
            {
                UpdateSpriteFetchOamLatch();
            }

            value = _oamCopyBuffer;
        }
        else
        {
            value = ReadPrimaryOam(_oamAddress);
        }

        DriveOpenBus(value, 0xFF);
        return value;
    }

    private void WriteOamData(byte value)
    {
        if (RenderingOamBusActive)
        {
            // OAM's write enable is suppressed while rendering. The address
            // generator still advances, but only its six sprite-index bits
            // move, which is observable as OAMADDR += 4.
            _oamAddress = (byte)(_oamAddress + 4);
            return;
        }

        WritePrimaryOam(_oamAddress++, value);
    }

    private void HandleRenderingStateChange(
        bool renderingWasEnabled,
        bool renderingIsEnabled,
        int executedCycle)
    {
        if (renderingWasEnabled == renderingIsEnabled ||
            !(Scanline is >= 0 and < 240 || Scanline == 261))
        {
            return;
        }

        if (_oamCorruptionMode == NesOamCorruptionMode.WorstCase &&
            (executedCycle >= 257 || (executedCycle & 1) == 0))
        {
            if (renderingIsEnabled)
            {
                // Turning rendering on switches from OAM1ADDR to OAM2ADDR
                // during the access half of the dot.
                CorruptOamRow(_oamAddress >> 3, _secondaryOamAddress);
            }
            else
            {
                // Turning rendering off performs the inverse asynchronous
                // switch, so the currently selected OAM2 row drives OAM1.
                CorruptOamRow(_secondaryOamAddress, _oamAddress >> 3);
            }
        }

        if (!renderingIsEnabled && executedCycle is >= 65 and <= 256)
        {
            // Disabling rendering during evaluation increments the current
            // primary-OAM address once and leaves the n/m counters misaligned
            // if rendering is enabled again on the same scanline.
            _oamAddress++;
            _spriteAddressHigh = (byte)((_oamAddress >> 2) & 0x3F);
            _spriteAddressLow = (byte)(_oamAddress & 0x03);
        }
    }

    private void HandleOamAddressWrite(byte value, byte cpuOpenBus)
    {
        var inVblank = Scanline is >= 240 and < 261;
        var oam1IsSelected =
            !RenderingEnabled ||
            inVblank ||
            (Cycle < 257 && (Cycle & 1) != 0);
        if (_oamCorruptionMode != NesOamCorruptionMode.WorstCase || !oam1IsSelected)
        {
            return;
        }

        // $2003 is not synchronized to the OAM precharge clock. In a
        // collision-prone CPU/PPU alignment, its early-write value first
        // selects the CPU open-bus row, followed by the intended row.
        CorruptOamRow(_oamAddress >> 3, cpuOpenBus >> 3);
        CorruptOamRow(cpuOpenBus >> 3, value >> 3);
    }

    private void ApplyPreRenderOamAddressBug()
    {
        if (_ppuRevision != NesPpuRevision.Rp2C02G ||
            Cycle is < 1 or > 8 ||
            _oamAddress < 8)
        {
            return;
        }

        var byteInRow = Cycle - 1;
        var sourceRow = (_oamAddress & 0xF8) >> 3;
        var sourceAddress = (sourceRow << 3) + byteInRow;
        WritePrimaryOam((byte)byteInRow, ReadPrimaryOam((byte)sourceAddress));
        if (Cycle == 1)
        {
            _secondaryOam[0] = _secondaryOam[sourceRow];
        }
    }

    private void CorruptOamRow(int sourceRow, int destinationRow)
    {
        sourceRow &= 0x1F;
        destinationRow &= 0x1F;
        if (sourceRow == destinationRow)
        {
            return;
        }

        AccessOamRow(sourceRow);
        AccessOamRow(destinationRow);
        Array.Copy(_oam, sourceRow << 3, _oam, destinationRow << 3, 8);
        _secondaryOam[destinationRow] = _secondaryOam[sourceRow];
        _enhancedSpriteCacheDirty = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadPrimaryOam(byte address)
    {
        AccessOamRow(address >> 3);
        return _oam[address];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WritePrimaryOam(byte address, byte value)
    {
        AccessOamRow(address >> 3);
        _oam[address] = MaskOamAttributeByte(address, value);
        _enhancedSpriteCacheDirty = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte MaskOamAttributeByte(int address, byte value) =>
        (address & 0x03) == 0x02 ? (byte)(value & 0xE3) : value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AccessOamRow(int row)
    {
        if (!_enableOamDecay)
        {
            return;
        }

        row &= 0x1F;
        if (_openBusClock - _oamRowLastRefresh[row] > OamDecayPpuCycles)
        {
            var rowAddress = row << 3;
            for (var index = 0; index < 8; index++)
            {
                var address = rowAddress | index;
                _oam[address] = MaskOamAttributeByte(address, (byte)address);
            }

            _enhancedSpriteCacheDirty = true;
        }

        _oamRowLastRefresh[row] = _openBusClock;
    }

    private byte ReadPpuData()
    {
        var address = (ushort)(_vramAddress & 0x3FFF);
        DrivePpuBus(address);
        var value = ReadMemory(address);
        byte result;

        if (address < 0x3F00)
        {
            result = _readBuffer;
            _readBuffer = value;
            DriveOpenBus(result, 0xFF);
        }
        else
        {
            result = (byte)((value & 0x3F) | (GetOpenBus() & 0xC0));
            _readBuffer = ReadMemory((ushort)(address - 0x1000));
            DriveOpenBus(result, 0x3F);
        }

        IncrementVramAddress();
        return result;
    }

    private byte GetOpenBus()
    {
        for (var bit = 0; bit < _openBusBitExpiry.Length; bit++)
        {
            var mask = 1 << bit;
            if ((_openBus & mask) != 0 &&
                _openBusBitExpiry[bit] != 0 &&
                _openBusClock >= _openBusBitExpiry[bit])
            {
                _openBus &= (byte)~mask;
                _openBusBitExpiry[bit] = 0;
            }
        }

        return _openBus;
    }

    private void DriveOpenBus(byte value, byte drivenMask)
    {
        _openBus = (byte)((_openBus & ~drivenMask) | (value & drivenMask));
        for (var bit = 0; bit < _openBusBitExpiry.Length; bit++)
        {
            var mask = 1 << bit;
            if ((drivenMask & mask) == 0)
            {
                continue;
            }

            _openBusBitExpiry[bit] = (value & mask) == 0
                ? 0
                : _openBusClock + OpenBusDecayPpuCycles;
        }
    }

    private void WriteScroll(byte value)
    {
        if (!_writeToggle)
        {
            _temporaryAddress = (ushort)((_temporaryAddress & 0xFFE0) | (value >> 3));
            _fineX = (byte)(value & 0x07);
        }
        else
        {
            _temporaryAddress = (ushort)((_temporaryAddress & 0x8FFF) | ((value & 0x07) << 12));
            _temporaryAddress = (ushort)((_temporaryAddress & 0xFC1F) | ((value & 0xF8) << 2));
        }

        _writeToggle = !_writeToggle;
    }

    private void WriteAddress(byte value)
    {
        if (!_writeToggle)
        {
            _temporaryAddress = (ushort)((_temporaryAddress & 0x00FF) | ((value & 0x3F) << 8));
        }
        else
        {
            _temporaryAddress = (ushort)((_temporaryAddress & 0xFF00) | value);
            _vramAddress = _temporaryAddress;
            DrivePpuBus((ushort)(_vramAddress & 0x3FFF));
        }

        _writeToggle = !_writeToggle;
    }

    private void IncrementVramAddress()
    {
        _vramAddress = (ushort)((_vramAddress + (((_control & 0x04) != 0) ? 32 : 1)) & 0x7FFF);
        DrivePpuBus((ushort)(_vramAddress & 0x3FFF));
    }

    private void RenderPixel(int y, int x)
    {
        var backgroundOpaque = false;
        byte colorIndex;

        if ((_mask & 0x08) != 0 && (x >= 8 || (_mask & 0x02) != 0))
        {
            var mux = (ushort)(0x8000 >> _fineX);
            var pixel = (byte)(
                ((_backgroundPatternShiftLow & mux) != 0 ? 1 : 0) |
                ((_backgroundPatternShiftHigh & mux) != 0 ? 2 : 0));
            var palette = (byte)(
                ((_backgroundAttributeShiftLow & mux) != 0 ? 1 : 0) |
                ((_backgroundAttributeShiftHigh & mux) != 0 ? 2 : 0));
            backgroundOpaque = pixel != 0;
            colorIndex = ReadMemory(
                backgroundOpaque
                    ? (ushort)(0x3F00 + (palette * 4) + pixel)
                    : (ushort)0x3F00);
        }
        else
        {
            colorIndex = ReadMemory(0x3F00);
        }

        byte spritePaletteOffset = 0;
        var spriteBehindBackground = false;
        var spriteZero = false;
        var spriteVisible =
            (_mask & 0x10) != 0 &&
            (x >= 8 || (_mask & 0x04) != 0) &&
            TryGetSpritePixel(out spritePaletteOffset, out spriteBehindBackground, out spriteZero);
        if (spriteVisible && backgroundOpaque && spriteZero && x < 255)
        {
            _status |= 0x40;
        }

        if (spriteVisible && (!backgroundOpaque || !spriteBehindBackground))
        {
            colorIndex = ReadMemory((ushort)(0x3F10 + spritePaletteOffset));
        }

        if ((_mask & 0x01) != 0)
        {
            colorIndex &= 0x30;
        }

        FrameBuffer[(y * Width) + x] = SystemPalette[colorIndex & 0x3F];
    }

    private void ClockBackgroundPipeline()
    {
        if ((Cycle is >= 1 and <= 257) || (Cycle is >= 321 and <= 337))
        {
            switch ((Cycle - 1) & 0x07)
            {
                case 0:
                    LoadBackgroundRegisters();
                    _nextBackgroundTile = ReadMemory(GetBackgroundNametableAddress());
                    break;
                case 2:
                    var attribute = ReadMemory(GetBackgroundAttributeAddress());
                    var shift = ((_vramAddress >> 4) & 4) | (_vramAddress & 2);
                    _nextBackgroundAttribute = (byte)((attribute >> shift) & 0x03);
                    break;
                case 4:
                    _nextBackgroundPatternLow = ReadMemory(GetBackgroundPatternAddress(highPlane: false));
                    break;
                case 6:
                    _nextBackgroundPatternHigh = ReadMemory(GetBackgroundPatternAddress(highPlane: true));
                    break;
                case 7:
                    _vramAddress = IncrementHorizontal(_vramAddress);
                    break;
            }
        }

        if (Cycle == 256)
        {
            IncrementVerticalPosition();
        }
    }

    private ushort GetBackgroundPatternAddress(bool highPlane)
    {
        var table = (_control & 0x10) != 0 ? 0x1000 : 0;
        var fineY = (_vramAddress >> 12) & 0x07;
        return (ushort)(table + (_nextBackgroundTile * 16) + fineY + (highPlane ? 8 : 0));
    }

    private void LoadBackgroundRegisters()
    {
        _backgroundPatternShiftLow =
            (ushort)((_backgroundPatternShiftLow & 0xFF00) | _nextBackgroundPatternLow);
        _backgroundPatternShiftHigh =
            (ushort)((_backgroundPatternShiftHigh & 0xFF00) | _nextBackgroundPatternHigh);
        _backgroundAttributeShiftLow =
            (ushort)((_backgroundAttributeShiftLow & 0xFF00) |
                     ((_nextBackgroundAttribute & 0x01) != 0 ? 0xFF : 0x00));
        _backgroundAttributeShiftHigh =
            (ushort)((_backgroundAttributeShiftHigh & 0xFF00) |
                     ((_nextBackgroundAttribute & 0x02) != 0 ? 0xFF : 0x00));
    }

    private void ShiftBackgroundRegisters()
    {
        _backgroundPatternShiftLow <<= 1;
        _backgroundPatternShiftHigh <<= 1;
        _backgroundAttributeShiftLow <<= 1;
        _backgroundAttributeShiftHigh <<= 1;
    }

    private bool TryGetSpritePixel(
        out byte paletteOffset,
        out bool behindBackground,
        out bool spriteZero)
    {
        for (var slot = 0; slot < _selectedSpriteCount; slot++)
        {
            if (_spriteXCounter[slot] != 0)
            {
                continue;
            }

            var pixel = (byte)(
                ((_spritePatternLow[slot] & 0x80) != 0 ? 1 : 0) |
                ((_spritePatternHigh[slot] & 0x80) != 0 ? 2 : 0));
            if (pixel == 0)
            {
                continue;
            }

            var attributes = _spriteAttributes[slot];
            paletteOffset = (byte)(((attributes & 0x03) * 4) + pixel);
            behindBackground = (attributes & 0x20) != 0;
            spriteZero = _selectedSpriteZero[slot] != 0;
            return true;
        }

        paletteOffset = 0;
        behindBackground = false;
        spriteZero = false;
        return false;
    }

    private void AdvanceSpriteRegisters()
    {
        for (var slot = 0; slot < _selectedSpriteCount; slot++)
        {
            if (_spriteXCounter[slot] > 0)
            {
                _spriteXCounter[slot]--;
            }
            else
            {
                _spritePatternLow[slot] <<= 1;
                _spritePatternHigh[slot] <<= 1;
            }
        }
    }

    private void ClockSpritePipeline(int targetScanline, bool evaluateSprites)
    {
        if (Cycle == 321)
        {
            _secondaryOamAddress = 0;
            _oamCopyBuffer = _secondaryOam[0];
        }

        if (evaluateSprites && Cycle is >= 1 and <= 64)
        {
            // During the first 64 dots, secondary OAM is filled with $FF.
            // Each physical byte is held for two dots by the clear circuitry.
            if (Cycle == 1)
            {
                _oamCopyBuffer = 0xFF;
            }

            if ((Cycle & 1) == 0)
            {
                var secondaryRow = (Cycle >> 1) - 1;
                _secondaryOamAddress = (byte)secondaryRow;
                AccessOamRow(secondaryRow);
                _secondaryOam[secondaryRow] = 0xFF;
            }
        }

        if (evaluateSprites && Cycle is >= 65 and <= 256)
        {
            if ((Cycle & 1) != 0)
            {
                if (Cycle == 65)
                {
                    _spriteEvaluationTargetScanline = targetScanline;
                    BeginSpriteEvaluation(targetScanline);
                }

                // Odd evaluation dots read primary OAM; the following even
                // dot decides whether to copy, skip, or enter overflow scan.
                _oamCopyBuffer = ReadPrimaryOam(_oamAddress);
            }
            else
            {
                ClockSpriteEvaluationWrite();
            }
        }

        if (Cycle is < 257 or > 320)
        {
            return;
        }

        if (Cycle == 257)
        {
            if (!evaluateSprites)
            {
                // The pre-render line performs dummy sprite fetches. It does
                // not clear/evaluate secondary OAM, so scanline 0 has no
                // active sprites on an NTSC 2C02.
                _secondarySpriteCount = 0;
                _evaluatedSpriteCount = 0;
            }

            CommitEvaluatedSprites();
        }

        // Rendering forces OAMADDR to zero throughout sprite tile loading.
        _oamAddress = 0;
        UpdateSpriteFetchOamLatch();

        var slot = (Cycle - 257) >> 3;
        var phase = (Cycle - 257) & 0x07;
        if (phase == 0)
        {
            if (slot < _selectedSpriteCount)
            {
                _spriteXCounter[slot] = _selectedSpriteX[slot];
            }

            _spritePatternLow[slot] = 0;
            _spritePatternHigh[slot] = 0;
        }
        else if (phase == 4)
        {
            if (slot < _selectedSpriteCount)
            {
                _spritePatternLow[slot] = ReadSpritePattern(slot, highPlane: false);
            }
        }
        else if (phase == 6)
        {
            if (slot < _selectedSpriteCount)
            {
                _spritePatternHigh[slot] = ReadSpritePattern(slot, highPlane: true);
            }
        }

        if (Cycle == 320 && _removeSpriteLimit)
        {
            for (var enhancedSlot = 8; enhancedSlot < _selectedSpriteCount; enhancedSlot++)
            {
                _spriteXCounter[enhancedSlot] = _selectedSpriteX[enhancedSlot];
                _spritePatternLow[enhancedSlot] = ReadSpritePattern(enhancedSlot, highPlane: false);
                _spritePatternHigh[enhancedSlot] = ReadSpritePattern(enhancedSlot, highPlane: true);
            }
        }
    }

    private void UpdateSpriteFetchOamLatch()
    {
        var fetchCycle = Cycle - 257;
        var slot = fetchCycle >> 3;
        var byteInSprite = Math.Min(3, fetchCycle & 0x07);
        _secondaryOamAddress = (byte)((slot * 4) + byteInSprite);
        AccessOamRow(_secondaryOamAddress);
        _oamCopyBuffer = _secondaryOam[_secondaryOamAddress];
    }

    private void BeginSpriteEvaluation(int targetScanline)
    {
        _spriteEvaluationTargetScanline = targetScanline;
        _secondaryOamAddress = 0;
        _secondarySpriteCount = 0;
        _spriteAddressHigh = (byte)((_oamAddress >> 2) & 0x3F);
        _spriteAddressLow = (byte)(_oamAddress & 0x03);
        _overflowBugCounter = 0;
        _spriteInRange = false;
        _spriteZeroAdded = false;
        _oamCopyDone = false;
        Array.Fill(_secondaryOamSpriteIndices, -1);
        Array.Clear(_secondaryOamSpriteZero);

        _evaluatedSpriteCount = 0;
        if (_removeSpriteLimit && targetScanline < Height)
        {
            if (_enhancedSpriteCacheDirty)
            {
                RebuildEnhancedSpriteCache();
            }

            var sprites = _enhancedSpriteScanlineMask[targetScanline];
            while (sprites != 0)
            {
                var sprite = BitOperations.TrailingZeroCount(sprites);
                _evaluatedSprites[_evaluatedSpriteCount++] = sprite;
                sprites &= sprites - 1;
            }
        }
    }

    private void RebuildEnhancedSpriteCache()
    {
        Array.Clear(_enhancedSpriteScanlineMask);
        var spriteHeight = (_control & 0x20) != 0 ? 16 : 8;
        for (var sprite = 0; sprite < 64; sprite++)
        {
            var firstScanline = _oam[sprite * 4] + 1;
            var lastScanline = Math.Min(Height, firstScanline + spriteHeight);
            var spriteBit = 1UL << sprite;
            for (var scanline = firstScanline; scanline < lastScanline; scanline++)
            {
                _enhancedSpriteScanlineMask[scanline] |= spriteBit;
            }
        }

        _enhancedSpriteCacheDirty = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void ClockSpriteEvaluationWrite()
    {
        if (Cycle == 256)
        {
            _secondarySpriteCount = Math.Min(8, (_secondaryOamAddress + 3) >> 2);
            if (_ppuRevision == NesPpuRevision.Rp2C02BOrEarlier &&
                _secondarySpriteCount < 8 &&
                IsSpriteYInRange(_oamCopyBuffer))
            {
                // On the letterless through 2C02B revisions, evaluation keeps
                // overwriting the next secondary-OAM Y byte after primary OAM
                // wraps. If the final value happens to be in range, the three
                // still-cleared $FF bytes form a real sprite at X=$FF.
                _secondarySpriteCount++;
            }
        }

        if (_oamCopyDone && _ppuRevision == NesPpuRevision.Rp2C02G)
        {
            // Once the primary-OAM scan wraps, the modern 2C02 keeps
            // refreshing rows without performing further range decisions.
            // Keep that observable address motion while avoiding the larger
            // evaluation path for the rest of the scanline.
            _spriteAddressHigh = (byte)((_spriteAddressHigh + 1) & 0x3F);
            _spriteAddressLow = 0;
            if (_secondaryOamAddress >= 0x20)
            {
                _oamCopyBuffer = _secondaryOam[_secondaryOamAddress & 0x1F];
            }

            _oamAddress = (byte)(_spriteAddressHigh << 2);
            return;
        }

        if (!_spriteInRange && IsSpriteYInRange(_oamCopyBuffer))
        {
            _spriteInRange = !_oamCopyDone;
        }

        if (_secondaryOamAddress < 0x20)
        {
            var secondaryAddress = _secondaryOamAddress;
            AccessOamRow(secondaryAddress);
            _secondaryOam[secondaryAddress] = _oamCopyBuffer;

            if (_spriteInRange)
            {
                if ((secondaryAddress & 0x03) == 0)
                {
                    var slot = secondaryAddress >> 2;
                    _secondaryOamSpriteIndices[slot] = _spriteAddressHigh;
                    if (Cycle == 66)
                    {
                        _spriteZeroAdded = true;
                        _secondaryOamSpriteZero[slot] = 1;
                    }
                }

                _spriteAddressLow++;
                _secondaryOamAddress++;

                if (_spriteAddressLow >= 4)
                {
                    _spriteAddressHigh = (byte)((_spriteAddressHigh + 1) & 0x3F);
                    _spriteAddressLow = 0;
                    if (_spriteAddressHigh == 0)
                    {
                        _oamCopyDone = true;
                    }
                }

                if ((_secondaryOamAddress & 0x03) == 0)
                {
                    _spriteInRange = false;
                    if (_spriteAddressLow != 0 && !IsSpriteYInRange(_oamCopyBuffer))
                    {
                        _spriteAddressLow = 0;
                    }
                }
            }
            else
            {
                _spriteAddressHigh = (byte)((_spriteAddressHigh + 1) & 0x3F);
                _spriteAddressLow = 0;
                if (_spriteAddressHigh == 0)
                {
                    _oamCopyDone = true;
                }
            }
        }
        else
        {
            // With secondary OAM full, its write port becomes a read. The
            // primary-OAM n/m counters then exhibit the 2C02's diagonal
            // overflow bug: an out-of-range comparison increments both
            // counters, so tile/attribute/X bytes can be tested as Y.
            AccessOamRow(_secondaryOamAddress);
            _oamCopyBuffer = _secondaryOam[_secondaryOamAddress & 0x1F];
            if (_oamCopyDone)
            {
                // Early PPUs keep evaluating after the primary address wraps.
                // Once secondary OAM is full, they skip whole sprites rather
                // than entering the modern post-wrap refresh shortcut.
                _spriteAddressHigh = (byte)((_spriteAddressHigh + 1) & 0x3F);
                _spriteAddressLow = 0;
            }
            else if (_spriteInRange)
            {
                _status |= 0x20;
                _spriteAddressLow++;
                if (_spriteAddressLow == 4)
                {
                    _spriteAddressHigh = (byte)((_spriteAddressHigh + 1) & 0x3F);
                    _spriteAddressLow = 0;
                }

                if (_overflowBugCounter == 0)
                {
                    _overflowBugCounter = 3;
                }
                else if (--_overflowBugCounter == 0)
                {
                    _oamCopyDone = true;
                    _spriteAddressLow = 0;
                }
            }
            else
            {
                _spriteAddressHigh = (byte)((_spriteAddressHigh + 1) & 0x3F);
                _spriteAddressLow = (byte)((_spriteAddressLow + 1) & 0x03);
                if (_spriteAddressHigh == 0)
                {
                    _oamCopyDone = true;
                }
            }
        }

        _oamAddress = (byte)((_spriteAddressHigh << 2) | (_spriteAddressLow & 0x03));
    }

    private void CommitEvaluatedSprites()
    {
        if (_removeSpriteLimit)
        {
            _selectedSpriteCount = _evaluatedSpriteCount;
            for (var slot = 0; slot < _selectedSpriteCount; slot++)
            {
                var sprite = _evaluatedSprites[slot];
                var offset = sprite * 4;
                _selectedSprites[slot] = sprite;
                _selectedSpriteY[slot] = _oam[offset];
                _selectedSpriteTile[slot] = _oam[offset + 1];
                _spriteAttributes[slot] = _oam[offset + 2];
                _selectedSpriteX[slot] = _oam[offset + 3];
                _selectedSpriteZero[slot] = sprite == 0 ? (byte)1 : (byte)0;
            }
        }
        else
        {
            _selectedSpriteCount = _secondarySpriteCount;
            for (var slot = 0; slot < _selectedSpriteCount; slot++)
            {
                var secondaryOffset = slot * 4;
                _selectedSprites[slot] = _secondaryOamSpriteIndices[slot];
                _selectedSpriteY[slot] = _secondaryOam[secondaryOffset];
                _selectedSpriteTile[slot] = _secondaryOam[secondaryOffset + 1];
                _spriteAttributes[slot] = _secondaryOam[secondaryOffset + 2];
                _selectedSpriteX[slot] = _secondaryOam[secondaryOffset + 3];
                _selectedSpriteZero[slot] = _secondaryOamSpriteZero[slot];
            }
        }

        Array.Clear(_spritePatternLow);
        Array.Clear(_spritePatternHigh);
        Array.Clear(_spriteXCounter);
    }

    private byte ReadSpritePattern(int slot, bool highPlane)
    {
        var value = ReadMemory(GetSpritePatternAddress(slot, highPlane));
        return (_spriteAttributes[slot] & 0x40) != 0 ? ReverseBits(value) : value;
    }

    private ushort GetSpritePatternAddress(int slot, bool highPlane)
    {
        if (slot >= _selectedSpriteCount)
        {
            var emptyTable = (_control & 0x20) != 0 || (_control & 0x08) != 0 ? 0x1000 : 0;
            return (ushort)(emptyTable + 0x0FF0 + (highPlane ? 8 : 0));
        }

        var top = _selectedSpriteY[slot] + 1;
        var tile = _selectedSpriteTile[slot];
        var attributes = _spriteAttributes[slot];
        var spriteHeight = (_control & 0x20) != 0 ? 16 : 8;
        var row = _spriteEvaluationTargetScanline - top;
        if ((attributes & 0x80) != 0)
        {
            row = spriteHeight - 1 - row;
        }

        int address;
        if (spriteHeight == 16)
        {
            var table = (tile & 1) * 0x1000;
            var tileNumber = tile & 0xFE;
            if (row >= 8)
            {
                tileNumber++;
                row -= 8;
            }

            address = table + (tileNumber * 16) + row;
        }
        else
        {
            var table = (_control & 0x08) != 0 ? 0x1000 : 0;
            address = table + (tile * 16) + row;
        }

        return (ushort)(address + (highPlane ? 8 : 0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsSpriteYInRange(byte y)
    {
        var top = y + 1;
        var spriteHeight = (_control & 0x20) != 0 ? 16 : 8;
        return _spriteEvaluationTargetScanline >= top &&
               _spriteEvaluationTargetScanline < top + spriteHeight;
    }

    private static byte ReverseBits(byte value)
    {
        value = (byte)(((value & 0xF0) >> 4) | ((value & 0x0F) << 4));
        value = (byte)(((value & 0xCC) >> 2) | ((value & 0x33) << 2));
        return (byte)(((value & 0xAA) >> 1) | ((value & 0x55) << 1));
    }

    private static ushort IncrementHorizontal(ushort address)
    {
        if ((address & 0x001F) == 31)
        {
            address &= 0xFFE0;
            address ^= 0x0400;
        }
        else
        {
            address++;
        }

        return address;
    }

    private void IncrementVerticalPosition()
    {
        if ((_vramAddress & 0x7000) != 0x7000)
        {
            _vramAddress += 0x1000;
            return;
        }

        _vramAddress &= 0x8FFF;
        var coarseY = (_vramAddress & 0x03E0) >> 5;
        if (coarseY == 29)
        {
            coarseY = 0;
            _vramAddress ^= 0x0800;
        }
        else if (coarseY == 31)
        {
            coarseY = 0;
        }
        else
        {
            coarseY++;
        }

        _vramAddress = (ushort)((_vramAddress & 0xFC1F) | (coarseY << 5));
    }

    private void CopyHorizontalPosition() =>
        _vramAddress = (ushort)((_vramAddress & 0xFBE0) | (_temporaryAddress & 0x041F));

    private void CopyVerticalPosition() =>
        _vramAddress = (ushort)((_vramAddress & 0x841F) | (_temporaryAddress & 0x7BE0));

    private byte ReadMemory(ushort address)
    {
        address &= 0x3FFF;
        if (address < 0x2000)
        {
            return _cartridge.PpuRead(address);
        }

        if (address < 0x3F00)
        {
            return _nametableRam[MapNametableAddress(address)];
        }

        return _paletteRam[MapPaletteAddress(address)];
    }

    private void WriteMemory(ushort address, byte value)
    {
        address &= 0x3FFF;
        DrivePpuBus(address);
        if (address < 0x2000)
        {
            _cartridge.PpuWrite(address, value);
        }
        else if (address < 0x3F00)
        {
            _nametableRam[MapNametableAddress(address)] = value;
        }
        else
        {
            _paletteRam[MapPaletteAddress(address)] = value;
        }
    }

    private void ClockPpuBusAddress()
    {
        if (!RenderingEnabled || (Scanline is not (>= 0 and < 240) && Scanline != 261))
        {
            _cartridge.ClockPpuAddress(_ppuBusAddress);
            return;
        }

        if (Scanline is >= 0 and < 240 && Cycle == 0)
        {
            // The unused nametable fetches at the end of the preceding line
            // leave a calculated background-pattern address on the PPU bus at
            // the start of a visible line. MMC3 uses this brief A12 pulse to
            // reject the otherwise-spurious background edge at dot 5. The
            // address update is absent on line 0 when the odd-frame dot was
            // skipped; _oddFrame identifies the complementary, unskipped case
            // after the pre-render-to-visible transition.
            var retainedPatternAddress =
                (Scanline > 0 || _oddFrame) && (_control & 0x10) != 0
                    ? (ushort)0x1000
                    : (ushort)0x0000;
            _ppuBusAddress = retainedPatternAddress;
            _cartridge.ClockPpuAddress(_ppuBusAddress);
            return;
        }

        if ((Cycle is >= 1 and <= 256) || (Cycle is >= 321 and <= 336))
        {
            var fetchPhase = (Cycle - 1) & 0x07;
            _ppuBusAddress = fetchPhase switch
            {
                0 or 1 => GetBackgroundNametableAddress(),
                2 or 3 => GetBackgroundAttributeAddress(),
                4 or 5 => GetBackgroundPatternAddress(highPlane: false),
                _ => GetBackgroundPatternAddress(highPlane: true)
            };
            _cartridge.ClockPpuAddress(_ppuBusAddress);
            return;
        }

        if (Cycle is >= 257 and <= 320)
        {
            var fetchPhase = (Cycle - 257) & 0x07;
            var spriteSlot = (Cycle - 257) >> 3;
            _ppuBusAddress = fetchPhase switch
            {
                0 or 1 => GetBackgroundNametableAddress(),
                2 or 3 => GetBackgroundAttributeAddress(),
                4 or 5 => GetSpritePatternAddress(spriteSlot, highPlane: false),
                _ => GetSpritePatternAddress(spriteSlot, highPlane: true)
            };
            _cartridge.ClockPpuAddress(_ppuBusAddress);
            return;
        }

        if (Cycle is >= 337 and <= 340)
        {
            _ppuBusAddress = GetBackgroundNametableAddress();
        }

        _cartridge.ClockPpuAddress(_ppuBusAddress);
    }

    private ushort GetBackgroundNametableAddress() =>
        (ushort)(0x2000 | (_vramAddress & 0x0FFF));

    private ushort GetBackgroundAttributeAddress() =>
        (ushort)(
            0x23C0 |
            (_vramAddress & 0x0C00) |
            ((_vramAddress >> 4) & 0x38) |
            ((_vramAddress >> 2) & 0x07));

    private void DrivePpuBus(ushort address)
    {
        _ppuBusAddress = (ushort)(address & 0x3FFF);
        _cartridge.ClockPpuAddress(_ppuBusAddress);
    }

    private int MapNametableAddress(ushort address)
    {
        var normalized = (address - 0x2000) % 0x1000;
        var table = normalized / 0x0400;
        var offset = normalized % 0x0400;

        var physicalTable = _cartridge.Mirroring switch
        {
            NametableMirroring.OneScreenLower => 0,
            NametableMirroring.OneScreenUpper => 1,
            NametableMirroring.Vertical => table & 1,
            NametableMirroring.Horizontal => table >> 1,
            _ => table
        };

        return (physicalTable * 0x0400) + offset;
    }

    private static int MapPaletteAddress(ushort address)
    {
        var index = (address - 0x3F00) % 32;
        return index is 0x10 or 0x14 or 0x18 or 0x1C ? index - 0x10 : index;
    }
}
