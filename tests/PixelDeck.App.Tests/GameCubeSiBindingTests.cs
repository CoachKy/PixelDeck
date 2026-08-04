using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public class GameCubeSiBindingTests
{
    [Fact]
    public void Controller_UpdatesSiRegisterOutputs()
    {
        var trace = new GameCubeTraceLog(null);
        var memory = new GameCubeMemory(trace);

        // Enable SI port 0 polling (bits 4..7)
        memory.WriteUInt32(0xCC006430, 0x00000080); // SerialPoll

        // Set controller buttons & stick
        memory.Hardware.Controllers[0].Buttons = GameCubeButtons.A | GameCubeButtons.Start;
        memory.Hardware.Controllers[0].MainStickX = 200;

        // Perform hardware advance (1 frame) to trigger controller poll
        memory.Hardware.Advance(10_000_000);

        // Read SI0_OUTBUF0 (0xCC006400) and SI0_OUTBUF1 (0xCC006404)
        var buf0 = memory.ReadUInt32(0xCC006400);
        var buf1 = memory.ReadUInt32(0xCC006404);

        Assert.NotEqual(0u, buf0 | buf1);
    }
}
