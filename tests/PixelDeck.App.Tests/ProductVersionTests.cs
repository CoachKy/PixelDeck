using PixelDeck.App.ViewModels;
using PixelDeck.Emulation.Nes;
using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.Tests;

public sealed class ProductVersionTests
{
    [Fact]
    public void Assemblies_HaveIndependentProductVersions()
    {
        Assert.Equal(new Version(0, 15, 49, 0), typeof(MainViewModel).Assembly.GetName().Version);
        Assert.Equal(new Version(1, 15, 21, 0), typeof(NesMachine).Assembly.GetName().Version);
        Assert.Equal(new Version(0, 14, 18, 0), typeof(SnesMachine).Assembly.GetName().Version);
    }

    [Fact]
    public void Dashboard_FormatsAndSwitchesProductVersionLabels()
    {
        using var viewModel = new MainViewModel();

        Assert.Equal("PixelDeck v0.15.049", viewModel.PixelDeckVersionText);
        Assert.Equal("PixelNES v1.15.021", viewModel.LibraryEmulatorVersionText);

        viewModel.SelectedLibrarySystem = LibrarySystem.SuperNintendo;

        Assert.Equal("PixelSNES v0.14.018", viewModel.LibraryEmulatorVersionText);
    }
}
