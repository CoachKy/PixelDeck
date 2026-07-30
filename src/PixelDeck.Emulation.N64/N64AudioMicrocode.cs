namespace PixelDeck.Emulation.N64;

/// <summary>
/// Audio microcode families identified from the task's resident ucode-data
/// signature. Command numbers and parameter layouts are family-specific.
/// </summary>
public enum N64AudioMicrocode
{
    Unknown,
    Abi1,
    Abi1GoldenEye,
    Abi1BlastCorps,
    NeadMarioKart,
    NeadStarFoxJapan,
    NeadWaveRaceJapanRevB,
    NeadStarFox,
    NeadFZeroX,
    NeadYoshisStory,
    Nead1080Snowboarding,
    NeadZeldaOcarinaOfTime,
    NeadZeldaMajorasMask,
    NeadZeldaMajorasMaskBeta,
    NeadAnimalCrossing,
    NeadMarioArtistTalentStudio,
    NeadFZeroXExpansion,
    MusyxV1,
    MusyxV2,
    NAudio,
    NAudioBanjoKazooie,
    NAudioDonkeyKong,
    NAudioMp3,
    NAudioConker
}
