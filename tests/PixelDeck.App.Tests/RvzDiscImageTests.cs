using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public sealed class RvzDiscImageTests
{
    [Fact]
    public void AnRvzDiscParsesIdenticallyToTheSameDiscUncompressed()
    {
        using var raw = new RvzScope(compress: false);
        using var rvz = new RvzScope(compress: true);

        Assert.Equal("ISO", raw.Disc.ContainerName);
        Assert.Equal("RVZ", rvz.Disc.ContainerName);
        Assert.Equal(raw.Disc.Header, rvz.Disc.Header);
        Assert.Equal(raw.Disc.AppLoader, rvz.Disc.AppLoader);
        Assert.Equal(GameCubeTestSupport.ImageSize, rvz.Disc.Length);
    }

    [Fact]
    public void TheDiscHeaderIsServedFromTheContainerHeaderRatherThanAGroup()
    {
        // RVZ keeps the first 0x80 bytes verbatim and its raw data entry still
        // reports a start of 0x80. Reading that offset literally shifts every
        // group by 0x80: the header looks right and nothing else does, which
        // is why this is asserted separately from the fields below it.
        using var rvz = new RvzScope(compress: true);

        Assert.Equal(GameCubeTestSupport.GameCode, rvz.Disc.Header.GameCode);
        Assert.Equal(GameCubeTestSupport.Title, rvz.Disc.Header.Title);
    }

    [Fact]
    public void FieldsPastTheEmbeddedHeaderAreReadFromTheCorrectGroup()
    {
        using var rvz = new RvzScope(compress: true);

        // These live at 0x420 and beyond, past the embedded header and inside
        // the first group. They are the fields that go wrong when the group
        // alignment is off.
        Assert.Equal(GameCubeTestSupport.ExecutableOffset, rvz.Disc.Header.MainExecutableOffset);
        Assert.Equal(GameCubeTestSupport.FileSystemOffset, rvz.Disc.Header.FileSystemOffset);

        var executable = rvz.Disc.ReadBootExecutable();
        Assert.Equal(GameCubeTestSupport.EntryPoint, executable.EntryPoint);
        Assert.Equal(2, executable.Sections.Count);
    }

    [Fact]
    public void TheFileTableSurvivesCompression()
    {
        using var rvz = new RvzScope(compress: true);

        var fileSystem = rvz.Disc.FileSystem;

        Assert.Equal(4, fileSystem.Entries.Count);
        Assert.Equal(["sub/inner.bin", "root.bin"], fileSystem.Files.Select(file => file.Path));
    }

    [Fact]
    public void DataFollowingAJunkRunIsStillReadCorrectly()
    {
        // The test that matters most. A junk run carries sixty-eight bytes of
        // generator state, not four; getting that wrong desynchronises the
        // packed stream, so every group before the first junk run decodes
        // perfectly and everything after it decodes to nothing.
        using var rvz = new RvzScope(compress: true);
        Assert.True(rvz.Disc.FileSystem.TryGetFile("root.bin", out var root));

        var contents = rvz.Disc.ReadFile(root);

        Assert.Equal(GameCubeTestSupport.RootFileLength, contents.Length);
        Assert.Equal(0x50, contents[0]);
        Assert.Equal(0x57, contents[^1]);
    }

    [Fact]
    public void AJunkRunReadsAsZeroesAndSaysSo()
    {
        using var rvz = new RvzScope(compress: true);

        var junk = rvz.Disc.Read(GameCubeTestSupport.RvzJunkStart, GameCubeTestSupport.RvzJunkLength);

        Assert.All(junk, value => Assert.Equal(0, value));
        Assert.Contains(
            rvz.Trace.CaptureCounters(),
            counter => counter.Key == "rvz/junk-data");
    }

    [Fact]
    public void AWiiDiscIsNamedRatherThanCalledCorrupt()
    {
        var image = GameCubeTestSupport.CreateRvzImage(GameCubeTestSupport.CreateDiscImage());
        image[0x48 + 3] = 2; // disc type: Wii

        var path = WriteTemporary(image, ".rvz");
        try
        {
            var exception = Assert.Throws<NotSupportedException>(() => GameCubeDisc.Open(path));
            Assert.Contains("Wii", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnUnsupportedCompressionMethodSaysWhichOneItIs()
    {
        var image = GameCubeTestSupport.CreateRvzImage(GameCubeTestSupport.CreateDiscImage());
        image[0x48 + 0x07] = 3; // compression: LZMA

        var path = WriteTemporary(image, ".rvz");
        try
        {
            var exception = Assert.Throws<NotSupportedException>(() => GameCubeDisc.Open(path));
            Assert.Contains("Lzma", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Zstandard", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Inspect_OffersAnRvzDiscForLaunch()
    {
        using var rvz = new RvzScope(compress: true);

        var summary = GameCubeDisc.Inspect(rvz.Path);

        Assert.True(summary.IsReadable);
        Assert.Equal("RVZ", summary.ContainerName);
        Assert.Equal("GTSE01", summary.GameId);
    }

    private static string WriteTemporary(byte[] image, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pixelcube-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, image);
        return path;
    }

    private sealed class RvzScope : IDisposable
    {
        public RvzScope(bool compress)
        {
            var raw = GameCubeTestSupport.CreateDiscImage();
            var image = compress ? GameCubeTestSupport.CreateRvzImage(raw) : raw;
            Path = WriteTemporary(image, compress ? ".rvz" : ".iso");
            Trace = new GameCubeTraceLog(
                new GameCubeTraceSettings(GameCubeTraceLevel.Information, GameCubeTraceChannel.All));
            Disc = GameCubeDisc.Open(Path, Trace);
        }

        public string Path { get; }

        public GameCubeTraceLog Trace { get; }

        public GameCubeDisc Disc { get; }

        public void Dispose()
        {
            Disc.Dispose();
            Trace.Dispose();
            File.Delete(Path);
        }
    }
}
