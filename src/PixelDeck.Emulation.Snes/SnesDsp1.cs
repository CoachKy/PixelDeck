namespace PixelDeck.Emulation.Snes;

/// <summary>
/// High-level implementation of the cartridge DSP-1 command interface.
///
/// The real part is a NEC uPD77C25 running Nintendo firmware. PixelSNES keeps
/// the firmware out of the application and implements the documented command
/// surface directly. Calculations use a wider host representation and are
/// quantized at the cartridge boundary.
/// </summary>
internal sealed class SnesDsp1
{
    private readonly byte[] _parameters = new byte[16];
    private readonly byte[] _output = new byte[2_048];
    private readonly long[] _commandCounts = new long[256];
    private readonly double[][,] _matrices =
    [
        new double[3, 3],
        new double[3, 3],
        new double[3, 3]
    ];

    private byte _command;
    private int _parameterCount;
    private int _parameterIndex;
    private int _outputCount;
    private int _outputIndex;
    private bool _waitingForCommand = true;
    private short _rasterLine;

    private double _sinAzimuth;
    private double _cosAzimuth = 1.0;
    private double _sinZenith;
    private double _cosZenith = 1.0;
    private double _centreX;
    private double _centreY;
    private double _centreZ;
    private double _eyeX;
    private double _eyeY;
    private double _eyeZ;
    private double _screenDistance = 1.0;
    private double _verticalOffset;

    public byte ReadData()
    {
        if (_outputCount == 0)
        {
            return 0x80;
        }

        var value = _output[_outputIndex++];
        _outputCount--;
        if (_outputCount == 0 && IsRasterCommand(_command))
        {
            ExecuteRaster();
        }

        return value;
    }

    public byte ReadStatus() => 0x80;

    public byte PeekData() => _outputCount == 0 ? (byte)0x80 : _output[_outputIndex];

    public void WriteData(byte value)
    {
        // During the DSP-1's streaming raster command, writes acknowledge one
        // pending result byte. Games use this to terminate an old stream before
        // issuing another command.
        if (IsRasterCommand(_command) && _outputCount != 0)
        {
            _outputIndex++;
            _outputCount--;
            return;
        }

        if (_waitingForCommand)
        {
            if (value == 0x80)
            {
                return;
            }

            _command = CanonicalCommand(value);
            _parameterCount = GetParameterWordCount(_command) * 2;
            _parameterIndex = 0;
            _outputIndex = 0;
            _outputCount = 0;
            _waitingForCommand = _parameterCount == 0;
            if (_waitingForCommand)
            {
                ExecuteCommand();
            }

            return;
        }

        _parameters[_parameterIndex++] = value;
        if (_parameterIndex < _parameterCount)
        {
            return;
        }

        _waitingForCommand = true;
        ExecuteCommand();
    }

