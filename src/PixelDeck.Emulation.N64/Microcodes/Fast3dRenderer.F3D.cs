namespace PixelDeck.Emulation.N64;

public partial class Fast3dRenderer
{
    /// <summary>
    /// Fast3D v1.0 microcode handler (Cruis'n USA, Super Mario 64, Wave Race 64).
    /// Matches Project64 GLideN64 uCodes/F3D.cpp.
    /// </summary>
    private void ExecuteCommandF3d(byte opcode, uint word0, uint word1, ref uint address, ref int? commandLimit, ref int commandsInList, ref int remainingBudget)
    {
        switch (opcode)
        {
            case 0x01: // G_MTX
                LoadMatrix((int)((word0 >> 16) & 0xFF), word1);
                break;

            case 0x03: // G_MOVEMEM
                MoveMemory(word0, word1);
                break;

            case 0x04: // G_VTX
                LoadVertices(word0, word1);
                break;

            case 0x06: // G_DL
            {
                var target = ResolveAddress(word1);
                var noPush = ((word0 >> 16) & 0xFF) != 0;
                if (noPush)
                {
                    address = target;
                    commandLimit = null;
                    commandsInList = 0;
                }
                else
                {
                    ExecuteDisplayList(target, null, 0, ref remainingBudget);
                }

                break;
            }

            case 0xB8: // G_ENDDL
                return;

            case 0xBA: // G_SETOTHERMODE_H
                SetOtherModeHigh(ConvertF3dModeSelector(word0), word1);
                CaptureCanonicalOtherMode();
                break;

            case 0xBB: // G_TEXTURE
                SetTexture(word0, word1);
                break;

            case 0xBC: // G_MOVEWORD
                MoveWord(word0, word1);
                break;

            case 0xBD: // G_POPMTX
                if (_modelViewStack.Count > 1)
                {
                    _modelViewStack.Pop();
                }

                break;

            case 0xBE: // G_CULLDL
                if (ShouldCullDisplayList(word0, word1))
                {
                    return;
                }

                break;

            case 0xBF: // G_TRI1
                if (_microcode == N64Microcode.F3dBeta)
                {
                    DrawTriangleF3dBeta(word1);
                }
                else
                {
                    DrawTriangle(word1);
                }

                break;

            case 0xB1: // G_TRI4 / G_TRI2
                if (_microcode == N64Microcode.F3dBeta)
                {
                    DrawTriangleF3dBeta(word0);
                    DrawTriangleF3dBeta(word1);
                }
                else
                {
                    for (var triangle = 0; triangle < 4; triangle++)
                    {
                        var shift = 24 - (triangle * 8);
                        var indices = (word0 >> shift) & 0xFF;
                        if (indices != 0)
                        {
                            DrawTriangleIndices((int)((indices >> 4) & 0xF), (int)(indices & 0xF), (int)((word1 >> shift) & 0xF));
                        }
                    }
                }

                break;

            case 0xB5: // G_QUAD
                if (_microcode == N64Microcode.F3dBeta)
                {
                    DrawTriangleF3dBeta(word0);
                    DrawTriangleF3dBeta(word1);
                }
                else
                {
                    DrawQuadF3d(word0, word1);
                }

                break;
        }
    }

    private static uint ConvertF3dModeSelector(uint word0) =>
        (word0 >> 16) & 0xFF;

    private void DrawQuadF3d(uint word0, uint word1)
    {
        DrawTriangleIndices(
            (int)((word0 >> 16) & 0xFF) / 10,
            (int)((word0 >> 8) & 0xFF) / 10,
            (int)(word0 & 0xFF) / 10);
        DrawTriangleIndices(
            (int)((word1 >> 16) & 0xFF) / 10,
            (int)((word1 >> 8) & 0xFF) / 10,
            (int)(word1 & 0xFF) / 10);
    }
}
