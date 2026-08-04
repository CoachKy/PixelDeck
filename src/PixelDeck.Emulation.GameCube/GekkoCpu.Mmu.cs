namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// PowerPC 750CX (Gekko) Memory Management Unit (MMU) & Block Address Translation (BAT).
/// </summary>
public sealed partial class GekkoCpu
{
    public struct BatEntry
    {
        public uint Upper { get; set; }
        public uint Lower { get; set; }

        public readonly uint Bepi => Upper & 0xFFFE0000u;
        public readonly uint Bl => (Upper >> 2) & 0x00001FFFu;
        public readonly bool ValidSupervisor => (Upper & 0x00000002u) != 0;
        public readonly bool ValidUser => (Upper & 0x00000001u) != 0;

        public readonly uint Brpn => Lower & 0xFFFE0000u;

        public readonly bool IsValid => ValidSupervisor || ValidUser;

        public readonly bool TryTranslate(uint effectiveAddress, out uint physicalAddress)
        {
            physicalAddress = 0;
            if (!IsValid)
            {
                return false;
            }

            var mask = ~(Bl << 17);
            if ((effectiveAddress & mask & 0xFFFE0000u) == (Bepi & mask))
            {
                var offset = effectiveAddress & ~(mask & 0xFFFE0000u);
                physicalAddress = (Brpn & mask) | offset;
                return true;
            }

            return false;
        }
    }

    private readonly BatEntry[] _ibat = new BatEntry[4];
    private readonly BatEntry[] _dbat = new BatEntry[4];

    public ReadOnlySpan<BatEntry> IBat => _ibat;
    public ReadOnlySpan<BatEntry> DBat => _dbat;

    /// <summary>
    /// Translates an effective memory address to physical address via BATs or direct mapping.
    /// </summary>
    public uint TranslateAddress(uint effectiveAddress, bool isInstruction)
    {
        var bats = isInstruction ? _ibat : _dbat;
        for (var i = 0; i < 4; i++)
        {
            if (bats[i].TryTranslate(effectiveAddress, out var physical))
            {
                return physical;
            }
        }

        // Direct hardware mapping fallback for GameCube (mask top 3 bits)
        return effectiveAddress & 0x1FFF_FFFFu;
    }

    private void UpdateBatRegister(uint register, uint value)
    {
        var isLower = (register & 1) != 0;

        if (register is >= 528 and <= 535)
        {
            // IBAT (528..535) -> indices 0..3
            var batIdx = (int)((register - 528) / 2);
            if (isLower) _ibat[batIdx].Lower = value;
            else _ibat[batIdx].Upper = value;
        }
        else if (register is >= 536 and <= 543)
        {
            // DBAT (536..543) -> indices 0..3
            var batIdx = (int)((register - 536) / 2);
            if (isLower) _dbat[batIdx].Lower = value;
            else _dbat[batIdx].Upper = value;
        }
    }
}
