using System.Numerics;

namespace PixelDeck.Emulation.N64;

public sealed partial class Fast3dRenderer
{
    private uint _f5RogueVertexColorBase;

    private void ResetF5RogueState()
    {
        _f5RogueVertexColorBase = 0;
    }

    /// <summary>
    /// Parses Factor 5's Rogue Squadron display-list format. Unlike Fast3D,
    /// every list begins with an eight-byte software branch header and several
    /// commands carry inline payload blocks. Consuming those blocks correctly
    /// is essential even for operations that Pixel64 cannot draw yet: treating
    /// payload words as opcodes corrupts the remainder of the frame.
    /// </summary>
    private void ExecuteF5RogueDisplayList(
        uint headerAddress,
        uint? commandLimit,
        int depth,
        ref int remainingBudget)
    {
        if (depth >= MaximumDisplayListDepth ||
            remainingBudget <= 0 ||
            headerAddress + 8 > N64Memory.RdramSize)
        {
            RecordUnsupportedF5RogueCommand(0x06);
            return;
        }

        DisplayListsProcessed++;
        var softwareBranchAddress = _memory.ReadUInt32(headerAddress) & 0x00FFFFFF;
        var address = headerAddress + 8;
        var commandUnitsConsumed = 1u;
        while (remainingBudget-- > 0 &&
               address + 8 <= N64Memory.RdramSize &&
               (!commandLimit.HasValue || commandUnitsConsumed < commandLimit.Value))
        {
            var word0 = _memory.ReadUInt32(address);
            var word1 = _memory.ReadUInt32(address + 4);
            address += 8;
            commandUnitsConsumed++;
            CommandsProcessed++;

            var opcode = (byte)(word0 >> 24);
            _opcodeHistogram[opcode]++;
            switch (opcode)
            {
                case 0x00: // F3DSWRS_SPNOOP
                    break;
                case 0x01: // F3DSWRS_MTX
                    LoadMatrix((int)((word0 >> 16) & 0xFF), word1);
                    break;
                case 0x02: // F3DSWRS_VTXCOLOR
                    _f5RogueVertexColorBase = ResolveAddress(word1);
                    break;
                case 0x03: // F3DSWRS_MOVEMEM + 16-byte inline payload
                    ExecuteF5RogueMoveMemory(word0, address);
                    if (!SkipF5RoguePayload(ref address, ref commandUnitsConsumed, ref remainingBudget, 16))
                    {
                        return;
                    }

                    break;
                case 0x04: // F3DSWRS_VTX
                    LoadF5RogueVertices(word0, word1);
                    break;
                case 0x05: // F3DSWRS_TRI_GEN + 32-byte generated-terrain parameters
                    RecordUnsupportedF5RogueCommand(opcode);
                    if (!SkipF5RoguePayload(ref address, ref commandUnitsConsumed, ref remainingBudget, 32))
                    {
                        return;
                    }

                    break;
                case 0x06: // F3DSWRS_DL
                    ExecuteF5RogueDisplayList(
                        ResolveAddress(word1),
                        commandLimit: null,
                        depth + 1,
                        ref remainingBudget);
                    break;
                case 0x07: // F3DSWRS_BRANCHDL
                {
                    var target = ResolveAddress(word1);
                    if (target + 8 > N64Memory.RdramSize)
                    {
                        RecordUnsupportedF5RogueCommand(opcode);
                        return;
                    }

                    softwareBranchAddress = _memory.ReadUInt32(target) & 0x00FFFFFF;
                    address = target + 8;
                    commandLimit = null;
                    commandUnitsConsumed = 1;
                    break;
                }
                case 0xB3: // F3DSWRS_SETOTHERMODE_L_EX
                    if (address + 8 > N64Memory.RdramSize)
                    {
                        return;
                    }

                    _otherModeLow = (_otherModeLow & _memory.ReadUInt32(address)) | word1;
                    CaptureCanonicalOtherMode();
                    if (!SkipF5RoguePayload(ref address, ref commandUnitsConsumed, ref remainingBudget, 8))
                    {
                        return;
                    }

                    break;
                case 0xB4: // F3DSWRS_TRI2
                    if (!ExecuteF5RogueTriangle(word0, word1, address, drawSecondTriangle: true))
                    {
                        return;
                    }

                    if (!SkipF5RoguePayload(
                            ref address,
                            ref commandUnitsConsumed,
                            ref remainingBudget,
                            (word0 & 2) != 0 ? 24 : 8))
                    {
                        return;
                    }

                    break;
                case 0xB5: // F3DSWRS_JUMPSWDL
                {
                    var target = ResolveAddress(softwareBranchAddress);
                    if (target + 8 > N64Memory.RdramSize)
                    {
                        RecordUnsupportedF5RogueCommand(opcode);
                        return;
                    }

                    softwareBranchAddress = _memory.ReadUInt32(target) & 0x00FFFFFF;
                    address = target + 8;
                    commandLimit = null;
                    commandUnitsConsumed = 1;
                    break;
                }
                case 0xB6: // G_CLEARGEOMETRYMODE
                    _geometryMode &= ~word1;
                    break;
                case 0xB7: // G_SETGEOMETRYMODE
                    _geometryMode |= word1;
                    break;
                case 0xB8: // F3DSWRS_ENDDL
                    return;
                case 0xB9: // G_SETOTHERMODE_L
                    SetOtherModeLow(word0, word1);
                    CaptureCanonicalOtherMode();
                    break;
                case 0xBA: // G_SETOTHERMODE_H
                    SetOtherModeHigh(word0, word1);
                    CaptureCanonicalOtherMode();
                    break;
                case 0xBB: // G_TEXTURE
                    SetTexture(word0, word1);
                    break;
                case 0xBC: // F3DSWRS_MOVEWORD
                    MoveWord(word0, word1);
                    break;
                case 0xBD: // F3DSWRS_TEXRECT_GEN + 16-byte inline payload
                    RecordUnsupportedF5RogueCommand(opcode);
                    if (!SkipF5RoguePayload(ref address, ref commandUnitsConsumed, ref remainingBudget, 16))
                    {
                        return;
                    }

                    break;
                case 0xBE: // F3DSWRS_SETOTHERMODE_H_EX
                    if (address + 8 > N64Memory.RdramSize)
                    {
                        return;
                    }

                    _otherModeHigh = (_otherModeHigh & _memory.ReadUInt32(address)) | word1;
                    CaptureCanonicalOtherMode();
                    if (!SkipF5RoguePayload(ref address, ref commandUnitsConsumed, ref remainingBudget, 8))
                    {
                        return;
                    }

                    break;
                case 0xBF: // F3DSWRS_TRI1
                    if (!ExecuteF5RogueTriangle(word0, word1, address, drawSecondTriangle: false))
                    {
                        return;
                    }

                    if (!SkipF5RoguePayload(
                            ref address,
                            ref commandUnitsConsumed,
                            ref remainingBudget,
                            (word0 & 2) != 0 ? 24 : 8))
                    {
                        return;
                    }

                    break;
                case 0xC0: // RDP NOOP
                    break;
                case 0xE4: // G_TEXRECT
                case 0xE5: // G_TEXRECTFLIP
                    // Factor 5 packs the texture origin and derivatives into
                    // one inline 64-bit block instead of two G_RDPHALF
                    // commands used by ordinary Fast3D.
                    if (address + 8 > N64Memory.RdramSize || remainingBudget < 1)
                    {
                        return;
                    }

                    var halfOne = _memory.ReadUInt32(address);
                    var halfTwo = _memory.ReadUInt32(address + 4);
                    CaptureAndExecuteRdpCommand(word0, word1, halfOne, halfTwo);
                    if (!SkipF5RoguePayload(
                            ref address,
                            ref commandUnitsConsumed,
                            ref remainingBudget,
                            8))
                    {
                        return;
                    }

                    break;
                case >= 0xE6:
                    CaptureAndExecuteRdpCommand(word0, word1);
                    break;
                default:
                    FirstUnsupportedListHeaderAddress ??= headerAddress;
                    RecordUnsupportedF5RogueCommand(
                        opcode,
                        address - 8,
                        $"F5Rogue list=0x{headerAddress:X8} depth={depth} " +
                        $"branch=0x{softwareBranchAddress:X8}");
                    break;
            }
        }
    }

