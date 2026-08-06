namespace PixelDeck.Emulation.N64;

/// <summary>
/// The 1 Mbit FlashRAM save device (Macronix MX29L1100 and relatives) that
/// Paper Mario, Majora's Mask and a number of later titles carry.
/// </summary>
/// <remarks>
/// FlashRAM is not memory that can simply be read and written where it is
/// mapped, which is how Pixel64 treated PI domain 2 until now. It is a command
/// driven device: the CPU writes a command word to <c>0x08010000</c>, reads a
/// status register at <c>0x08000000</c>, and moves the bytes themselves by PI
/// DMA through a 128-byte page buffer. A title that finds flat RAM there gets no
/// silicon ID, a status register that never reports success, and saved data that
/// reads back as whatever happened to be in the buffer.
///
/// Modelled on Project64's <c>CFlashRam</c> rather than mupen64plus-core's
/// simpler version, because libultra compares the status register against whole
/// 32-bit constants -- <c>osFlashCheckEraseEnd</c> tests for <c>0x11118008</c>
/// exactly. A status byte holding <c>0x08</c> never matches that, so the status
/// here is the full 64-bit value with its <c>0x1111800x</c> tag, and the command
/// that does the work is <c>0xD2</c>, with <c>0x78</c> and <c>0xA5</c> only
/// selecting what it will do. Both are GPLv2 like PixelDeck; see
/// THIRD_PARTY_NOTICES.md.
/// </remarks>
public sealed class N64FlashRam
{
    /// <summary>
    /// The unit every erase and program command moves, and the size of the
    /// buffer a write DMA fills.
    /// </summary>
    public const int PageSize = 128;

    // Status words libultra matches against whole. The low half carries the
    // device ID so that an eight-byte status DMA doubles as the ID read.
    private const ulong SiliconIdStatus = 0x1111800100C2001E;
    private const ulong ReadModeStatus = 0x11118004F0000000;
    private const ulong EraseStatus = 0x1111800800C2001E;
    private const ulong WriteStatus = 0x1111800400C2001E;

    private enum FlashMode
    {
        Idle,
        ReadArray,
        Status,
        SectorErase,
        ChipErase,
        PageProgram
    }

    private readonly byte[] _storage;
    private readonly byte[] _pageBuffer = new byte[PageSize];
    private FlashMode _mode = FlashMode.Idle;
    private ulong _status;
    private int _offset;

    public N64FlashRam(byte[] storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
        Reset();
    }

    /// <summary>Set when a command has changed the backing store.</summary>
    public bool Dirty { get; private set; }

    public void MarkFlushed() => Dirty = false;

    public void Reset()
    {
        _mode = FlashMode.Idle;
        _status = 0;
        _offset = 0;
        Array.Fill(_pageBuffer, (byte)0xFF);
    }

    /// <summary>
    /// The status register. Only the high half is visible to a 32-bit read,
    /// which is the half libultra compares.
    /// </summary>
    public uint ReadIoWord(uint physicalAddress) =>
        (physicalAddress & 0x1FFFF) == 0x00000 ? (uint)(_status >> 32) : 0;

    public void WriteIoWord(uint physicalAddress, uint value)
    {
        if ((physicalAddress & 0x1FFFF) == 0x10000)
        {
            ExecuteCommand(value);
        }
    }

    private void ExecuteCommand(uint command)
    {
        switch (command & 0xFF000000)
        {
            case 0xD2000000:
                Commit();
                break;

            case 0xE1000000:
                _mode = FlashMode.Status;
                _status = SiliconIdStatus;
                break;

            case 0xF0000000:
            case 0x00000000:
                _mode = FlashMode.ReadArray;
                _status = ReadModeStatus;
                break;

            case 0x4B000000:
                _offset = (int)(command & 0xFFFF) * PageSize;
                break;

            case 0x78000000:
                _mode = FlashMode.SectorErase;
                _status = EraseStatus;
                break;

            case 0x3C000000:
                _mode = FlashMode.ChipErase;
                _status = EraseStatus;
                break;

            case 0xB4000000:
                _mode = FlashMode.PageProgram;
                break;

            case 0xA5000000:
                _offset = (int)(command & 0xFFFF) * PageSize;
                _status = WriteStatus;
                break;
        }
    }

    /// <summary>
    /// 0xD2 is what actually moves bytes. The commands before it only choose
    /// which operation is pending and where it lands.
    /// </summary>
    private void Commit()
    {
        switch (_mode)
        {
            case FlashMode.SectorErase:
                Fill(_offset, PageSize, 0xFF);
                break;

            case FlashMode.ChipErase:
                Fill(0, _storage.Length, 0xFF);
                break;

            case FlashMode.PageProgram:
                if (_offset >= 0 && _offset + PageSize <= _storage.Length)
                {
                    _pageBuffer.CopyTo(_storage.AsSpan(_offset, PageSize));
                    Dirty = true;
                }

                break;
        }

        _mode = FlashMode.Idle;
    }

    private void Fill(int offset, int length, byte value)
    {
        if (offset < 0 || offset >= _storage.Length)
        {
            return;
        }

        Array.Fill(_storage, value, offset, Math.Min(length, _storage.Length - offset));
        Dirty = true;
    }

    /// <summary>
    /// PI DMA from the cartridge into RDRAM. Returns false when the device is
    /// in a mode that answers nothing, so the caller leaves the destination
    /// alone rather than copying stale bytes into it.
    /// </summary>
    public bool TryReadDma(Span<byte> destination, uint cartridgeOffset)
    {
        if (_mode == FlashMode.Status)
        {
            // An eight-byte status read is also how libultra reads the ID.
            if (destination.Length < 8)
            {
                return false;
            }

            WriteBigEndian(destination[..4], (uint)(_status >> 32));
            WriteBigEndian(destination.Slice(4, 4), (uint)_status);
            return true;
        }

        if (_mode != FlashMode.ReadArray)
        {
            return false;
        }

        // The array is addressed in 16-bit units at DMA start, so the offset
        // doubles.
        var start = (int)((cartridgeOffset & 0xFFFF) * 2);
        for (var index = 0; index < destination.Length; index++)
        {
            var source = start + index;
            destination[index] = source < _storage.Length ? _storage[source] : (byte)0xFF;
        }

        return true;
    }

    /// <summary>
    /// PI DMA from RDRAM into the cartridge. The bytes only reach the page
    /// buffer; nothing touches the array until a following 0xD2.
    /// </summary>
    public bool TryWriteDma(ReadOnlySpan<byte> source, uint cartridgeOffset)
    {
        _ = cartridgeOffset;
        if (_mode != FlashMode.PageProgram)
        {
            return false;
        }

        var length = Math.Min(source.Length, PageSize);
        source[..length].CopyTo(_pageBuffer);
        return true;
    }

    private static void WriteBigEndian(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }
}
