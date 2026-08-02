namespace PixelDeck.Emulation.N64;

public sealed partial class Fast3dRenderer
{
    private List<N64RdpCommand>? _capturedRdpCommands;
    private int _omittedHlePrimitiveCommands;
    private long _unsupportedCommandsAtCaptureStart;

    internal void BeginRdpTraceCapture()
    {
        if (_capturedRdpCommands is not null)
        {
            throw new InvalidOperationException("RDP trace capture is already active.");
        }

        _capturedRdpCommands = [];
        _omittedHlePrimitiveCommands = 0;
        _unsupportedCommandsAtCaptureStart = UnsupportedCommands;
    }

    internal N64RdpTrace EndRdpTraceCapture(ReadOnlySpan<byte> initialRdram)
    {
        var batch = EndRdpCommandBatchCapture();
        return new N64RdpTrace(
            initialRdram,
            batch.Commands,
            DetectedMicrocodeName,
            batch.OmittedHlePrimitiveCommands,
            batch.UnsupportedSourceCommands);
    }

    internal N64RdpCommandBatch EndRdpCommandBatchCapture()
    {
        var commands = _capturedRdpCommands ??
            throw new InvalidOperationException("RDP trace capture is not active.");
        _capturedRdpCommands = null;
        return new N64RdpCommandBatch(
            commands,
            _omittedHlePrimitiveCommands,
            UnsupportedCommands - _unsupportedCommandsAtCaptureStart);
    }

    internal void ReplayRdpCommands(IReadOnlyList<N64RdpCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (_capturedRdpCommands is not null)
        {
            throw new InvalidOperationException("Cannot replay while RDP capture is active.");
        }

        foreach (var command in commands)
        {
            CommandsProcessed++;
            _opcodeHistogram[command.Opcode]++;
            ExecuteRdpCommand(command);
        }
    }

    private void RecordOmittedHlePrimitives(int count)
    {
        if (_capturedRdpCommands is not null)
        {
            _omittedHlePrimitiveCommands = checked(_omittedHlePrimitiveCommands + count);
        }
    }

    private void CaptureHleTriangle(
        Fast3dVertex first,
        Fast3dVertex second,
        Fast3dVertex third)
    {
        if (_capturedRdpCommands is null)
        {
            return;
        }

        // Match paraLLEl-RDP's converter: scale reciprocal W so the largest
        // value remains just below 0.5 and fits the RDP's signed fixed-point
        // interpolation range.
        var minimumW = MathF.Min(
            first.ClipPosition.W,
            MathF.Min(second.ClipPosition.W, third.ClipPosition.W));
        if (!float.IsFinite(minimumW) || minimumW <= 0.000001f)
        {
            RecordOmittedHlePrimitives(1);
            return;
        }

        var reciprocalScale = minimumW * 0.49f;
        if (N64RdpTriangleEncoder.TryEncode(
                CreateRdpVertex(first, reciprocalScale),
                CreateRdpVertex(second, reciprocalScale),
                CreateRdpVertex(third, reciprocalScale),
                _textureTile,
                _textureMaximumMipLevel,
                out var command) &&
            command is not null)
        {
            _capturedRdpCommands.Add(command);
            return;
        }

        // Degenerate triangles are discarded by both the HLE rasterizer and
        // native RDP setup, so they do not make a trace incomplete.
    }

    private N64RdpHleVertex CreateRdpVertex(
        Fast3dVertex vertex,
        float reciprocalScale)
    {
        var scaledReciprocalW = vertex.ReciprocalW * reciprocalScale;
        var normalizedDepth = Math.Abs(_viewportScale.Z) > 0.000001f
            ? (vertex.Position.Z - _viewportTranslate.Z) / _viewportScale.Z
            : 0;
        return new N64RdpHleVertex(
            vertex.Position.X,
            vertex.Position.Y,
            Math.Clamp(normalizedDepth, 0, 1),
            vertex.TextureCoordinate.X * scaledReciprocalW,
            vertex.TextureCoordinate.Y * scaledReciprocalW,
            scaledReciprocalW,
            vertex.Color);
    }

    private void CaptureCanonicalOtherMode()
    {
        _capturedRdpCommands?.Add(
            new N64RdpCommand(0xEF000000 | (_otherModeHigh & 0x00FFFFFF), _otherModeLow));
    }

    private void CaptureAndExecuteRdpCommand(params uint[] words)
    {
        var opcode = (byte)(words[0] >> 24);
        if (opcode is 0xFD or 0xFE or 0xFF)
        {
            words[1] = ResolveAddress(words[1]);
        }

        var command = new N64RdpCommand(words);
        _capturedRdpCommands?.Add(command);
        ExecuteRdpCommand(command);
    }

