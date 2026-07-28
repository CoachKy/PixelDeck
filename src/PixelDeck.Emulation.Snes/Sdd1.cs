namespace PixelDeck.Emulation.Snes;

/// <summary>
/// The S-DD1: a ROM bank mapper paired with a hardware graphics decompressor,
/// used by Star Ocean and Street Fighter Alpha 2.
/// </summary>
/// <remarks>
/// The mapper half works like the SA-1's super MMC: four 1 MiB windows cover
/// banks $C0-$FF. The decompressor half is transparent to the CPU — a game arms
/// a DMA channel through $4800/$4801 and then starts an ordinary DMA whose
/// A-bus source lies in ROM. The chip substitutes decompressed bytes for the
/// raw ROM contents as the transfer runs, so the destination (usually VRAM)
/// receives expanded tile data the cartridge never had to store.
/// </remarks>
internal sealed class Sdd1
{
    private readonly SnesCartridge _cartridge;
    private readonly Sdd1Decompressor _decompressor = new();

    // $4804-$4807 select which 1 MiB block each of the four upper-bank windows
    // exposes. Power-on order is the identity mapping.
    private readonly byte[] _mmcBanks = [0, 1, 2, 3];

    private byte _dmaEnable;       // $4800: channels that may use the chip
    private byte _transferEnable;  // $4801: channels armed for the next transfer
    private bool _transferActive;

    public Sdd1(SnesCartridge cartridge) => _cartridge = cartridge;

    /// <summary>Decompression runs started, for diagnostics.</summary>
    public long DecompressionCount { get; private set; }

    /// <summary>Transfers that reached the chip but ran undecompressed.</summary>
    internal long CandidateTransfers { get; private set; }

    /// <summary>
    /// Decompression runs seen per bitplane mode and per context template, so
    /// the header variants a cartridge actually uses can be confirmed rather
    /// than assumed from a single sample.
    /// </summary>
    internal int[] HeaderModeCounts { get; } = new int[4];

    internal int[] HeaderContextCounts { get; } = new int[4];

    /// <summary>Source address and header of each run, for replaying one directly.</summary>
    internal List<(uint Source, byte Header)> Runs { get; } = [];

    public byte ReadRegister(ushort address) => address switch
    {
        0x4800 => _dmaEnable,
        0x4801 => _transferEnable,
        >= 0x4804 and <= 0x4807 => _mmcBanks[address - 0x4804],
        _ => 0x00
    };

    public void WriteRegister(ushort address, byte value)
    {
        switch (address)
        {
            case 0x4800:
                _dmaEnable = value;
                break;
            case 0x4801:
                _transferEnable = value;
                break;
            case >= 0x4804 and <= 0x4807:
                _mmcBanks[address - 0x4804] = (byte)(value & 0x07);
                break;
        }
    }

    /// <summary>
    /// Called as a DMA channel starts. When the channel is armed on both $4800
    /// and $4801 the transfer is fed by the decompressor instead of raw ROM.
    /// </summary>
    public bool TryBeginTransfer(int channel, uint sourceAddress)
    {
        CandidateTransfers++;

        // Both registers gate the transfer. The observed sequence is
        // $4800=mask, $4801=mask, DMA, $4800=0 — the game disarms $4800 itself,
        // so neither register is cleared here. Ordinary DMAs run with $4800
        // clear and correctly bypass the decompressor.
        var mask = 1 << channel;
        if ((_dmaEnable & mask) == 0 || (_transferEnable & mask) == 0)
        {
            return false;
        }

        _transferActive = true;
        DecompressionCount++;
        var header = ReadRom(sourceAddress);
        HeaderModeCounts[header >> 6]++;
        HeaderContextCounts[(header >> 4) & 3]++;
        if (Runs.Count < 32)
        {
            Runs.Add((sourceAddress, header));
        }
        _decompressor.Begin(this, sourceAddress);
        return true;
    }

    public byte ReadTransferByte() => _transferActive ? _decompressor.ReadByte() : (byte)0;

    internal int InputBytesConsumed => _decompressor.InputBytesConsumed;

    public void EndTransfer() => _transferActive = false;

    /// <summary>
    /// ROM as both the CPU and the decompressor see it. Banks $C0-$FF are the
    /// four mapper windows; the lower banks are a plain LoROM projection.
    /// </summary>
    public byte ReadRom(uint address)
    {
        var rom = _cartridge.RomSpan;
        if (rom.Length == 0)
        {
            return 0;
        }

        var bank = (byte)(address >> 16);
        var offset = (ushort)address;

        if (bank >= 0xC0)
        {
            var linear = (_mmcBanks[(bank - 0xC0) >> 4] << 20) | ((bank & 0x0F) << 16) | offset;
            return rom[linear % rom.Length];
        }

        if (offset < 0x8000)
        {
            return 0;
        }

        return rom[(((bank & 0x7F) << 15) | (offset & 0x7FFF)) % rom.Length];
    }
}