    internal long GetCommandExecutionCount(byte command) =>
        _commandCounts[command];

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(_parameters);
        writer.Write(_output);
        writer.Write(_command);
        writer.Write(_parameterCount);
        writer.Write(_parameterIndex);
        writer.Write(_outputCount);
        writer.Write(_outputIndex);
        writer.Write(_waitingForCommand);
        writer.Write(_rasterLine);
        writer.Write(_sinAzimuth);
        writer.Write(_cosAzimuth);
        writer.Write(_sinZenith);
        writer.Write(_cosZenith);
        writer.Write(_centreX);
        writer.Write(_centreY);
        writer.Write(_centreZ);
        writer.Write(_eyeX);
        writer.Write(_eyeY);
        writer.Write(_eyeZ);
        writer.Write(_screenDistance);
        writer.Write(_verticalOffset);
        foreach (var count in _commandCounts) writer.Write(count);
        foreach (var matrix in _matrices)
        {
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    writer.Write(matrix[row, column]);
                }
            }
        }
    }

    public void LoadState(BinaryReader reader)
    {
        reader.ReadExactly(_parameters);
        reader.ReadExactly(_output);
        _command = reader.ReadByte();
        _parameterCount = reader.ReadInt32();
        _parameterIndex = reader.ReadInt32();
        _outputCount = reader.ReadInt32();
        _outputIndex = reader.ReadInt32();
        _waitingForCommand = reader.ReadBoolean();
        _rasterLine = reader.ReadInt16();
        _sinAzimuth = reader.ReadDouble();
        _cosAzimuth = reader.ReadDouble();
        _sinZenith = reader.ReadDouble();
        _cosZenith = reader.ReadDouble();
        _centreX = reader.ReadDouble();
        _centreY = reader.ReadDouble();
        _centreZ = reader.ReadDouble();
        _eyeX = reader.ReadDouble();
        _eyeY = reader.ReadDouble();
        _eyeZ = reader.ReadDouble();
        _screenDistance = reader.ReadDouble();
        _verticalOffset = reader.ReadDouble();
        for (var index = 0; index < _commandCounts.Length; index++)
        {
            _commandCounts[index] = reader.ReadInt64();
        }

        foreach (var matrix in _matrices)
        {
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    matrix[row, column] = reader.ReadDouble();
                }
            }
        }
    }

    private void ExecuteCommand()
    {
        _commandCounts[_command]++;
        _outputIndex = 0;
        _outputCount = 0;

        switch (_command)
        {
            case 0x00:
            case 0x20:
            {
                var result = ((int)Parameter(0) * Parameter(1)) >> 15;
                if (_command == 0x20) result++;
                WriteResults(ToInt16(result));
                break;
            }
            case 0x10:
                ExecuteInverse();
                break;
            case 0x04:
            {
                var angle = Angle(Parameter(0));
                var radius = Parameter(1);
                WriteResults(
                    Quantize(Math.Sin(angle) * radius),
                    Quantize(Math.Cos(angle) * radius));
                break;
            }
            case 0x08:
            {
                var x = (long)Parameter(0);
                var y = (long)Parameter(1);
                var z = (long)Parameter(2);
                var radiusSquared = (x * x + y * y + z * z) << 1;
                WriteResults(
                    unchecked((short)radiusSquared),
                    unchecked((short)(radiusSquared >> 16)));
                break;
            }
            case 0x18:
            case 0x38:
            {
                var x = (long)Parameter(0);
                var y = (long)Parameter(1);
                var z = (long)Parameter(2);
                var radius = (long)Parameter(3);
                var result = ((x * x + y * y + z * z - radius * radius) >> 15) +
                             (_command == 0x38 ? 1 : 0);
                WriteResults(ToInt16(result));
                break;
            }
            case 0x28:
            {
                var x = (double)Parameter(0);
                var y = Parameter(1);
                var z = Parameter(2);
                WriteResults(Quantize(Math.Sqrt((x * x) + (y * y) + (z * z))));
                break;
            }
            case 0x0C:
                ExecuteRotate2D();
                break;
            case 0x1C:
                ExecuteRotate3D();
                break;
            case 0x02:
                ExecuteProjectionParameters();
                break;
            case 0x0A:
                _rasterLine = Parameter(0);
                ExecuteRaster();
                break;
            case 0x06:
                ExecuteProject();
                break;
            case 0x0E:
                ExecuteTarget();
                break;
            case 0x01:
            case 0x11:
            case 0x21:
                SetAttitudeMatrix((_command >> 4) & 3);
                break;
            case 0x0D:
            case 0x1D:
            case 0x2D:
                TransformVector((_command >> 4) & 3, transpose: false);
                break;
            case 0x03:
            case 0x13:
            case 0x23:
                TransformVector((_command >> 4) & 3, transpose: true);
                break;
            case 0x0B:
            case 0x1B:
            case 0x2B:
                ExecuteScalar((_command >> 4) & 3);
                break;
            case 0x14:
                ExecuteGyrate();
                break;
            case 0x0F:
                WriteResults(0);
                break;
            case 0x2F:
                WriteResults(0x0100);
                break;
            case 0x1F:
                Array.Clear(_output);
                _outputCount = _output.Length;
                break;
        }
    }

    private void ExecuteInverse()
    {
        var coefficient = Parameter(0);
        var exponent = Parameter(1);
        if (coefficient == 0)
        {
            WriteResults(0x7FFF, 0x002F);
            return;
        }

        var value = (coefficient / 32768.0) * Math.Pow(2.0, exponent);
        var inverse = 1.0 / value;
        var outputExponent = 0;
        var outputCoefficient = inverse;
        while (Math.Abs(outputCoefficient) < 0.5 && outputCoefficient != 0)
        {
            outputCoefficient *= 2.0;
            outputExponent--;
        }
        while (Math.Abs(outputCoefficient) >= 1.0)
        {
            outputCoefficient *= 0.5;
            outputExponent++;
        }

        WriteResults(
            Quantize(outputCoefficient * 32768.0),
            ToInt16(outputExponent));
    }

    private void ExecuteRotate2D()
    {
        var angle = Angle(Parameter(0));
        var x = Parameter(1);
        var y = Parameter(2);
        var sine = Math.Sin(angle);
        var cosine = Math.Cos(angle);
        WriteResults(
            Quantize((y * sine) + (x * cosine)),
            Quantize((y * cosine) - (x * sine)));
    }

    private void ExecuteRotate3D()
    {
        var zAngle = Angle(Parameter(0));
        var yAngle = Angle(Parameter(1));
        var xAngle = Angle(Parameter(2));
        var vector = new[]
        {
            (double)Parameter(3),
            (double)Parameter(4),
            (double)Parameter(5)
        };

        vector = RotateZ(vector, zAngle);
        vector = RotateY(vector, yAngle);
        vector = RotateX(vector, xAngle);
        WriteResults(
            Quantize(vector[0]),
            Quantize(vector[1]),
            Quantize(vector[2]));
    }

    private void ExecuteProjectionParameters()
    {
        var focalX = Parameter(0);
        var focalY = Parameter(1);
        var focalZ = Parameter(2);
        var focalToEye = Parameter(3);
        _screenDistance = Parameter(4);
        if (Math.Abs(_screenDistance) < 1.0) _screenDistance = 1.0;

        var azimuth = Angle(Parameter(5));
        var zenith = Angle(Parameter(6));
        _sinAzimuth = Math.Sin(azimuth);
        _cosAzimuth = Math.Cos(azimuth);
        _sinZenith = Math.Sin(zenith);
        _cosZenith = Math.Cos(zenith);

        var normalX = -_sinZenith * _sinAzimuth;
        var normalY = _sinZenith * _cosAzimuth;
        var normalZ = _cosZenith;
        _centreX = focalX + (focalToEye * normalX);
        _centreY = focalY + (focalToEye * normalY);
        _centreZ = focalZ + (focalToEye * normalZ);
        _eyeX = _centreX - (_screenDistance * normalX);
        _eyeY = _centreY - (_screenDistance * normalY);
        _eyeZ = _centreZ - (_screenDistance * normalZ);
        _verticalOffset = _screenDistance * _cosZenith;

        var verticalVanishing = Math.Abs(_sinZenith) < 1e-9
            ? 0.0
            : -_verticalOffset / _sinZenith;
        WriteResults(
            0,
            Quantize(verticalVanishing),
            Quantize(_centreX),
            Quantize(_centreY));
    }

    private void ExecuteRaster()
    {
        var denominator = (_rasterLine * _sinZenith) + _verticalOffset;
        var scale = Math.Abs(denominator) < 1e-9
            ? 0.0
            : (_centreZ * 256.0) / denominator;
        var verticalScale = Math.Abs(_cosZenith) < 1e-9
            ? scale
            : scale / _cosZenith;

        WriteResults(
            Quantize(scale * _cosAzimuth),
            Quantize(-verticalScale * _sinAzimuth),
            Quantize(scale * _sinAzimuth),
            Quantize(verticalScale * _cosAzimuth));
        _rasterLine++;
    }

    private void ExecuteProject()
    {
        var x = Parameter(0) - _eyeX;
        var y = Parameter(1) - _eyeY;
        var z = Parameter(2) - _eyeZ;
        var normalX = -_sinZenith * _sinAzimuth;
        var normalY = _sinZenith * _cosAzimuth;
        var normalZ = _cosZenith;
        var depth = (x * normalX) + (y * normalY) + (z * normalZ);
        var scale = Math.Abs(depth) < 1e-9 ? 0.0 : _screenDistance / depth;
        var horizontal = (x * _cosAzimuth) + (y * _sinAzimuth);
        var vertical =
            (x * -_cosZenith * _sinAzimuth) +
            (y * _cosZenith * _cosAzimuth) -
            (z * _sinZenith);
        WriteResults(
            Quantize(horizontal * scale),
            Quantize(vertical * scale),
            Quantize(scale * 256.0));
    }

    private void ExecuteTarget()
    {
        var horizontal = Parameter(0);
        var vertical = Parameter(1);
        var rightX = _cosAzimuth;
        var rightY = _sinAzimuth;
        var upX = -_cosZenith * _sinAzimuth;
        var upY = _cosZenith * _cosAzimuth;
        var upZ = -_sinZenith;
        var normalX = -_sinZenith * _sinAzimuth;
        var normalY = _sinZenith * _cosAzimuth;
        var normalZ = _cosZenith;

        var rayX = (_screenDistance * normalX) + (horizontal * rightX) + (vertical * upX);
        var rayY = (_screenDistance * normalY) + (horizontal * rightY) + (vertical * upY);
        var rayZ = (_screenDistance * normalZ) + (vertical * upZ);
        var amount = Math.Abs(rayZ) < 1e-9 ? 0.0 : -_eyeZ / rayZ;
        WriteResults(
            Quantize(_eyeX + (rayX * amount)),
            Quantize(_eyeY + (rayY * amount)));
    }

    private void SetAttitudeMatrix(int matrixIndex)
    {
        var scale = Parameter(0) / 65536.0;
        var zAngle = Angle(Parameter(1));
        var yAngle = Angle(Parameter(2));
        var xAngle = Angle(Parameter(3));
        var sineZ = Math.Sin(zAngle);
        var cosineZ = Math.Cos(zAngle);
        var sineY = Math.Sin(yAngle);
        var cosineY = Math.Cos(yAngle);
        var sineX = Math.Sin(xAngle);
        var cosineX = Math.Cos(xAngle);
        var matrix = _matrices[matrixIndex];

        matrix[0, 0] = scale * cosineZ * cosineY;
        matrix[0, 1] = -scale * sineZ * cosineY;
        matrix[0, 2] = scale * sineY;
        matrix[1, 0] = scale * ((sineZ * cosineX) + (cosineZ * sineX * sineY));
        matrix[1, 1] = scale * ((cosineZ * cosineX) - (sineZ * sineX * sineY));
        matrix[1, 2] = -scale * sineX * cosineY;
        matrix[2, 0] = scale * ((sineZ * sineX) - (cosineZ * cosineX * sineY));
        matrix[2, 1] = scale * ((cosineZ * sineX) + (sineZ * cosineX * sineY));
        matrix[2, 2] = scale * cosineX * cosineY;
    }

    private void TransformVector(int matrixIndex, bool transpose)
    {
        var matrix = _matrices[matrixIndex];
        var input = new[]
        {
            (double)Parameter(0),
            (double)Parameter(1),
            (double)Parameter(2)
        };
        var output = new double[3];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                output[row] += input[column] *
                               (transpose ? matrix[column, row] : matrix[row, column]);
            }
        }

        WriteResults(
            Quantize(output[0]),
            Quantize(output[1]),
            Quantize(output[2]));
    }

    private void ExecuteScalar(int matrixIndex)
    {
        var matrix = _matrices[matrixIndex];
        var scalar =
            (Parameter(0) * matrix[0, 0]) +
            (Parameter(1) * matrix[0, 1]) +
            (Parameter(2) * matrix[0, 2]);
        WriteResults(Quantize(scalar));
    }

    private void ExecuteGyrate()
    {
        var z = Angle(Parameter(0));
        var x = Angle(Parameter(1));
        var y = Angle(Parameter(2));
        var up = Parameter(3) / 32768.0;
        var forward = Parameter(4) / 32768.0;
        var left = Parameter(5);
        var cosineX = Math.Cos(x);
        if (Math.Abs(cosineX) < 1e-9) cosineX = 1e-9;

        var nextZ = z + ((up * Math.Cos(y) - forward * Math.Sin(y)) / cosineX);
        var nextX = x + (up * Math.Sin(y)) + (forward * Math.Cos(y));
        var nextY = y -
                    ((up * Math.Cos(y) + forward * Math.Sin(y)) *
                     (Math.Sin(x) / cosineX)) +
                    Angle(ToInt16(left));
        WriteResults(
            QuantizeAngle(nextZ),
            QuantizeAngle(nextX),
            QuantizeAngle(nextY));
    }

    private void WriteResults(short first)
    {
        BeginResults(1);
        WriteResult(0, first);
    }

    private void WriteResults(short first, short second)
    {
        BeginResults(2);
        WriteResult(0, first);
        WriteResult(1, second);
    }

    private void WriteResults(short first, short second, short third)
    {
        BeginResults(3);
        WriteResult(0, first);
        WriteResult(1, second);
        WriteResult(2, third);
    }

    private void WriteResults(short first, short second, short third, short fourth)
    {
        BeginResults(4);
        WriteResult(0, first);
        WriteResult(1, second);
        WriteResult(2, third);
        WriteResult(3, fourth);
    }

    private void BeginResults(int wordCount)
    {
        _outputIndex = 0;
        _outputCount = wordCount * 2;
    }

    private void WriteResult(int index, short result)
    {
        var value = unchecked((ushort)result);
        _output[index * 2] = (byte)value;
        _output[(index * 2) + 1] = (byte)(value >> 8);
    }

    private short Parameter(int wordIndex) =>
        unchecked((short)(_parameters[wordIndex * 2] | (_parameters[(wordIndex * 2) + 1] << 8)));

    private static double Angle(short value) => value * Math.PI / 32768.0;

    private static short Quantize(double value)
    {
        if (double.IsNaN(value)) return 0;
        return ToInt16((long)Math.Round(value, MidpointRounding.AwayFromZero));
    }

    private static short QuantizeAngle(double radians) =>
        unchecked((short)(long)Math.Round(
            radians * 32768.0 / Math.PI,
            MidpointRounding.AwayFromZero));

    private static short ToInt16(long value) => value switch
    {
        > short.MaxValue => short.MaxValue,
        < short.MinValue => short.MinValue,
        _ => (short)value
    };

    private static double[] RotateX(double[] vector, double angle)
    {
        var sine = Math.Sin(angle);
        var cosine = Math.Cos(angle);
        return [vector[0], (vector[1] * cosine) - (vector[2] * sine), (vector[1] * sine) + (vector[2] * cosine)];
    }

    private static double[] RotateY(double[] vector, double angle)
    {
        var sine = Math.Sin(angle);
        var cosine = Math.Cos(angle);
        return [(vector[0] * cosine) + (vector[2] * sine), vector[1], (-vector[0] * sine) + (vector[2] * cosine)];
    }

    private static double[] RotateZ(double[] vector, double angle)
    {
        var sine = Math.Sin(angle);
        var cosine = Math.Cos(angle);
        return [(vector[0] * cosine) + (vector[1] * sine), (vector[1] * cosine) - (vector[0] * sine), vector[2]];
    }

    private static bool IsRasterCommand(byte command) => command == 0x0A;

    private static byte CanonicalCommand(byte command) => command switch
    {
        0x30 => 0x10,
        0x24 => 0x04,
        0x2C => 0x0C,
        0x3C => 0x1C,
        0x12 or 0x22 or 0x32 => 0x02,
        0x1A or 0x2A or 0x3A => 0x0A,
        0x16 or 0x26 or 0x36 => 0x06,
        0x1E or 0x2E or 0x3E => 0x0E,
        0x05 or 0x31 or 0x35 => 0x01,
        0x15 => 0x11,
        0x25 => 0x21,
        0x09 or 0x39 or 0x3D => 0x0D,
        0x19 => 0x1D,
        0x29 => 0x2D,
        0x33 => 0x03,
        0x3B => 0x0B,
        0x34 => 0x14,
        0x07 => 0x0F,
        0x27 => 0x2F,
        0x17 or 0x37 or 0x3F => 0x1F,
        _ => command
    };

    private static int GetParameterWordCount(byte command) => command switch
    {
        0x00 or 0x10 or 0x20 or 0x04 => 2,
        0x08 or 0x28 or 0x0C or 0x06 or 0x0D or 0x1D or 0x2D or
            0x03 or 0x13 or 0x23 or 0x0B or 0x1B or 0x2B => 3,
        0x18 or 0x38 or 0x01 or 0x11 or 0x21 => 4,
        0x1C or 0x14 => 6,
        0x02 => 7,
        0x0A or 0x0F or 0x2F or 0x1F => 1,
        0x0E => 2,
        _ => 0
    };
}
