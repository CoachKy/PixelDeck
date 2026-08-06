namespace PixelDeck.Emulation.N64;

public partial class Fast3dRenderer
{
    /// <summary>
    /// Turbo3D / T3DUX high-speed graphics pipeline microcode handler (Dark Rift, Toukon Road).
    /// Matches Project64 GLideN64 uCodes/Turbo3D.cpp & T3DUX.h.
    /// </summary>
    private void ExecuteCommandTurbo3d(byte opcode, uint word0, uint word1)
    {
        switch (opcode)
        {
            case 0x01: // G_T3D_TRI1
                DrawTriangleF3dex2(word0);
                break;

            case 0x02: // G_T3D_TRI2
                DrawTriangleF3dex2(word0);
                DrawTriangleF3dex2(word1);
                break;
        }
    }
}
