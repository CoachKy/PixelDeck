namespace PixelDeck.Emulation.N64;

public sealed partial class Fast3dRenderer
{
    private List<N64RdpCommand>? _capturedRdpCommands;
    private int _omittedHlePrimitiveCommands;

    internal void BeginRdpTraceCapture()
    {
        if (_capturedRdpCommands is not null)
        {
            throw new InvalidOperationException("RDP trace capture is already active.");
        }

        _capturedRdpCommands = [];
        _omittedHlePrimitiveCommands = 0;
    }

    internal N64RdpTrace EndRdpTraceCapture(ReadOnlySpan<byte> initialRdram)
    {
        var commands = _capturedRdpCommands ??
            throw new InvalidOperationException("RDP trace capture is not active.");
        _capturedRdpCommands = null;
        return new N64RdpTrace(
            initialRdram,
            commands,
            DetectedMicrocodeName,
            _omittedHlePrimitiveCommands,
            UnsupportedCommands);
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
            default:
                UnsupportedCommands++;
                _unsupportedCommandCounts[command.Opcode] =
                    _unsupportedCommandCounts.GetValueOrDefault(command.Opcode) + 1;
                break;
        }
    }
}
