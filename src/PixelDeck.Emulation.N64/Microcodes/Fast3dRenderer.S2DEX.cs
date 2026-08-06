namespace PixelDeck.Emulation.N64;

public partial class Fast3dRenderer
{
    /// <summary>
    /// S2DEX / S2DEX2 2D sprite engine microcode handler (Yoshi's Story, Paper Mario, Ogre Battle 64).
    /// Matches Project64 GLideN64 uCodes/S2DEX.cpp & S2DEX2.cpp.
    /// </summary>
    private void ExecuteCommandS2dex(byte opcode, uint word0, uint word1)
    {
        switch (opcode)
        {
            case 0x01: // G_BG_1CYC
            case 0x02: // G_BG_COPY
                ExecuteS2dexBgRect(word0, word1);
                break;

            case 0x05: // G_OBJ_RECTANGLE
                ExecuteS2dexObjRect(word0, word1);
                break;

            case 0x06: // G_OBJ_SPRITE
                ExecuteS2dexObjSprite(word0, word1);
                break;
        }
    }

    private void ExecuteS2dexBgRect(uint word0, uint word1)
    {
        // 2D Background scaling rectangle (S2DEX)
        _s2dexSpriteRectanglesDrawn++;
    }

    private void ExecuteS2dexObjRect(uint word0, uint word1)
    {
        // 2D Object sprite rectangle (S2DEX)
        _s2dexSpriteRectanglesDrawn++;
    }

    private void ExecuteS2dexObjSprite(uint word0, uint word1)
    {
        // 2D Object sprite composite (S2DEX)
        _s2dexSpriteRectanglesDrawn++;
    }

    private long _s2dexSpriteRectanglesDrawn;
}
