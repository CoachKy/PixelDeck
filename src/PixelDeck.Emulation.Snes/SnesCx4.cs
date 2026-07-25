namespace PixelDeck.Emulation.Snes;

/// <summary>
/// High-level model of Capcom's CX4 cartridge coprocessor. The original part is
/// a Hitachi HG51B169 with internal firmware; PixelSNES implements the
/// observable Mega Man X2/X3 command interface so no external firmware is
/// required.
/// </summary>
internal sealed class SnesCx4
{
    private const int AddressBase = 0x6000;
    private const int MemorySize = 0x2000;
    private const int CommandRegister = 0x1F4F;
    private const int CommandModeRegister = 0x1F4D;
    private const int BusyRegister = 0x1F5E;
    private const int RegisterBase = 0x1F80;

    private static readonly short[] Sine = CreateSineTable();

    // Documented input order. CX4 command $5C emits this sequence in reverse.
    private static readonly byte[] ImmediateSource =
    [
        0x00, 0xFE, 0xFF, 0x00, 0x01, 0x00, 0xFE, 0xFF,
        0xFF, 0x01, 0x00, 0x00, 0xFF, 0xFF, 0x7F, 0xFF,
        0x7F, 0xFF, 0x00, 0x7F, 0xFF, 0x00, 0x80, 0x00,
        0x7F, 0xFF, 0xFF, 0x80, 0x00, 0x00, 0xFF, 0xFF,
        0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,
        0xFF, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00
    ];

    private readonly SnesCartridge _cartridge;
    private readonly byte[] _memory = new byte[MemorySize];
    private readonly long[] _commandCounts = new long[256];

    public SnesCx4(SnesCartridge cartridge)
    {
        _cartridge = cartridge;
    }

    public byte Read(ushort address)
    {
        var index = address - AddressBase;
        return index == BusyRegister ? (byte)0 : _memory[index];
    }

    public byte Peek(ushort address) => Read(address);

    public void Write(ushort address, byte value)
    {
        var index = address - AddressBase;
        _memory[index] = value;

        if (index == 0x1F47)
        {
            TransferRomData();
        }
        else if (index == CommandRegister)
        {
            Execute(value);
        }
    }