    private void ExecuteF5RogueMoveMemory(uint word0, uint payloadAddress)
    {
        var index = (int)((word0 >> 16) & 0xFF);
        if (index == 0x80 && payloadAddress + 16 <= N64Memory.RdramSize)
        {
            ReadViewport(payloadAddress);
        }
    }

    private void LoadF5RogueVertices(uint word0, uint word1)
    {
        var count = Math.Min((int)((word0 >> 10) & 0x3F), _vertices.Length);
        var address = ResolveAddress(word1);
        if (count <= 0 || address + (count * 8u) > N64Memory.RdramSize)
        {
            return;
        }

        var combined = Matrix4x4.Multiply(_modelViewStack.Peek(), _projection);
        for (var index = 0; index < count; index++)
        {
            var source = address + (uint)(index * 8);
            var position = new Vector4(
                (short)_memory.ReadUInt16(source + 2),
                (short)_memory.ReadUInt16(source),
                (short)_memory.ReadUInt16(source + 6),
                1);
            _vertices[index] = CreateVertex(
                Vector4.Transform(position, combined),
                Vector4.One,
                Vector2.Zero);
            VerticesTransformed++;
        }
    }

    private bool ExecuteF5RogueTriangle(
        uint word0,
        uint word1,
        uint payloadAddress,
        bool drawSecondTriangle)
    {
        var payloadLength = (word0 & 2) != 0 ? 24u : 8u;
        if (payloadAddress + payloadLength > N64Memory.RdramSize)
        {
            return false;
        }

        var first = (int)(((word1 >> 13) & 0x7F8) / 40);
        var second = (int)(((word1 >> 5) & 0x7F8) / 40);
        var third = (int)(((word1 << 3) & 0x7F8) / 40);
        var fourth = (int)(((word1 >> 21) & 0x7F8) / 40);
        Span<int> indices = stackalloc int[4] { first, second, third, fourth };
        var colorOffsets = _memory.ReadUInt32(payloadAddress);
        Span<byte> colorIndices = stackalloc byte[4]
        {
            (byte)(colorOffsets >> 16),
            (byte)(colorOffsets >> 8),
            (byte)colorOffsets,
            (byte)(colorOffsets >> 24)
        };

        var vertexCount = drawSecondTriangle ? 4 : 3;
        var textured = (word0 & 2) != 0;
        for (var index = 0; index < vertexCount; index++)
        {
            var vertexIndex = indices[index];
            if ((uint)vertexIndex >= _vertices.Length || !_vertices[vertexIndex].Valid)
            {
                continue;
            }

            var colorAddress = _f5RogueVertexColorBase + colorIndices[index];
            var color = colorAddress + 4 <= N64Memory.RdramSize
                ? new Vector4(
                    _memory.ReadByte(colorAddress) / 255f,
                    _memory.ReadByte(colorAddress + 1) / 255f,
                    _memory.ReadByte(colorAddress + 2) / 255f,
                    _memory.ReadByte(colorAddress + 3) / 255f)
                : Vector4.One;
            var textureCoordinate = Vector2.Zero;
            if (textured)
            {
                var coordinates = _memory.ReadUInt32(payloadAddress + 8 + (uint)(index * 4));
                textureCoordinate = new Vector2(
                    (short)(coordinates >> 16) / 32f * _textureScaleS,
                    (short)coordinates / 32f * _textureScaleT);
            }

            _vertices[vertexIndex] = _vertices[vertexIndex] with
            {
                Color = color,
                TextureCoordinate = textureCoordinate
            };
        }

        DrawTriangleIndices(first, second, third);
        if (drawSecondTriangle)
        {
            DrawTriangleIndices(first, third, fourth);
        }

        return true;
    }

    private static bool SkipF5RoguePayload(
        ref uint address,
        ref uint commandUnitsConsumed,
        ref int remainingBudget,
        int bytes)
    {
        var units = bytes / 8;
        address += (uint)bytes;
        commandUnitsConsumed += (uint)units;
        remainingBudget -= units;
        return remainingBudget >= 0;
    }

    private void RecordUnsupportedF5RogueCommand(
        byte opcode,
        uint? address = null,
        string? context = null)
    {
        FirstUnsupportedCommandAddress ??= address;
        FirstUnsupportedCommandContext ??= context;
        UnsupportedCommands++;
        _unsupportedCommandCounts[opcode] =
            _unsupportedCommandCounts.GetValueOrDefault(opcode) + 1;
    }
}
