using System.Security.Cryptography;
using PixelDeck.App.Services;

namespace PixelDeck.App.Tests;

public sealed class GameLibraryTests
{
    [Fact]
    public async Task ScanAsync_FindsSupportedGamesRecursivelyAndIgnoresOtherFiles()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var nestedFolder = Directory.CreateDirectory(Path.Combine(testRoot, GameLibrary.NintendoFolderName));
            await File.WriteAllBytesAsync(Path.Combine(nestedFolder.FullName, "My_Homebrew.nes"), CreateNesImage(mapper: 0));
            await File.WriteAllTextAsync(Path.Combine(testRoot, "notes.txt"), "not a game");

            var games = await new GameLibrary(testRoot).ScanAsync();

            var game = Assert.Single(games);
            Assert.Equal("My Homebrew", game.Title);
            Assert.Equal("Nintendo Entertainment System", game.Platform);
            Assert.Equal(Path.Combine(GameLibrary.NintendoFolderName, "My_Homebrew.nes"), game.RelativePath);
            Assert.Equal("16 KB", game.SizeText);
            Assert.EndsWith(".png", game.ScreenshotCachePath);
            Assert.EndsWith(".sav", game.SaveRamPath);
            Assert.Contains($"{Path.DirectorySeparatorChar}saves{Path.DirectorySeparatorChar}", game.SaveRamPath);
            Assert.False(game.HasScreenshot);
            Assert.Equal(0, game.MapperNumber);
            Assert.True(game.CanLaunch);
            Assert.Equal("READY", game.LaunchBadgeText);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_ReportsAnUnsupportedMapperWithoutOfferingLaunch()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(testRoot, "Future.nes"), CreateNesImage(mapper: 99));

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.Equal(99, game.MapperNumber);
            Assert.False(game.CanLaunch);
            Assert.Contains("mapper 99", game.CompatibilityText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_MarksMulticartsWithUnsupportedPeripheralContentAsPartial()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(testRoot, "Controller-and-Zapper.nes"),
                CreateNesImage(mapper: 0, nes20: true, defaultInputDevice: 0x2A));

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.True(game.CanLaunch);
            Assert.True(game.IsLimitedCompatibility);
            Assert.Equal("PARTIAL", game.LaunchBadgeText);
            Assert.Contains("Zapper", game.CompatibilityText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_DoesNotOfferPalOnlyNesImagesToTheNtscCore()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(testRoot, "PAL-Homebrew.nes"),
                CreateNesImage(mapper: 0, nes20: true, timingMode: 1));

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.False(game.CanLaunch);
            Assert.Equal("UNSUPPORTED", game.LaunchBadgeText);
            Assert.Contains("NTSC-only", game.CompatibilityText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_OffersStandardSnesImagesAsEarlyCoreGames()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var snesFolder = Directory.CreateDirectory(Path.Combine(testRoot, GameLibrary.SuperNintendoFolderName));
            await File.WriteAllBytesAsync(Path.Combine(snesFolder.FullName, "Homebrew.sfc"), CreateSnesImage());

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.Equal("PIXELDECK HOMEBREW", game.Title);
            Assert.Equal("Super Nintendo Entertainment System", game.Platform);
            Assert.Equal("LOROM", game.MapperText);
            Assert.Equal("READY", game.LaunchBadgeText);
            Assert.True(game.CanLaunch);
            Assert.Contains("S-DSP stereo audio are active", game.CompatibilityText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_UsesAValidatedNintendoHeaderTitleForNesImages()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var image = CreateNesImage(mapper: 0);
            WriteNintendoHeaderTitle(image, "PIXEL ADVENTURE");
            await File.WriteAllBytesAsync(Path.Combine(testRoot, "A1B2C3D4.nes"), image);

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.Equal("PIXEL ADVENTURE", game.Title);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_DoesNotLetALegacyNintendoHeaderReplaceAReadableFilename()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var image = CreateNesImage(mapper: 0);
            WriteNintendoHeaderTitle(image, "ABBREVIATED");
            await File.WriteAllBytesAsync(Path.Combine(testRoot, "Readable Game Name.nes"), image);

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.Equal("Readable Game Name", game.Title);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_UsesAnOfflineDatTitleAndInvalidatesTheFilenameCache()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var image = CreateNesImage(mapper: 0);
            var gamePath = Path.Combine(testRoot, "not-the-real-title.nes");
            await File.WriteAllBytesAsync(gamePath, image);
            var library = new GameLibrary(testRoot);

            var filenameGame = Assert.Single(await library.ScanAsync());
            Assert.Equal("not-the-real-title", filenameGame.Title);

            var payloadSha1 = Convert.ToHexString(SHA1.HashData(image.AsSpan(16)));
            await File.WriteAllTextAsync(
                Path.Combine(library.MetadataFolder, "Nintendo - Nintendo Entertainment System.dat"),
                $$"""
                clrmamepro (
                    name "Nintendo - Nintendo Entertainment System"
                )
                game (
                    name "Catalog Title (USA)"
                    description "Catalog Title (USA)"
                    rom (
                        name "Catalog Title (USA).nes"
                        size {{image.Length - 16}}
                        sha1 {{payloadSha1}}
                    )
                )
                """);

            var catalogGame = Assert.Single(await library.ScanAsync());

            Assert.Equal("Catalog Title (USA)", catalogGame.Title);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Theory]
    [InlineData(".xml")]
    [InlineData(".json")]
    public async Task ScanAsync_AcceptsXmlAndJsonOfflineCatalogs(string catalogExtension)
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var image = CreateNesImage(mapper: 0);
            await File.WriteAllBytesAsync(Path.Combine(testRoot, "unknown.nes"), image);
            var library = new GameLibrary(testRoot);
            var sha1 = Convert.ToHexString(SHA1.HashData(image));
            var catalog = catalogExtension == ".xml"
                ? $$"""
                    <datafile>
                      <game name="Structured Catalog Title">
                        <description>Structured Catalog Title</description>
                        <rom name="game.nes" size="{{image.Length}}" sha1="{{sha1}}" />
                      </game>
                    </datafile>
                    """
                : $$"""
                    {
                      "games": [
                        {
                          "title": "Structured Catalog Title",
                          "sha1": "{{sha1}}"
                        }
                      ]
                    }
                    """;
            await File.WriteAllTextAsync(
                Path.Combine(library.MetadataFolder, "catalog" + catalogExtension),
                catalog);

            var game = Assert.Single(await library.ScanAsync());

            Assert.Equal("Structured Catalog Title", game.Title);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_OffersDsp1SnesImagesWithTheCorrectCapability()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var snesFolder = Directory.CreateDirectory(
                Path.Combine(testRoot, GameLibrary.SuperNintendoFolderName));
            await File.WriteAllBytesAsync(
                Path.Combine(snesFolder.FullName, "Kart.sfc"),
                CreateDsp1SnesImage());

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.Equal("HIROM", game.MapperText);
            Assert.Equal("READY", game.LaunchBadgeText);
            Assert.True(game.CanLaunch);
            Assert.Contains("DSP-1", game.CompatibilityText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_LoadsASameNamedLocalScreenshot()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var gamePath = Path.Combine(testRoot, "Preview.nes");
            var screenshotPath = Path.Combine(testRoot, "Preview.png");
            var onePixelPng = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

            await File.WriteAllBytesAsync(gamePath, new byte[16]);
            await File.WriteAllBytesAsync(screenshotPath, onePixelPng);

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.Equal(screenshotPath, game.ScreenshotPath);
            Assert.False(game.HasScreenshot);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_ReturnsAnEmptyLibraryWhenNoGamesExist()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var games = await new GameLibrary(testRoot).ScanAsync();

            Assert.Empty(games);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public void ConstructorCreatesConsoleLibraryFolders()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var library = new GameLibrary(testRoot);

            Assert.Equal(Path.Combine(testRoot, GameLibrary.NintendoFolderName), library.NintendoFolder);
            Assert.Equal(Path.Combine(testRoot, GameLibrary.SuperNintendoFolderName), library.SuperNintendoFolder);
            Assert.True(Directory.Exists(library.NintendoFolder));
            Assert.True(Directory.Exists(library.SuperNintendoFolder));
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    private static string CreateTestDirectory()
    {
        var testParent = Path.Combine(Path.GetTempPath(), "PixelDeck.Tests");
        var testRoot = Path.Combine(testParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        return testRoot;
    }

    private static byte[] CreateNesImage(
        int mapper,
        bool nes20 = false,
        byte defaultInputDevice = 0,
        byte timingMode = 0)
    {
        var image = new byte[16 + 16_384];
        image[0] = (byte)'N';
        image[1] = (byte)'E';
        image[2] = (byte)'S';
        image[3] = 0x1A;
        image[4] = 1;
        image[6] = (byte)((mapper & 0x0F) << 4);
        image[7] = (byte)((mapper & 0xF0) | (nes20 ? 0x08 : 0));
        if (nes20)
        {
            image[8] = (byte)((mapper >> 8) & 0x0F);
            image[12] = timingMode;
            image[15] = defaultInputDevice;
        }

        return image;
    }

    private static byte[] CreateSnesImage()
    {
        var image = new byte[32 * 1024];
        const int header = 0x7FC0;
        "PIXELDECK HOMEBREW  ".Select(character => (byte)character).ToArray().CopyTo(image, header);
        image[header + 0x15] = 0x20;
        image[header + 0x16] = 0x00;
        image[header + 0x17] = 0x05;
        image[header + 0x19] = 0x01;
        image[header + 0x1C] = 0xCB;
        image[header + 0x1D] = 0xED;
        image[header + 0x1E] = 0x34;
        image[header + 0x1F] = 0x12;
        image[header + 0x3C] = 0x00;
        image[header + 0x3D] = 0x80;
        return image;
    }

    private static byte[] CreateDsp1SnesImage()
    {
        var image = new byte[64 * 1024];
        const int header = 0xFFC0;
        "PIXELDECK DSP1 TEST  ".Select(character => (byte)character).ToArray().CopyTo(image, header);
        image[header + 0x15] = 0x31;
        image[header + 0x16] = 0x05;
        image[header + 0x17] = 0x06;
        image[header + 0x18] = 0x03;
        image[header + 0x19] = 0x01;
        image[header + 0x1C] = 0xCB;
        image[header + 0x1D] = 0xED;
        image[header + 0x1E] = 0x34;
        image[header + 0x1F] = 0x12;
        image[header + 0x3C] = 0x00;
        image[header + 0x3D] = 0x80;
        return image;
    }

    private static void WriteNintendoHeaderTitle(byte[] image, string title)
    {
        var titleBytes = System.Text.Encoding.ASCII.GetBytes(title);
        Assert.InRange(titleBytes.Length, 2, 16);

        var nintendoHeader = image.AsSpan(16 + 16_384 - 32, 32);
        titleBytes.CopyTo(nintendoHeader.Slice(16 - titleBytes.Length, titleBytes.Length));
        nintendoHeader[0x16] = 1;
        nintendoHeader[0x17] = (byte)(titleBytes.Length - 1);

        var validationSum = 0;
        for (var index = 0x12; index < 0x19; index++)
        {
            validationSum = (validationSum + nintendoHeader[index]) & 0xFF;
        }

        nintendoHeader[0x19] = unchecked((byte)-validationSum);
    }

    private static void DeleteTestDirectory(string testRoot)
    {
        var testParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PixelDeck.Tests"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolvedRoot = Path.GetFullPath(testRoot);

        if (!resolvedRoot.StartsWith(testParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to remove a directory outside the PixelDeck test area.");
        }

        if (Directory.Exists(resolvedRoot))
        {
            Directory.Delete(resolvedRoot, recursive: true);
        }
    }
}