    internal long GetCommandExecutionCount(byte command) =>
        _commandCounts[command];

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(_memory);
        foreach (var count in _commandCounts)
        {
            writer.Write(count);
        }
    }

    public void LoadState(BinaryReader reader)
    {
        reader.ReadExactly(_memory);
        for (var index = 0; index < _commandCounts.Length; index++)
        {
            _commandCounts[index] = reader.ReadInt64();
        }
    }

    private void TransferRomData()
    {
        var source = (uint)Read24(0x1F40);
        var length = Read16(0x1F43);
        var destination = Read16(0x1F45) & (MemorySize - 1);

        for (var index = 0; index < length; index++)
        {
            _memory[(destination + index) & (MemorySize - 1)] =
                _cartridge.ReadCx4RomByte(source, index);
        }
    }

    private void Execute(byte command)
    {
        _commandCounts[command]++;
        if (_memory[CommandModeRegister] == 0x0E &&
            command < 0x40 &&
            (command & 0x03) == 0)
        {
            _memory[RegisterBase] = (byte)(command >> 2);
            return;
        }

        switch (command)
        {
            case 0x00:
                ExecuteSpriteCommand();
                break;
            case 0x01:
                Array.Clear(_memory, 0x300, 0x900);
                DrawWireFrame();
                break;
            case 0x05:
                CalculatePropulsion();
                break;
            case 0x0D:
                SetVectorLength();
                break;
            case 0x10:
                PolarToCartesian(compactResult: true);
                break;
            case 0x13:
                PolarToCartesian(compactResult: false);
                break;
            case 0x15:
                CalculateVectorLength();
                break;
            case 0x1F:
                CalculateAngle();
                break;
            case 0x22:
                BuildTrapezoidEdges();
                break;
            case 0x25:
                Multiply();
                break;
            case 0x2D:
                TransformSingleCoordinate();
                break;
            case 0x40:
                SumRam();
                break;
            case 0x54:
                Square();
                break;
            case 0x5C:
                EmitImmediatePattern(ImmediateSource.Length, destination: 0);
                break;
            case >= 0x5E and <= 0x7C when (command & 1) == 0:
                EmitImmediatePattern(48 - (((command - 0x5E) / 2) * 3), Read24(RegisterBase));
                break;
            case 0x89:
                Write24(RegisterBase, 0x054336);
                Write24(RegisterBase + 3, 0xFFFFFF);
                break;
        }
    }

    private void ExecuteSpriteCommand()
    {
        switch (_memory[CommandModeRegister])
        {
            case 0x00:
                BuildObjectAttributes();
                break;
            case 0x03:
                ScaleAndRotate(rowPadding: 0);
                break;
            case 0x05:
                TransformLineList();
                break;
            case 0x07:
                ScaleAndRotate(rowPadding: 64);
                break;
            case 0x08:
                DrawWireFrame();
                break;
            case 0x0B:
                DisintegrateSprite();
                break;
            case 0x0C:
                DrawWave();
                break;
        }
    }

    private void BuildObjectAttributes()
    {
        var firstObject = _memory[0x626];
        var lowTable = firstObject * 4;
        for (var index = 0x1FD; index > lowTable; index -= 4)
        {
            _memory[index] = 0xE0;
        }

        var globalX = Read16(0x621);
        var globalY = Read16(0x623);
        var highTable = 0x200 + (firstObject >> 2);
        var highBitOffset = (firstObject & 3) * 2;
        var objectsRemaining = 128 - firstObject;
        var descriptor = 0x220;

        for (var objectIndex = 0;
             objectIndex < _memory[0x620] && objectsRemaining > 0 && descriptor + 15 < MemorySize;
             objectIndex++, descriptor += 16)
        {
            var objectX = unchecked((short)(Read16(descriptor) - globalX));
            var objectY = unchecked((short)(Read16(descriptor + 2) - globalY));
            var tile = _memory[descriptor + 5];
            var attributes = (byte)(_memory[descriptor + 4] | _memory[descriptor + 6]);
            var source = (uint)Read24(descriptor + 7);
            var pieceCount = _cartridge.ReadCx4RomByte(source, 0);

            if (pieceCount == 0)
            {
                WriteObject(
                    ref lowTable,
                    ref highTable,
                    ref highBitOffset,
                    ref objectsRemaining,
                    objectX,
                    objectY,
                    tile,
                    attributes,
                    large: true);
                continue;
            }

            for (var piece = 0; piece < pieceCount && objectsRemaining > 0; piece++)
            {
                var pieceOffset = 1 + piece * 4;
                var flags = _cartridge.ReadCx4RomByte(source, pieceOffset);
                var xOffset = unchecked((sbyte)_cartridge.ReadCx4RomByte(source, pieceOffset + 1));
                var yOffset = unchecked((sbyte)_cartridge.ReadCx4RomByte(source, pieceOffset + 2));
                var tileOffset = _cartridge.ReadCx4RomByte(source, pieceOffset + 3);
                var large = (flags & 0x20) != 0;

                if ((attributes & 0x40) != 0)
                {
                    xOffset = unchecked((sbyte)(-xOffset - (large ? 16 : 8)));
                }
                if ((attributes & 0x80) != 0)
                {
                    yOffset = unchecked((sbyte)(-yOffset - (large ? 16 : 8)));
                }

                var x = objectX + xOffset;
                var y = objectY + yOffset;
                if (x is < -16 or > 272 || y is < -16 or > 224)
                {
                    continue;
                }

                WriteObject(
                    ref lowTable,
                    ref highTable,
                    ref highBitOffset,
                    ref objectsRemaining,
                    x,
                    y,
                    (byte)(tile + tileOffset),
                    (byte)(attributes ^ (flags & 0xC0)),
                    large);
            }
        }
    }

    private void WriteObject(
        ref int lowTable,
        ref int highTable,
        ref int highBitOffset,
        ref int objectsRemaining,
        int x,
        int y,
        byte tile,
        byte attributes,
        bool large)
    {
        if (lowTable + 3 >= 0x200 || highTable >= 0x220)
        {
            objectsRemaining = 0;
            return;
        }

        _memory[lowTable] = (byte)x;
        _memory[lowTable + 1] = (byte)y;
        _memory[lowTable + 2] = tile;
        _memory[lowTable + 3] = attributes;

        var pairMask = 3 << highBitOffset;
        var pair = ((x & 0x100) != 0 ? 1 : 0) | (large ? 2 : 0);
        _memory[highTable] = (byte)((_memory[highTable] & ~pairMask) | (pair << highBitOffset));

        lowTable += 4;
        objectsRemaining--;
        highBitOffset = (highBitOffset + 2) & 6;
        if (highBitOffset == 0)
        {
            highTable++;
        }
    }

    private void ScaleAndRotate(int rowPadding)
    {
        var angle = Read16(RegisterBase) & 0x1FF;
        var xScale = Read16(0x1F8F);
        var yScale = Read16(0x1F92);
        if ((xScale & 0x8000) != 0) xScale = 0x7FFF;
        if ((yScale & 0x8000) != 0) yScale = 0x7FFF;

        int a;
        int b;
        int c;
        int d;
        switch (angle)
        {
            case 0:
                a = xScale;
                b = 0;
                c = 0;
                d = yScale;
                break;
            case 128:
                a = 0;
                b = -yScale;
                c = xScale;
                d = 0;
                break;
            case 256:
                a = -xScale;
                b = 0;
                c = 0;
                d = -yScale;
                break;
            case 384:
                a = 0;
                b = yScale;
                c = -xScale;
                d = 0;
                break;
            default:
                a = (Sine[(angle + 128) & 0x1FF] * xScale) >> 15;
                b = -((Sine[angle] * yScale) >> 15);
                c = (Sine[angle] * xScale) >> 15;
                d = (Sine[(angle + 128) & 0x1FF] * yScale) >> 15;
                break;
        }

        var width = _memory[0x1F89] & ~7;
        var height = _memory[0x1F8C] & ~7;
        var outputLength = Math.Min(_memory.Length, (width + rowPadding / 4) * height / 2);
        Array.Clear(_memory, 0, outputLength);

        var centerX = unchecked((short)Read16(0x1F83));
        var centerY = unchecked((short)Read16(0x1F86));
        var lineX = (centerX << 12) - centerX * a - centerX * b;
        var lineY = (centerY << 12) - centerY * c - centerY * d;
        var output = 0;
        var bit = 0x80;

        for (var y = 0; y < height; y++)
        {
            var sourceX = lineX;
            var sourceY = lineY;
            for (var x = 0; x < width; x++)
            {
                var sourcePixelX = sourceX >> 12;
                var sourcePixelY = sourceY >> 12;
                byte pixel = 0;
                if (sourcePixelX >= 0 &&
                    sourcePixelY >= 0 &&
                    sourcePixelX < width &&
                    sourcePixelY < height)
                {
                    var packedIndex = sourcePixelY * width + sourcePixelX;
                    var sourceIndex = 0x600 + (packedIndex >> 1);
                    if (sourceIndex < _memory.Length)
                    {
                        pixel = _memory[sourceIndex];
                        if ((packedIndex & 1) != 0) pixel >>= 4;
                    }
                }

                SetPlanarPixel(output, bit, pixel);
                bit >>= 1;
                if (bit == 0)
                {
                    bit = 0x80;
                    output += 32;
                }

                sourceX += a;
                sourceY += c;
            }

            output += 2 + rowPadding;
            if ((output & 0x10) != 0)
            {
                output &= ~0x10;
            }
            else
            {
                output -= width * 4 + rowPadding;
            }

            lineX += b;
            lineY += d;
        }
    }

    private void SetPlanarPixel(int output, int bit, byte pixel)
    {
        if (output < 0 || output + 17 >= _memory.Length)
        {
            return;
        }

        if ((pixel & 1) != 0) _memory[output] |= (byte)bit;
        if ((pixel & 2) != 0) _memory[output + 1] |= (byte)bit;
        if ((pixel & 4) != 0) _memory[output + 16] |= (byte)bit;
        if ((pixel & 8) != 0) _memory[output + 17] |= (byte)bit;
    }

    private void TransformLineList()
    {
        var rotationX = _memory[0x1F83];
        var rotationY = _memory[0x1F86];
        var rotationZ = _memory[0x1F89];
        var scale = _memory[0x1F8C];
        var vertex = 0;

        for (var remaining = Read16(RegisterBase);
             remaining > 0 && vertex + 10 < MemorySize;
             remaining--, vertex += 16)
        {
            var x = unchecked((short)Read16(vertex + 1));
            var y = unchecked((short)Read16(vertex + 5));
            var z = unchecked((short)Read16(vertex + 9));
            TransformWirePoint(ref x, ref y, z, rotationX, rotationY, rotationZ, scale, perspective: true);
            Write16(vertex + 1, (ushort)(x + 0x80));
            Write16(vertex + 5, (ushort)(y + 0x50));
        }

        Write16(0x600, 23);
        Write16(0x602, 0x60);
        Write16(0x605, 0x40);
        Write16(0x608, 23);
        Write16(0x60A, 0x60);
        Write16(0x60D, 0x40);

        var pair = 0xB02;
        var output = 0x600;
        for (var remaining = Read16(0xB00);
             remaining > 0 && pair + 1 < MemorySize && output + 6 < MemorySize;
             remaining--, pair += 2, output += 8)
        {
            var first = _memory[pair] << 4;
            var second = _memory[pair + 1] << 4;
            if (first + 6 >= MemorySize || second + 6 >= MemorySize)
            {
                continue;
            }

            var x1 = unchecked((short)Read16(first + 1));
            var y1 = unchecked((short)Read16(first + 5));
            var x2 = unchecked((short)Read16(second + 1));
            var y2 = unchecked((short)Read16(second + 5));
            CalculateLineStep(x1, y1, x2, y2, out var distance, out var stepX, out var stepY);
            Write16(output, (ushort)Math.Max(1, distance));
            Write16(output + 2, (ushort)stepX);
            Write16(output + 5, (ushort)stepY);
        }
    }

    private void DrawWireFrame()
    {
        var lineSource = (uint)Read24(RegisterBase);
        var pointBank = _memory[0x1F82];
        var lineCount = _memory[0x295];

        for (var line = 0; line < lineCount; line++)
        {
            var lineOffset = line * 5;
            var p1High = _cartridge.ReadCx4RomByte(lineSource, lineOffset);
            var p1Low = _cartridge.ReadCx4RomByte(lineSource, lineOffset + 1);
            var p2High = _cartridge.ReadCx4RomByte(lineSource, lineOffset + 2);
            var p2Low = _cartridge.ReadCx4RomByte(lineSource, lineOffset + 3);
            var color = _cartridge.ReadCx4RomByte(lineSource, lineOffset + 4);

            if (p1High == 0xFF && p1Low == 0xFF)
            {
                var previous = line - 1;
                while (previous >= 0)
                {
                    var previousOffset = previous * 5;
                    var previousHigh = _cartridge.ReadCx4RomByte(lineSource, previousOffset + 2);
                    var previousLow = _cartridge.ReadCx4RomByte(lineSource, previousOffset + 3);
                    if (previousHigh != 0xFF || previousLow != 0xFF)
                    {
                        p1High = previousHigh;
                        p1Low = previousLow;
                        break;
                    }
                    previous--;
                }
            }

            var point1 = ((uint)pointBank << 16) | ((uint)p1High << 8) | p1Low;
            var point2 = ((uint)pointBank << 16) | ((uint)p2High << 8) | p2Low;
            DrawLine(
                ReadCx4BigEndianInt16(point1, 0),
                ReadCx4BigEndianInt16(point1, 2),
                ReadCx4BigEndianInt16(point1, 4),
                ReadCx4BigEndianInt16(point2, 0),
                ReadCx4BigEndianInt16(point2, 2),
                ReadCx4BigEndianInt16(point2, 4),
                color);
        }
    }

    private short ReadCx4BigEndianInt16(uint source, int displacement) =>
        unchecked((short)(
            (_cartridge.ReadCx4RomByte(source, displacement) << 8) |
            _cartridge.ReadCx4RomByte(source, displacement + 1)));

    private void DrawLine(
        short x1,
        short y1,
        short z1,
        short x2,
        short y2,
        short z2,
        byte color)
    {
        var rotationX = _memory[0x1F86];
        var rotationY = _memory[0x1F87];
        var rotationZ = _memory[0x1F88];
        var scale = _memory[0x1F90];

        TransformWirePoint(ref x1, ref y1, z1, rotationX, rotationY, rotationZ, scale, perspective: false);
        TransformWirePoint(ref x2, ref y2, z2, rotationX, rotationY, rotationZ, scale, perspective: false);
        var currentX = (x1 + 48) << 8;
        var currentY = (y1 + 48) << 8;
        CalculateLineStep(x1 + 48, y1 + 48, x2 + 48, y2 + 48, out var distance, out var stepX, out var stepY);

        for (var pixel = 0; pixel < Math.Max(1, distance); pixel++)
        {
            if (currentX > 0xFF && currentY > 0xFF && currentX < 0x6000 && currentY < 0x6000)
            {
                var x = currentX >> 8;
                var y = currentY >> 8;
                var address = ((y >> 3) * 0xC0) + ((x >> 3) * 16) + ((y & 7) * 2) + 0x300;
                if (address + 1 < MemorySize)
                {
                    var mask = (byte)(0x80 >> (x & 7));
                    _memory[address] &= (byte)~mask;
                    _memory[address + 1] &= (byte)~mask;
                    if ((color & 1) != 0) _memory[address] |= mask;
                    if ((color & 2) != 0) _memory[address + 1] |= mask;
                }
            }

            currentX += stepX;
            currentY += stepY;
        }
    }

    private static void CalculateLineStep(
        int x1,
        int y1,
        int x2,
        int y2,
        out int distance,
        out short stepX,
        out short stepY)
    {
        var deltaX = x2 - x1;
        var deltaY = y2 - y1;
        if (Math.Abs(deltaX) > Math.Abs(deltaY))
        {
            distance = Math.Abs(deltaX) + 1;
            stepY = deltaX == 0 ? (short)0 : (short)(256 * deltaY / Math.Abs(deltaX));
            stepX = (short)(deltaX < 0 ? -256 : 256);
        }
        else if (deltaY != 0)
        {
            distance = Math.Abs(deltaY) + 1;
            stepX = (short)(256 * deltaX / Math.Abs(deltaY));
            stepY = (short)(deltaY < 0 ? -256 : 256);
        }
        else
        {
            distance = 0;
            stepX = 0;
            stepY = 0;
        }
    }

    private static void TransformWirePoint(
        ref short x,
        ref short y,
        short zValue,
        int rotationX,
        int rotationY,
        int rotationZ,
        int scale,
        bool perspective)
    {
        var pointX = (double)x;
        var pointY = (double)y;
        var pointZ = perspective ? (double)zValue - 0x95 : zValue;

        Rotate(ref pointY, ref pointZ, -rotationX * Math.Tau / 128.0);
        RotatePair(ref pointX, ref pointZ, -rotationY * Math.Tau / 128.0, invertSecond: true);
        Rotate(ref pointX, ref pointY, -rotationZ * Math.Tau / 128.0);

        if (perspective)
        {
            var divisor = 0x90 * (pointZ + 0x95);
            if (Math.Abs(divisor) < double.Epsilon)
            {
                x = 0;
                y = 0;
                return;
            }
            x = (short)(pointX * scale / divisor * 0x95);
            y = (short)(pointY * scale / divisor * 0x95);
        }
        else
        {
            x = (short)(pointX * scale / 256.0);
            y = (short)(pointY * scale / 256.0);
        }
    }

    private static void Rotate(ref double first, ref double second, double angle)
    {
        var sine = Math.Sin(angle);
        var cosine = Math.Cos(angle);
        var nextFirst = first * cosine - second * sine;
        var nextSecond = first * sine + second * cosine;
        first = nextFirst;
        second = nextSecond;
    }

    private static void RotatePair(
        ref double first,
        ref double second,
        double angle,
        bool invertSecond)
    {
        var sine = Math.Sin(angle);
        var cosine = Math.Cos(angle);
        var nextFirst = first * cosine + second * sine;
        var nextSecond = first * -sine + second * cosine;
        first = nextFirst;
        second = invertSecond ? nextSecond : -nextSecond;
    }

    private void DrawWave()
    {
        var destination = 0;
        var wave = (int)_memory[0x1F83];
        ushort drawMask = 0xC0C0;
        ushort preserveMask = 0x3F3F;

        for (var columnGroup = 0; columnGroup < 16; columnGroup++)
        {
            DrawWaveHalf(ref destination, ref wave, ref drawMask, ref preserveMask, 0xA00);
            destination += 16;
            DrawWaveHalf(ref destination, ref wave, ref drawMask, ref preserveMask, 0xA10);
            destination += 16;
        }
    }

    private void DrawWaveHalf(
        ref int destination,
        ref int wave,
        ref ushort drawMask,
        ref ushort preserveMask,
        int patternBase)
    {
        do
        {
            var height = -unchecked((sbyte)_memory[0xB00 + wave]) - 16;
            for (var row = 0; row < 40; row++)
            {
                var offset = ((row / 8) * 0x200) + ((row & 7) * 2);
                var address = destination + offset;
                if (address + 1 >= MemorySize)
                {
                    continue;
                }

                var value = (ushort)(Read16(address) & preserveMask);
                if (height >= 0)
                {
                    value |= height < 8
                        ? (ushort)(drawMask & Read16(patternBase + height * 2))
                        : (ushort)(drawMask & 0xFF00);
                }
                Write16(address, value);
                height++;
            }

            wave = (wave + 1) & 0x7F;
            drawMask = (ushort)((drawMask >> 2) | (drawMask << 6));
            preserveMask = (ushort)((preserveMask >> 2) | (preserveMask << 6));
        }
        while (drawMask != 0xC0C0);
    }

    private void DisintegrateSprite()
    {
        var width = _memory[0x1F89];
        var height = _memory[0x1F8C];
        var centerX = unchecked((short)Read16(RegisterBase));
        var centerY = unchecked((short)Read16(RegisterBase + 3));
        var scaleX = unchecked((short)Read16(RegisterBase + 6));
        var scaleY = unchecked((short)Read16(RegisterBase + 15));
        var startX = -centerX * scaleX + (centerX << 8);
        var startY = -centerY * scaleY + (centerY << 8);
        var source = 0x600;
        Array.Clear(_memory, 0, Math.Min(MemorySize, width * height / 2));

        var sourceY = startY;
        for (var row = 0; row < height; row++, sourceY += scaleY)
        {
            var sourceX = startX;
            for (var column = 0; column < width; column++, sourceX += scaleX)
            {
                if (source >= MemorySize)
                {
                    return;
                }

                var pixel = (column & 1) == 0 ? _memory[source] : (byte)(_memory[source] >> 4);
                var x = sourceX >> 8;
                var y = sourceY >> 8;
                if (x >= 0 && y >= 0 && x < width && y < height)
                {
                    var output = ((y >> 3) * width * 4) + ((x >> 3) * 32) + ((y & 7) * 2);
                    SetPlanarPixel(output, 0x80 >> (x & 7), pixel);
                }

                if ((column & 1) != 0)
                {
                    source++;
                }
            }
        }
    }

    private void CalculatePropulsion()
    {
        var result = 0x10000;
        var denominator = Read16(RegisterBase + 3);
        if (denominator != 0)
        {
            result = ((result / denominator) * Read16(RegisterBase + 1)) >> 8;
        }
        Write16(RegisterBase, (ushort)result);
    }

    private void SetVectorLength()
    {
        var x = unchecked((short)Read16(RegisterBase));
        var y = unchecked((short)Read16(RegisterBase + 3));
        var requestedLength = unchecked((short)Read16(RegisterBase + 6));
        var currentLength = Math.Sqrt((double)x * x + (double)y * y);
        if (currentLength == 0)
        {
            Write16(RegisterBase + 9, 0);
            Write16(RegisterBase + 12, 0);
            return;
        }

        var ratio = requestedLength / currentLength;
        Write16(RegisterBase + 9, (ushort)(short)(x * ratio * 0.98));
        Write16(RegisterBase + 12, (ushort)(short)(y * ratio * 0.99));
    }

    private void PolarToCartesian(bool compactResult)
    {
        var angle = Read16(RegisterBase) & 0x1FF;
        var radius = compactResult
            ? unchecked((short)Read16(RegisterBase + 3))
            : (int)Read16(RegisterBase + 3);
        var shift = compactResult ? 16 : 8;
        var x = (radius * Sine[(angle + 128) & 0x1FF] * 2) >> shift;
        var y = (radius * Sine[angle] * 2) >> shift;
        if (compactResult)
        {
            y -= y >> 6;
        }
        Write24(RegisterBase + 6, x);
        Write24(RegisterBase + 9, y);
    }

    private void CalculateVectorLength()
    {
        var x = unchecked((short)Read16(RegisterBase));
        var y = unchecked((short)Read16(RegisterBase + 3));
        Write16(RegisterBase, (ushort)(short)Math.Sqrt((double)x * x + (double)y * y));
    }

    private void CalculateAngle()
    {
        var x = unchecked((short)Read16(RegisterBase));
        var y = unchecked((short)Read16(RegisterBase + 3));
        int angle;
        if (x == 0)
        {
            angle = y > 0 ? 0x80 : 0x180;
        }
        else
        {
            angle = (int)(Math.Atan((double)y / x) / Math.Tau * 512);
            if (x < 0) angle += 0x100;
            angle &= 0x1FF;
        }
        Write16(RegisterBase + 6, (ushort)angle);
    }

    private void BuildTrapezoidEdges()
    {
        var firstAngle = Read16(RegisterBase + 12) & 0x1FF;
        var secondAngle = Read16(RegisterBase + 15) & 0x1FF;
        var firstCosine = Sine[(firstAngle + 128) & 0x1FF];
        var secondCosine = Sine[(secondAngle + 128) & 0x1FF];
        var firstTangent = firstCosine == 0 ? int.MinValue : (Sine[firstAngle] << 16) / firstCosine;
        var secondTangent = secondCosine == 0 ? int.MinValue : (Sine[secondAngle] << 16) / secondCosine;
        var y = unchecked((short)(Read16(RegisterBase + 3) - Read16(RegisterBase + 9)));

        for (var row = 0; row < 225; row++, y++)
        {
            var left = 1;
            var right = 0;
            if (y >= 0)
            {
                left = (int)(((long)firstTangent * y) >> 16) -
                       Read16(RegisterBase) +
                       Read16(RegisterBase + 6);
                right = (int)(((long)secondTangent * y) >> 16) -
                        Read16(RegisterBase) +
                        Read16(RegisterBase + 6) +
                        Read16(RegisterBase + 19);

                if (left < 0 && right < 0)
                {
                    left = 1;
                    right = 0;
                }
                else
                {
                    left = Math.Clamp(left, 0, 255);
                    right = Math.Clamp(right, 0, 255);
                    if (left == 255 && right == 255) right = 254;
                }
            }

            _memory[0x800 + row] = (byte)left;
            _memory[0x900 + row] = (byte)right;
        }
    }

    private void Multiply()
    {
        var left = SignExtend24(Read24(RegisterBase));
        var right = SignExtend24(Read24(RegisterBase + 3));
        var product = (long)left * right;
        Write24(RegisterBase, (int)product);
        Write24(RegisterBase + 3, (int)(product >> 24));
    }

    private void TransformSingleCoordinate()
    {
        var x = unchecked((short)Read16(RegisterBase + 1));
        var y = unchecked((short)Read16(RegisterBase + 4));
        var z = unchecked((short)Read16(RegisterBase + 7));
        TransformWirePoint(
            ref x,
            ref y,
            z,
            _memory[RegisterBase + 9],
            _memory[RegisterBase + 10],
            _memory[RegisterBase + 11],
            Read16(RegisterBase + 16),
            perspective: false);
        Write16(RegisterBase, (ushort)x);
        Write16(RegisterBase + 3, (ushort)y);
    }

    private void SumRam()
    {
        var sum = 0;
        for (var index = 0; index < 0x800; index++)
        {
            sum = (sum + _memory[index]) & 0xFFFFFF;
        }
        Write24(RegisterBase, sum);
    }

    private void Square()
    {
        var value = SignExtend24(Read24(RegisterBase));
        var result = (long)value * value;
        Write24(RegisterBase + 3, (int)result);
        Write24(RegisterBase + 6, (int)(result >> 24));
    }

    private void EmitImmediatePattern(int length, int destination)
    {
        destination &= MemorySize - 1;
        for (var index = 0; index < length; index++)
        {
            _memory[(destination + index) & (MemorySize - 1)] =
                ImmediateSource[ImmediateSource.Length - 1 - index];
        }
        Write24(RegisterBase, destination + length);
    }

    private static int SignExtend24(int value) =>
        (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;

    private ushort Read16(int index) =>
        (ushort)(_memory[index] | (_memory[index + 1] << 8));

    private int Read24(int index) =>
        _memory[index] | (_memory[index + 1] << 8) | (_memory[index + 2] << 16);

    private void Write16(int index, ushort value)
    {
        _memory[index] = (byte)value;
        _memory[index + 1] = (byte)(value >> 8);
    }

    private void Write24(int index, int value)
    {
        _memory[index] = (byte)value;
        _memory[index + 1] = (byte)(value >> 8);
        _memory[index + 2] = (byte)(value >> 16);
    }

    private static short[] CreateSineTable()
    {
        var result = new short[512];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = (short)Math.Round(
                Math.Sin(index * Math.Tau / result.Length) * short.MaxValue,
                MidpointRounding.AwayFromZero);
        }
        return result;
    }
}
