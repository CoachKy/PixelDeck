namespace PixelDeck.Emulation.N64;

public partial class Fast3dRenderer
{
    /// <summary>
    /// F3DEX 1.xx & F3DEX095 microcode handler (Star Fox 64, Mario Kart 64).
    /// Matches Project64 GLideN64 uCodes/F3DEX.cpp & F3DEX095.cpp.
    /// </summary>
    private void ExecuteCommandF3dex(byte opcode, uint word0, uint word1)
    {
        switch (opcode)
        {
            case 0x04: // G_VTX
                LoadVerticesF3dex(word0, word1);
                break;

            case 0xB1: // G_TRI2
                DrawTriangleF3dex2(word0);
                DrawTriangleF3dex2(word1);
                break;

            case 0xB2: // G_MODIFYVTX
                ModifyVertex(word0, word1);
                break;

            case 0xB3: // G_RDPHALF_2
            case 0xB4: // G_RDPHALF_1
                break;

            case 0xB5: // G_LINE3D
                DrawTriangleF3dex2(word0);
                break;

            case 0xBE: // G_CULLDL
                if (ShouldCullDisplayList(word0, word1))
                {
                    return;
                }

                break;

            case 0xBF: // G_TRI1
                DrawTriangleF3dex2(word1);
                break;
        }
    }
}