/// <summary>
/// The S-DD1's lossless decompressor. It is an adaptive binary coder in the
/// Allen-Boliek-Schwartz family: a Golomb run decoder supplies bits, a 33-state
/// probability model per context decides whether each decoded bit is the most
/// or least probable symbol, and an output stage interleaves the resulting bits
/// into SNES bitplane order.
/// </summary>
internal sealed class Sdd1Decompressor
{
    /// <summary>
    /// Probability evolution table: for each of the 33 states, the Golomb code
    /// order to use and the next state after a most- or least-probable symbol.
    /// </summary>
    private static readonly byte[,] Evolution =
    {
        { 0, 25, 25 }, { 0, 2, 1 }, { 0, 3, 1 }, { 0, 4, 2 }, { 0, 5, 3 },
        { 1, 6, 4 }, { 1, 7, 5 }, { 1, 8, 6 }, { 1, 9, 7 }, { 2, 10, 8 },
        { 2, 11, 9 }, { 2, 12, 10 }, { 2, 13, 11 }, { 3, 14, 12 }, { 3, 15, 13 },
        { 3, 16, 14 }, { 3, 17, 15 }, { 4, 18, 16 }, { 4, 19, 17 }, { 5, 20, 18 },
        { 5, 21, 19 }, { 6, 22, 20 }, { 6, 23, 21 }, { 7, 24, 22 }, { 7, 24, 23 },
        { 0, 26, 1 }, { 1, 27, 2 }, { 2, 28, 4 }, { 3, 29, 8 }, { 4, 30, 12 },
        { 5, 31, 16 }, { 6, 32, 18 }, { 7, 24, 22 }
    };

    /// <summary>
    /// Maps a Golomb codeword to a run length. The bit-reversed ordering is the
    /// chip's own: codewords arrive most significant bit first, but the run
    /// length they encode is built up from the least significant end.
    /// </summary>
    private static readonly byte[] RunTable =
    [
        128, 64, 96, 32, 112, 48, 80, 16, 120, 56, 88, 24, 104, 40, 72, 8,
        124, 60, 92, 28, 108, 44, 76, 12, 116, 52, 84, 20, 100, 36, 68, 4,
        126, 62, 94, 30, 110, 46, 78, 14, 118, 54, 86, 22, 102, 38, 70, 6,
        122, 58, 90, 26, 106, 42, 74, 10, 114, 50, 82, 18, 98, 34, 66, 2,
        127, 63, 95, 31, 111, 47, 79, 15, 119, 55, 87, 23, 103, 39, 71, 7,
        123, 59, 91, 27, 107, 43, 75, 11, 115, 51, 83, 19, 99, 35, 67, 3,
        125, 61, 93, 29, 109, 45, 77, 13, 117, 53, 85, 21, 101, 37, 69, 5,
        121, 57, 89, 25, 105, 41, 73, 9, 113, 49, 81, 17, 97, 33, 65, 1
    ];

    /// <summary>
    /// Compressed bytes consumed by the current run. The ratio of output to
    /// input is the cheapest desync check available: real S-DD1 graphics
    /// compress at roughly 2:1 to 4:1, so a far higher figure means the decoder
    /// is emitting long predicted runs instead of reading codewords.
    /// </summary>
    internal int InputBytesConsumed { get; private set; }

    private readonly int[] _runCounters = new int[8];
    private readonly byte[] _contextState = new byte[32];
    private readonly byte[] _contextMps = new byte[32];
    // Nine bits of history per plane: the widest context mask reaches bit 8.
    private readonly ushort[] _previousBits = new ushort[8];

    private Sdd1 _chip = null!;
    private uint _readAddress;
    private int _inputStream;
    private int _validBits;

    private int _bitplaneMode;
    private int _highContextMask;
    private int _lowContextMask;

    private readonly byte[] _output = new byte[2];
    private int _outputCount;
    private int _outputIndex;
    private int _iterationIndex;

    public void Begin(Sdd1 chip, uint sourceAddress)
    {
        _chip = chip;
        _readAddress = sourceAddress;

        // The first byte is a header: bits 7-6 pick the bitplane interleave and
        // bits 5-4 pick which previous bits form each context.
        var header = NextInputByte();
        var second = NextInputByte();
        _bitplaneMode = header >> 6;
        (_highContextMask, _lowContextMask) = (header & 0x30) switch
        {
            0x00 => (0x01C0, 0x0001),
            0x10 => (0x0180, 0x0001),
            0x20 => (0x00C0, 0x0001),
            _ => (0x0180, 0x0003)
        };

        _inputStream = ((header << 11) | (second << 3)) & 0xFFFF;
        _validBits = 5;

        Array.Clear(_runCounters);
        Array.Clear(_contextState);
        Array.Clear(_contextMps);
        Array.Clear(_previousBits);
        _iterationIndex = 0;
        _outputCount = 0;
        _outputIndex = 0;
        InputBytesConsumed = 2;
    }

    /// <summary>Produces one decompressed byte.</summary>
    public byte ReadByte()
    {
        if (_outputIndex >= _outputCount)
        {
            DecodeIteration();
        }

        return _output[_outputIndex++];
    }

