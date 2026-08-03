using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public sealed class GameCubeHardwareTests
{
    private const uint DspControlStatus = 0xCC00_500A;
    private const uint DspMailFromDsp = 0xCC00_5004;
    private const uint ExiChannelZeroControl = 0xCC00_680C;
    private const uint AramDmaMain = 0xCC00_5020;
    private const uint AramDmaAram = 0xCC00_5024;
    private const uint AramDmaControl = 0xCC00_5028;

    [Fact]
    public void TheDspResetBitClearsItself()
    {
        // The single most valuable register in the whole block. Super Mario
        // Sunshine writes it three times and then reads it 978,834 times
        // waiting for this bit to drop; without that, the boot never advances
        // past DSPInit.
        using var fixture = new MemoryFixture();

        fixture.Memory.WriteUInt16(DspControlStatus, 0x0001);

        Assert.Equal(0, fixture.Memory.ReadUInt16(DspControlStatus) & 0x0001);
    }

    [Fact]
    public void TheDspDmaBusyBitIsNeverSet()
    {
        using var fixture = new MemoryFixture();

        fixture.Memory.WriteUInt16(DspControlStatus, 0x0200);

        Assert.Equal(0, fixture.Memory.ReadUInt16(DspControlStatus) & 0x0200);
    }

    [Fact]
    public void AnExternalInterfaceTransferReportsItselfFinished()
    {
        // Same handshake shape as the DSP one, on the bus the console's SRAM
        // and memory cards live on. OSInit reads SRAM before anything else.
        using var fixture = new MemoryFixture();

        fixture.Memory.WriteUInt32(ExiChannelZeroControl, 0x0000_0035);

        Assert.Equal(0u, fixture.Memory.ReadUInt32(ExiChannelZeroControl) & 1);
    }

    [Fact]
    public void AramTransfersMoveBytesInBothDirectionsAndRaiseCompletion()
    {
        using var fixture = new MemoryFixture();
        for (var index = 0; index < 64; index++)
        {
            fixture.Memory.MainMemory[0x1000 + index] = (byte)(index + 1);
        }

        fixture.Memory.WriteUInt32(AramDmaMain, 0x1000);
        fixture.Memory.WriteUInt32(AramDmaAram, 0x2000);
        fixture.Memory.WriteUInt32(AramDmaControl, 64);          // main -> ARAM

        Assert.Equal(1, fixture.Memory.AuxiliaryMemory[0x2000]);
        Assert.Equal(64, fixture.Memory.AuxiliaryMemory[0x2000 + 63]);
        Assert.NotEqual(0, fixture.Memory.ReadUInt16(DspControlStatus) & 0x0020);

        fixture.Memory.WriteUInt32(AramDmaMain, 0x3000);
        fixture.Memory.WriteUInt32(AramDmaControl, 0x8000_0000 | 64); // ARAM -> main

        Assert.Equal(1, fixture.Memory.MainMemory[0x3000]);
        Assert.Equal(64, fixture.Memory.MainMemory[0x3000 + 63]);
    }

    [Fact]
    public void AnAramTransferOutsideMemoryIsRefusedAndReported()
    {
        using var fixture = new MemoryFixture();

        fixture.Memory.WriteUInt32(AramDmaMain, GameCubeMemory.MainMemorySize - 8);
        fixture.Memory.WriteUInt32(AramDmaAram, 0);
        fixture.Memory.WriteUInt32(AramDmaControl, 0x1000);

        Assert.Contains(
            fixture.Trace.CaptureCounters(),
            counter => counter.Key == "aram/out-of-range");
    }

    [Fact]
    public void ARegisterWithNoModelledBehaviourStaysOnTheWorkList()
    {
        using var fixture = new MemoryFixture();

        // The pixel engine has nothing behind it at all.
        fixture.Memory.WriteUInt32(0xCC00_1000, 0x1234);

        Assert.Contains(
            fixture.Trace.CaptureRecent(),
            record => record.Channel == GameCubeTraceChannel.Unimplemented &&
                      record.Message.Contains("no modelled behaviour", StringComparison.Ordinal));
    }

    [Fact]
    public void AModelledRegisterIsStillCountedWhenItIsHot()
    {
        // The trap this exists to catch: declaring a register handled removes
        // it from the unimplemented list, so a handshake that answers wrongly
        // produces a spin loop that nothing counts. Sunshine hit exactly this
        // — the DSP mailbox replaced the DSP reset as the wall, and only the
        // counters showed it.
        using var fixture = new MemoryFixture();

        for (var index = 0; index < 1000; index++)
        {
            fixture.Memory.ReadUInt16(DspControlStatus);
        }

        var counter = Assert.Single(
            fixture.Trace.CaptureCounters(),
            entry => entry.Key.Contains("DSP+0x00A", StringComparison.Ordinal) &&
                     entry.Key.StartsWith("register/read", StringComparison.Ordinal));
        Assert.Equal(1000, counter.Count);
    }

    [Fact]
    public void ResettingTheDspAnnouncesItsBootRom()
    {
        // 0x8071FEED is what the real boot ROM sends, and its top bit is the
        // mailbox's own "mail waiting" flag — which is exactly what the CPU's
        // poll is testing for.
        using var fixture = new MemoryFixture();

        fixture.Memory.WriteUInt16(DspControlStatus, 0x0001); // reset

        Assert.Equal(0x8071, fixture.Memory.ReadUInt16(DspMailFromDsp));
        Assert.Equal(0xFEED, fixture.Memory.ReadUInt16(DspMailFromDsp + 2));

        // Reading the low word takes the message; the flag drops.
        Assert.Equal(0, fixture.Memory.ReadUInt16(DspMailFromDsp) & 0x8000);
    }

    [Fact]
    public void ClearingTheInitBitAnnouncesTheMicrocode()
    {
        // The second half of the handshake. Without it the CPU takes the boot
        // ROM's greeting and then waits forever for the microcode's — which is
        // how it presented: twenty-four million reads of one register, and on
        // removing it again, a hundred and twenty-four million.
        using var fixture = new MemoryFixture();

        fixture.Memory.WriteUInt16(DspControlStatus, 0x0801); // reset, init in progress
        fixture.Memory.ReadUInt16(DspMailFromDsp + 2);        // take the ROM's mail

        fixture.Memory.WriteUInt16(DspControlStatus, 0x0004); // init bit cleared

        Assert.Equal(0x8054, fixture.Memory.ReadUInt16(DspMailFromDsp));
        Assert.Equal(0x4348, fixture.Memory.ReadUInt16(DspMailFromDsp + 2));
    }

    [Fact]
    public void AMessageTheBootRomCannotUnderstandIsRefusedRatherThanIgnored()
    {
        // Refusing is part of the protocol, not a courtesy. While the boot ROM
        // is waiting to be told where a microcode lives, anything that does not
        // begin with its parameter prefix is echoed back under 0xFEEE, and
        // software checks for that answer. Saying nothing at all is a different
        // message from saying no.
        using var fixture = new MemoryFixture();

        fixture.Memory.WriteUInt16(0xCC00_5000, 0xABCD);
        fixture.Memory.WriteUInt16(0xCC00_5002, 0x1234);

        // The reply is waiting in the mailbox, with the top bit set to say so.
        var reply = fixture.Memory.ReadUInt32(0xCC00_5004);
        Assert.Equal(0xFEEE_1234u, reply);
    }

    [Fact]
    public void TheBootRomCollectsAMicrocodeDescriptionAndStartsIt()
    {
        // Five parameters, each named by one message and given by the next.
        // The last pair starts the uploaded code, and the microcode then
        // announces itself — which is the message a game blocks on.
        using var fixture = new MemoryFixture();

        foreach (var (selector, value) in ((uint, uint)[])
            [(0x80F3_A001, 0x0100_0000), (0x80F3_A002, 0x1000),
             (0x80F3_B002, 0x0800), (0x80F3_C002, 0x0000), (0x80F3_D001, 0x0010)])
        {
            fixture.Memory.WriteUInt16(0xCC00_5000, (ushort)(selector >> 16));
            fixture.Memory.WriteUInt16(0xCC00_5002, (ushort)selector);
            fixture.Memory.WriteUInt16(0xCC00_5000, (ushort)(value >> 16));
            fixture.Memory.WriteUInt16(0xCC00_5002, (ushort)value);
        }

        // The announcement is deliberately not immediate: a reply that arrives
        // inside the store which sent the request lands before the sending
        // routine has returned.
        Assert.Equal(0u, fixture.Memory.ReadUInt32(0xCC00_5004) & 0x8000_0000);
        fixture.Memory.Hardware.Advance(4000);
        Assert.NotEqual(0u, fixture.Memory.ReadUInt32(0xCC00_5004) & 0x8000_0000);
    }

    [Fact]
    public void TheAramControllerReportsItselfInitialised()
    {
        // ARAM_NORM, bit 0 of the ARAM mode register: raised by the controller
        // once it has finished initialising. Confirmed against the hardware
        // documentation rather than inferred from the shape of the poll — the
        // upper bits of this same register are a mode the CPU writes, so
        // "reading back configuration" was an equally plausible reading.
        using var fixture = new MemoryFixture();

        Assert.Equal(1, fixture.Memory.ReadUInt16(0xCC00_5016) & 1);

        // A mode written by the CPU survives alongside it.
        fixture.Memory.WriteUInt16(0xCC00_5016, 0x8000);

        var mode = fixture.Memory.ReadUInt16(0xCC00_5016);
        Assert.Equal(0x8000, mode & 0x8000);
        Assert.Equal(1, mode & 1);
    }

    [Fact]
    public void TheDvdDriveReportsItselfPresent()
    {
        using var fixture = new MemoryFixture();

        Assert.Equal(1u, fixture.Memory.ReadUInt32(0xCC00_6024));
    }

    private sealed class MemoryFixture : IDisposable
    {
        public MemoryFixture()
        {
            Trace = new GameCubeTraceLog(
                new GameCubeTraceSettings(GameCubeTraceLevel.Information, GameCubeTraceChannel.All));
            Memory = new GameCubeMemory(Trace);
        }

        public GameCubeTraceLog Trace { get; }

        public GameCubeMemory Memory { get; }

        public void Dispose() => Trace.Dispose();
    }
}
