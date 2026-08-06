namespace PixelDeck.Emulation.N64;

public partial class Fast3dRenderer
{
    /// <summary>
    /// BOSS Z-Sort microcode handler (World Driver Championship, Stunt Racer 64).
    /// Matches Project64 GLideN64 uCodes/ZSortBOSS.cpp.
    /// </summary>
    private void ExecuteCommandZSortBoss(byte opcode, uint word0, uint word1)
    {
        switch (opcode)
        {
            case 0x01: // G_ZSORT_TRI1
                DrawTriangleF3dex2(word0);
                break;

            case 0x02: // G_ZSORT_TRI2
                DrawTriangleF3dex2(word0);
                DrawTriangleF3dex2(word1);
                break;
        }
    }
}