    /// <summary>
    /// Decodes one iteration of the output stage.
    /// </summary>
    /// <remarks>
    /// The three paired modes decode a plane pair together: for each of eight
    /// pixels a bit is taken for the low plane and then the high plane, and the
    /// iteration emits the two resulting bytes. That interleaving matters
    /// beyond byte layout, because the probability model advances with every
    /// decoded bit — decoding a whole byte of one plane first would feed every
    /// later bit from the wrong state. A pair covers eight iterations, which is
    /// the sixteen bytes the SNES uses for one plane pair of a tile.
    /// The fourth mode is per-pixel instead: one byte whose eight bits each
    /// come from a different plane.
    /// </remarks>
    private void DecodeIteration()
    {
        _outputIndex = 0;

        if (_bitplaneMode == 3)
        {
            var pixel = 0;
            for (var plane = 0; plane < 8; plane++)
            {
                pixel = (pixel << 1) | DecodePlaneBit(plane);
            }

            _output[0] = (byte)pixel;
            _outputCount = 1;
            _iterationIndex++;
            return;
        }

        var pairBase = _bitplaneMode switch
        {
            1 => (_iterationIndex / 8 * 2) & 7,
            2 => (_iterationIndex / 8 & 1) * 2,
            _ => 0
        };

        int low = 0, high = 0;
        for (var index = 0; index < 8; index++)
        {
            low = (low << 1) | DecodePlaneBit(pairBase);
            high = (high << 1) | DecodePlaneBit(pairBase + 1);
        }

        _output[0] = (byte)low;
        _output[1] = (byte)high;
        _outputCount = 2;
        _iterationIndex++;
    }

    /// <summary>
    /// Decodes one bit for a bitplane, using that plane's own history as the
    /// prediction context and folding the result back into it.
    /// </summary>
    private int DecodePlaneBit(int plane)
    {
        var previous = _previousBits[plane];
        var context = ((plane & 1) << 4) |
                      ((previous & _highContextMask) >> 5) |
                      (previous & _lowContextMask);
        var bit = DecodeBit(context & 0x1F);
        _previousBits[plane] = (ushort)(((previous << 1) | bit) & 0x01FF);
        return bit;
    }

    /// <summary>
    /// Decodes one bit for a context, evolving that context's probability state.
    /// </summary>
    private byte DecodeBit(int context)
    {
        var state = _contextState[context];
        var run = ReadGolombBit(Evolution[state, 0]);
        switch (run)
        {
            case 0:
                // Still inside a run of most-probable symbols.
                return _contextMps[context];
            case 2:
                // The run reached its full length, so confidence increases.
                _contextState[context] = Evolution[state, 1];
                return _contextMps[context];
            default:
                // A least-probable symbol: confidence drops, and at the least
                // confident states the two symbols swap roles.
                var symbol = (byte)(_contextMps[context] ^ 1);

                // At the least-confident states the least-probable symbol has
                // become the more likely one, so the roles swap. State 1's
                // least-probable transition is a self-loop, which would other-
                // wise leave the context predicting the wrong symbol forever.
                if (state <= 1)
                {
                    _contextMps[context] ^= 1;
                }

                _contextState[context] = Evolution[state, 2];
                return symbol;
        }
    }

    /// <summary>
    /// Returns 0 while inside a run of zeros, 1 for a terminating one, and 2
    /// when the run reached the full 2^order length with no terminator.
    /// </summary>
    private int ReadGolombBit(int order)
    {
        if (_runCounters[order] == 0)
        {
            _runCounters[order] = ReadCodeword(order);
        }

        _runCounters[order]--;
        if (_runCounters[order] == 0x80)
        {
            _runCounters[order] = 0;
            return 2;
        }

        return _runCounters[order] == 0 ? 1 : 0;
    }

    /// <summary>
    /// Pulls one variable-length codeword from the compressed stream. Bits are
    /// consumed from the top of a 16-bit window that is refilled a byte at a
    /// time from the low end.
    /// </summary>
    private int ReadCodeword(int order)
    {
        if (_validBits == 0)
        {
            _inputStream |= NextInputByte();
            _validBits = 8;
        }

        _inputStream = (_inputStream << 1) & 0xFFFF;
        _validBits--;
        _inputStream ^= 0x8000;

        if ((_inputStream & 0x8000) != 0)
        {
            // Escape: a full-length run of 2^order zeros with no terminator.
            return 0x80 + (1 << order);
        }

        var index = ((_inputStream >> 8) | (0x7F >> order)) & 0x7F;
        _inputStream = (_inputStream << order) & 0xFFFF;
        _validBits -= order;
        if (_validBits < 0)
        {
            _inputStream |= NextInputByte() << -_validBits;
            _inputStream &= 0xFFFF;
            _validBits += 8;
        }

        return RunTable[index];
    }

    private byte NextInputByte()
    {
        InputBytesConsumed++;
        return _chip.ReadRom(_readAddress++);
    }
}
