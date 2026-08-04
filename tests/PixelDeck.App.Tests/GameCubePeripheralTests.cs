using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public class GameCubePeripheralTests
{
    [Fact]
    public void Controller_GeneratesCorrectSiReport()
    {
        var controller = new GameCubeController
        {
            Buttons = GameCubeButtons.A | GameCubeButtons.Start,
            MainStickX = 200,
            MainStickY = 50,
            CStickX = 128,
            CStickY = 128,
            TriggerL = 100,
            TriggerR = 255
        };

        var report = controller.GetSiReport();
        Assert.NotEqual(0UL, report);

        // Disconnected controller gives 0
        controller.IsConnected = false;
        Assert.Equal(0UL, controller.GetSiReport());
    }

    [Fact]
    public void MemoryCard_ParsesAndExportsGciHeader()
    {
        var rawGci = new byte[0x40 + 512]; // Header + payload
        System.Text.Encoding.ASCII.GetBytes("GMSP").CopyTo(rawGci, 0);
        System.Text.Encoding.ASCII.GetBytes("01").CopyTo(rawGci, 4);
        System.Text.Encoding.ASCII.GetBytes("SuperMarioSave").CopyTo(rawGci, 8);

        var gci = GameCubeMemoryCardFile.Parse(rawGci);
        Assert.StartsWith("GMSP", gci.GameCode);
        Assert.StartsWith("SuperMarioSave", gci.FileName);

        var exported = gci.Export();
        Assert.Equal(rawGci.Length, exported.Length);
    }
}
