namespace PixelDeck.Emulation.N64;

public partial class Fast3dRenderer
{
    /// <summary>
    /// F3DEX2 2.xx microcode handler (Zelda OoT, Majora, Conker, Perfect Dark).
    /// Matches Project64 GLideN64 uCodes/F3DEX2.cpp.
    /// </summary>
    private void ExecuteCommandF3dex2(byte opcode, uint word0, uint word1)
    {
        switch (opcode)
        {
            case 0x01: // G_VTX
                LoadVerticesF3dex2(word0, word1);
                break;

            case 0x02: // G_MODIFYVTX
                ModifyVertex(word0, word1);
                break;

            case 0x03: // G_CULLDL
                if (ShouldCullDisplayList(word0, word1))
                {
                    return;
                }

                break;

            case 0x05: // G_TRI1
                DrawTriangleF3dex2(word0);
                break;

            case 0x06: // G_TRI2
                DrawTriangleF3dex2(word0);
                DrawTriangleF3dex2(word1);
                break;

            case 0x07: // G_QUAD
                DrawTriangleF3dex2(word0);
                DrawTriangleF3dex2(word1);
                break;

            case 0xD7: // G_TEXTURE
                SetTexture(word0, word1);
                break;

            case 0xD8: // G_POPMTX
                if (_modelViewStack.Count > 1)
                {
                    _modelViewStack.Pop();
                }

                break;

            case 0xD9: // G_GEOMETRYMODE
                _geometryMode = (_geometryMode & (word0 & 0x00FFFFFF)) | word1;
                break;

            case 0xDA: // G_MTX
                LoadMatrix(ConvertF3dex2MatrixParameters(word0), word1);
                break;

            case 0xDB: // G_MOVEWORD
                MoveWordF3dex2(word0, word1);
                break;

            case 0xDC: // G_MOVEMEM
                MoveMemoryF3dex2(word0, word1);
                break;

            case 0xDF: // G_ENDDL
                return;
        }
    }
}
