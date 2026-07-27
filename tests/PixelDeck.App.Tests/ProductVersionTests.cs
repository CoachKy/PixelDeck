using PixelDeck.App.ViewModels;
using PixelDeck.Emulation.Nes;
using PixelDeck.Emulation.N64;
using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.Tests;

public sealed class ProductVersionTests
{
    [Fact]
    public void Assemblies_HaveIndependentProductVersions()
    {
        Assert.Equal(new Version(1, 18, 61, 0), typeof(MainViewModel).Assembly.GetName().Version);
        Assert.Equal(new Version(1, 15, 23, 0), typeof(NesMachine).Assembly.GetName().Version);
        Assert.Equal(new Version(1, 15, 21, 0), typeof(SnesMachine).Assembly.GetName().Version);
        Assert.Equal(new Version(0, 9, 7, 0), typeof(N64Machine).Assembly.GetName().Version);
    }

    [Fact]
    public void Dashboard_FormatsAndSwitchesProductVersionLabels()
    {
        using var viewModel = new MainViewModel();

        Assert.Equal("PixelDeck v1.18.061", viewModel.PixelDeckVersionText);
        Assert.Equal("PixelNES v1.15.023", viewModel.LibraryEmulatorVersionText);

        viewModel.SelectedLibrarySystem = LibrarySystem.SuperNintendo;

        Assert.Equal("PixelSNES v1.15.021", viewModel.LibraryEmulatorVersionText);

        viewModel.SelectedLibrarySystem = LibrarySystem.Nintendo64;

        Assert.Equal("Pixel64 v0.9.007", viewModel.LibraryEmulatorVersionText);
    }
}