    private void ExecuteRdpCommand(N64RdpCommand command)
    {
        var words = command.Words.Span;
        var word0 = words[0];
        var word1 = words[1];
        switch (command.Opcode)
        {
            case 0xF6: // G_FILLRECT
                FillRectangle(word0, word1);
                break;
            case 0xF7: // G_SETFILLCOLOR
                _fillColor = word1;
                break;
            case 0xF2: // G_SETTILESIZE
                SetTileSize(word0, word1);
                break;
            case 0xF3: // G_LOADBLOCK
                LoadTextureBlock(word0, word1);
                break;
            case 0xF5: // G_SETTILE
                SetTile(word0, word1);
                break;
            case 0xFD: // G_SETTIMG
                SetTextureImage(word0, word1);
                break;
            case 0xFC: // G_SETCOMBINE
                DecodeCombiner(word0, word1);
                break;
            case 0xFE: // G_SETZIMG
                _depthImageAddress = ResolveAddress(word1);
                break;
            case 0xFF: // G_SETCIMG
                _colorImageSize = (int)((word0 >> 19) & 3);
                _colorImageWidth = (int)(word0 & 0xFFF) + 1;
                _colorImageAddress = ResolveAddress(word1);
                break;
            case 0xF0: // G_LOADTLUT
                LoadTextureLookupTable(word0, word1);
                break;
            case 0xF4: // G_LOADTILE
                LoadTextureTile(word0, word1);
                break;
            case 0xEF: // G_RDPSETOTHERMODE
                _otherModeHigh = word0 & 0x00FFFFFF;
                _otherModeLow = word1;
                UpdateCombinerTextureUsage();
                break;
            case 0xED: // G_SETSCISSOR
                _scissorLeft = (int)((word0 >> 12) & 0xFFF) / 4;
                _scissorTop = (int)(word0 & 0xFFF) / 4;
                _scissorRight = (int)((word1 >> 12) & 0xFFF) / 4;
                _scissorBottom = (int)(word1 & 0xFFF) / 4;
                break;
            case 0xFB: // G_SETENVCOLOR
                _environmentColor = DecodeRgba32(word1);
                break;
            case 0xF8: // G_SETFOGCOLOR
                _fogColor = DecodeRgba32(word1);
                break;
            case 0xF9: // G_SETBLENDCOLOR
                _blendColor = DecodeRgba32(word1);
                break;
            case 0xFA: // G_SETPRIMCOLOR
                // word0 carries the LOD level in bits 15-8 and the LOD
                // fraction in bits 7-0; the fraction is a combiner source.
                _primitiveLodFraction = (word0 & 0xFF) / 255f;
                _primitiveColor = DecodeRgba32(word1);
                break;
            case 0xE4: // G_TEXRECT
            case 0xE5: // G_TEXRECTFLIP
                if (words.Length != 4)
                {
                    throw new InvalidDataException(
                        "Texture-rectangle RDP commands require four words.");
                }

                DrawTextureRectangle(
                    word0,
                    word1,
                    words[2],
                    words[3],
                    command.Opcode == 0xE5);
                break;
            case 0xE6: // G_RDPLOADSYNC
            case 0xE7: // G_RDPPIPESYNC
            case 0xE8: // G_RDPTILESYNC
            case 0xE9: // G_RDPFULLSYNC
                break;
            case 0xEA: // G_SETKEYGB
                _keyGreenBlueWord0 = word0;
                _keyGreenBlueWord1 = word1;
                break;
            case 0xEB: // G_SETKEYR
                _keyRedWord1 = word1;
                break;
            case 0xEC: // G_SETCONVERT
                _convertWord0 = word0;
                _convertWord1 = word1;
                break;
            case 0xEE: // G_SETPRIMDEPTH
                _primitiveDepth = (ushort)(word1 >> 16);
                _primitiveDeltaDepth = (ushort)word1;
                break;
            default:
                UnsupportedCommands++;
                _unsupportedCommandCounts[command.Opcode] =
                    _unsupportedCommandCounts.GetValueOrDefault(command.Opcode) + 1;
                break;
        }
    }
}

internal sealed record N64RdpCommandBatch(
    IReadOnlyList<N64RdpCommand> Commands,
    int OmittedHlePrimitiveCommands,
    long UnsupportedSourceCommands)
{
    internal bool IsComplete =>
        OmittedHlePrimitiveCommands == 0 &&
        UnsupportedSourceCommands == 0;
}
