using PixelDeck.App.ViewModels;
using PixelDeck.Emulation.Nes;
using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.Tests;

public sealed class ProductVersionTests
{
    [Fact]
    public void Assemblies_HaveIndependentProductVersions()
    {
        Assert.Equal(new Version(0, 8, 20, 0), typeof(MainViewModel).Assembly.GetName().Version);
        Assert.Equal(new Version(1, 7, 11, 0), typeof(NesMachine).Assembly.GetName().Version);
        Assert.Equal(new Version(0, 8, 9, 0), typeof(SnesMachine).Assembly.GetName().Version);
    }

    [Fact]
    public void Dashboard_FormatsAndSwitchesProductVersionLabels()
    {
        using var viewModel = new MainViewModel();

        Assert.Equal("PixelDeck v0.8.020", viewModel.PixelDeckVersionText);
        Assert.Equal("PixelNES v1.7.011", viewModel.LibraryEmulatorVersionText);

        viewModel.SelectedLibrarySystem = LibrarySystem.SuperNintendo;

        Assert.Equal("PixelSNES v0.8.009", viewModel.LibraryEmulatorVersionText);
    }
}
