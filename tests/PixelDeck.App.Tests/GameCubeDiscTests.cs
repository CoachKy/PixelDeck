using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public sealed class GameCubeDiscTests
{
    [Fact]
    public void Open_ReadsTheDiscHeader()
    {
        using var scope = new DiscScope(compressed: false);

        var header = scope.Disc.Header;

        Assert.Equal("GTSE", header.GameCode);
        Assert.Equal("01", header.MakerCode);
        Assert.Equal("GTSE01", header.GameId);
        Assert.Equal(GameCubeTestSupport.Title, header.Title);
        Assert.Equal(0, header.DiscNumber);
        Assert.Equal(2, header.Version);
        Assert.True(header.AudioStreaming);
        Assert.Equal(GameCubeRegion.NtscUsa, header.Region);
        Assert.Equal("NTSC-U", header.RegionText);
        Assert.Equal(59.94, header.FramesPerSecond);
        Assert.Equal(GameCubeTestSupport.ExecutableOffset, header.MainExecutableOffset);
        Assert.Equal(GameCubeTestSupport.FileSystemOffset, header.FileSystemOffset);
    }

    [Fact]
    public void Open_ReadsTheAppLoaderHeader()
    {
        using var scope = new DiscScope(compressed: false);

        Assert.Equal("2003/01/01", scope.Disc.AppLoader.Date);
        Assert.Equal(0x8130_0000u, scope.Disc.AppLoader.EntryPoint);
        Assert.Equal(4096, scope.Disc.AppLoader.Size);
        Assert.Equal(256, scope.Disc.AppLoader.TrailerSize);
    }

    [Fact]
    public void Open_RejectsAFileWithoutTheGameCubeMagicWord()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pixelcube-{Guid.NewGuid():N}.iso");
        File.WriteAllBytes(path, new byte[0x1000]);

        try
        {
            var exception = Assert.Throws<InvalidDataException>(() => GameCubeDisc.Open(path));
            Assert.Contains("0xC2339F3D", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_NamesAWiiDiscRatherThanCallingItCorrupt()
    {
        var image = GameCubeTestSupport.CreateDiscImage();
        // Replace the GameCube magic word with the Wii one at its own offset.
        image.AsSpan(0x1C, 4).Clear();
        image[0x18] = 0x5D;
        image[0x19] = 0x1C;
        image[0x1A] = 0x9E;
        image[0x1B] = 0xA3;

        var path = Path.Combine(Path.GetTempPath(), $"pixelcube-{Guid.NewGuid():N}.iso");
        File.WriteAllBytes(path, image);

        try
        {
            var exception = Assert.Throws<InvalidDataException>(() => GameCubeDisc.Open(path));
            Assert.Contains("Wii", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FileSystem_RebuildsPathsFromTheEntryTable()
    {
        using var scope = new DiscScope(compressed: false);

        var fileSystem = scope.Disc.FileSystem;

        Assert.Equal(4, fileSystem.Entries.Count);
        Assert.Equal(2, fileSystem.Files.Count);
        Assert.Equal(["sub/inner.bin", "root.bin"], fileSystem.Files.Select(file => file.Path));

        Assert.True(fileSystem.TryGetFile("sub/inner.bin", out var inner));
        Assert.Equal("inner.bin", inner.Name);
        Assert.Equal(GameCubeTestSupport.InnerFileOffset, inner.Offset);
        Assert.Equal((uint)GameCubeTestSupport.InnerFileLength, inner.Length);
        Assert.False(inner.IsDirectory);
    }

    [Fact]
    public void ReadFile_ReturnsTheBytesTheEntryPointsAt()
    {
        using var scope = new DiscScope(compressed: false);
        Assert.True(scope.Disc.FileSystem.TryGetFile("sub/inner.bin", out var inner));

        var contents = scope.Disc.ReadFile(inner);

        Assert.Equal(GameCubeTestSupport.InnerFileLength, contents.Length);
        Assert.Equal(0xA0, contents[0]);
        Assert.Equal(0xAF, contents[^1]);
    }

    [Fact]
    public void ReadBootExecutable_ParsesTheDolSectionTable()
    {
        using var scope = new DiscScope(compressed: false);

        var executable = scope.Disc.ReadBootExecutable();

        Assert.Equal(GameCubeTestSupport.EntryPoint, executable.EntryPoint);
        Assert.Equal(GameCubeTestSupport.BssAddress, executable.BssAddress);
        Assert.Equal(GameCubeTestSupport.BssSize, executable.BssSize);
        Assert.Equal(2, executable.Sections.Count);

        var text = executable.Sections[0];
        Assert.True(text.IsText);
        Assert.Equal("text0", text.Name);
        Assert.Equal(GameCubeTestSupport.TextLoadAddress, text.LoadAddress);
        Assert.Equal((uint)GameCubeTestSupport.TextSize, text.Size);
        Assert.Equal(0x10, text.Data.Span[0]);

        var data = executable.Sections[1];
        Assert.False(data.IsText);
        Assert.Equal("data0", data.Name);
        Assert.Equal(GameCubeTestSupport.DataLoadAddress, data.LoadAddress);
        Assert.Equal(0xE0, data.Data.Span[0]);
    }

    [Fact]
    public void CompressedAndRawImagesParseIdentically()
    {
        using var raw = new DiscScope(compressed: false);
        using var compressed = new DiscScope(compressed: true);

        Assert.Equal("ISO", raw.Disc.ContainerName);
        Assert.Equal("CISO", compressed.Disc.ContainerName);
        Assert.Equal(raw.Disc.Header, compressed.Disc.Header);
        Assert.Equal(raw.Disc.AppLoader, compressed.Disc.AppLoader);
        Assert.Equal(
            raw.Disc.FileSystem.Files.Select(file => file.Path),
            compressed.Disc.FileSystem.Files.Select(file => file.Path));
        Assert.Equal(
            raw.Disc.ReadBootExecutable().EntryPoint,
            compressed.Disc.ReadBootExecutable().EntryPoint);
    }

    [Fact]
    public void CompressedImage_ReadsAcrossAnAbsentBlock()
    {
        // The payload files live in the third block; the second is all zeroes
        // and is not stored at all, so reaching them proves the block map is
        // followed rather than the file simply being read straight through.
        using var scope = new DiscScope(compressed: true);

        Assert.True(scope.Disc.FileSystem.TryGetFile("root.bin", out var root));
        var contents = scope.Disc.ReadFile(root);

        Assert.Equal(0x50, contents[0]);
        Assert.Equal(0x57, contents[^1]);

        var absentBlock = scope.Disc.Read(GameCubeTestSupport.BlockSize, 64);
        Assert.All(absentBlock, value => Assert.Equal(0, value));
    }

    [Fact]
    public void CompressedImage_ReportsTheExpandedSizeRatherThanTheStoredSize()
    {
        using var raw = new DiscScope(compressed: false);
        using var compressed = new DiscScope(compressed: true);

        Assert.Equal(GameCubeTestSupport.ImageSize, raw.Disc.Length);
        Assert.Equal(GameCubeTestSupport.ImageSize, compressed.Disc.Length);
        Assert.True(compressed.StoredLength < raw.StoredLength + GameCubeTestSupport.BlockSize);
    }

    [Fact]
    public void Inspect_DescribesAReadableDiscAsUnplayable()
    {
        using var scope = new DiscScope(compressed: true);

        var summary = GameCubeDisc.Inspect(scope.Path);

        Assert.True(summary.IsReadable);
        Assert.False(summary.IsPlayable);
        Assert.Equal("GTSE01", summary.GameId);
        Assert.Equal("CISO", summary.ContainerName);
        Assert.Contains("no execution core", summary.CompatibilityMessage, StringComparison.Ordinal);
        Assert.Contains("trace log", summary.CompatibilityMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_ReportsAnUnreadableDiscWithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pixelcube-{Guid.NewGuid():N}.iso");
        File.WriteAllBytes(path, new byte[64]);

        try
        {
            using var trace = new GameCubeTraceLog(
                new GameCubeTraceSettings(GameCubeTraceLevel.Warning, GameCubeTraceChannel.All));

            var summary = GameCubeDisc.Inspect(path, trace);

            Assert.False(summary.IsReadable);
            Assert.False(summary.IsPlayable);
            Assert.Equal("UNKNOWN", summary.RegionText);
            Assert.Contains(
                trace.CaptureRecent(),
                record => record.Message.Contains("could not be read", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Boot_PlacesEverySectionInMainMemoryAndClearsTheBss()
    {
        using var scope = new DiscScope(compressed: true);
        using var trace = new GameCubeTraceLog(
            new GameCubeTraceSettings(GameCubeTraceLevel.Debug, GameCubeTraceChannel.All));
        using var machine = GameCubeMachine.Load(scope.Path, trace);

        var executable = machine.Boot();

        Assert.True(machine.IsBooted);
        Assert.Equal(GameCubeTestSupport.EntryPoint, machine.EntryPoint);
        Assert.Equal(0x10111213u, machine.Memory.ReadUInt32(GameCubeTestSupport.TextLoadAddress));
        Assert.Equal(0xE0E1E2E3u, machine.Memory.ReadUInt32(GameCubeTestSupport.DataLoadAddress));
        Assert.Equal(0u, machine.Memory.ReadUInt32(GameCubeTestSupport.BssAddress));
        Assert.Equal(2, executable.Sections.Count);

        // The same bytes are reachable through the uncached mirror, because
        // both windows address the same physical memory.
        Assert.Equal(
            machine.Memory.ReadUInt32(GameCubeTestSupport.TextLoadAddress),
            machine.Memory.ReadUInt32(
                (GameCubeTestSupport.TextLoadAddress & 0x3FFF_FFFF) | GameCubeMemory.UncachedBase));
    }

    [Fact]
    public void Boot_LeavesTheStateARealStartupWouldHave()
    {
        using var scope = new DiscScope(compressed: false);
        using var trace = new GameCubeTraceLog(
            new GameCubeTraceSettings(GameCubeTraceLevel.Information, GameCubeTraceChannel.All));
        using var machine = GameCubeMachine.Load(scope.Path, trace);

        machine.TraceStartupReport();

        // The globals __init_hardware and OSInit branch on. Zeroes here send a
        // real title down paths no console takes.
        Assert.Equal(GameCubeBootState.BootRomMagic, machine.Memory.ReadUInt32(0x8000_0020));
        Assert.Equal(GameCubeBootState.PhysicalMemorySize, machine.Memory.ReadUInt32(0x8000_0028));
        Assert.Equal(GameCubeBootState.ConsoleType, machine.Memory.ReadUInt32(0x8000_002C));
        Assert.Equal(GameCubeBootState.BusClockSpeed, machine.Memory.ReadUInt32(0x8000_00F8));

        // The IPL copies the head of the disc header to address zero.
        Assert.Equal((uint)'G' << 24 | (uint)'T' << 16 | (uint)'S' << 8 | 'E',
            machine.Memory.ReadUInt32(0x8000_0000));

        // Free memory is left at zero on purpose, because that is what a real
        // handoff leaves: the low water mark is documented as being set by the
        // game's own OSInit, from a linker symbol in its own image. Supplying a
        // value here can only be a guess at something the game already knows,
        // and the guess was wrong in a way nothing could see — the linker puts
        // the stack in space the executable does not declare, immediately above
        // its last section, so "past the executable" landed exactly on it. The
        // game then cleared the arena it had been given and erased its own
        // stack, returning to address zero six and a half million instructions
        // in.
        Assert.Equal(0u, machine.Memory.ReadUInt32(0x8000_0030));

        Assert.Equal(GameCubeBootState.InitialMsr, machine.Cpu.Msr);
        Assert.Equal(GameCubeTestSupport.EntryPoint, machine.Cpu.Pc);
        Assert.Equal(GameCubeBootState.InitialStackPointer, machine.Cpu.Gpr[1]);

        // There is an interpreter now, but not a console.
        Assert.False(GameCubeMachine.HasExecutionCore);
    }

    [Fact]
    public void TheDriveCopiesDiscDataIntoMainMemory()
    {
        // The drive is driven entirely through registers: a command word, a
        // disc offset in units of four bytes, a destination and a length, then
        // the start bit. Getting that unit wrong is silent — a read of the
        // right length lands from the wrong place.
        using var scope = new DiscScope(compressed: false);
        using var trace = new GameCubeTraceLog(GameCubeTraceSettings.Disabled);
        using var machine = GameCubeMachine.Load(scope.Path, trace);
        machine.Boot();

        const uint Destination = 0x8010_0000;
        machine.Memory.WriteUInt32(0xCC00_6008, 0xA800_0000);                       // read
        machine.Memory.WriteUInt32(0xCC00_600C, GameCubeTestSupport.RootFileOffset >> 2);
        machine.Memory.WriteUInt32(0xCC00_6014, Destination);
        machine.Memory.WriteUInt32(0xCC00_6018, GameCubeTestSupport.RootFileLength);
        machine.Memory.WriteUInt32(0xCC00_601C, 3);                                 // start, DMA

        Assert.Equal(0x50, machine.Memory.ReadByte(Destination));
        Assert.Equal(
            0x57,
            machine.Memory.ReadByte(Destination + GameCubeTestSupport.RootFileLength - 1));

        // The drive takes time. Six hundred microseconds is what a read costs
        // before any data moves, and reporting completion sooner than the
        // routine that asked for it can return puts the interrupt into code
        // that has not yet installed a handler for it.
        machine.Memory.Hardware.Advance(600 * 486);

        // Transfer complete is bit 4. Bit 2 is the device error flag, which is
        // what this register used to be told to raise on every successful read.
        Assert.Equal(0u, machine.Memory.ReadUInt32(0xCC00_601C) & 1);
        Assert.NotEqual(0u, machine.Memory.ReadUInt32(0xCC00_6000) & 0x10);
        Assert.Equal(0u, machine.Memory.ReadUInt32(0xCC00_6000) & 0x04);

        // And it says how much it moved: length counts down to zero.
        Assert.Equal(0u, machine.Memory.ReadUInt32(0xCC00_6018));
    }

    [Fact]
    public void TheDriveReadsTheDiscIdentifierFromTheHeader()
    {
        using var scope = new DiscScope(compressed: true);
        using var trace = new GameCubeTraceLog(GameCubeTraceSettings.Disabled);
        using var machine = GameCubeMachine.Load(scope.Path, trace);
        machine.Boot();

        const uint Destination = 0x8010_1000;
        machine.Memory.WriteUInt32(0xCC00_6008, 0x1200_0000);
        machine.Memory.WriteUInt32(0xCC00_6014, Destination);
        machine.Memory.WriteUInt32(0xCC00_6018, 0x20);
        machine.Memory.WriteUInt32(0xCC00_601C, 3);

        Assert.Equal(
            ((uint)'G' << 24) | ((uint)'T' << 16) | ((uint)'S' << 8) | 'E',
            machine.Memory.ReadUInt32(Destination));
    }

    [Fact]
    public void AnImplausibleDvdTransferIsRefusedAndCounted()
    {
        using var scope = new DiscScope(compressed: false);
        using var trace = new GameCubeTraceLog(
            new GameCubeTraceSettings(GameCubeTraceLevel.Warning, GameCubeTraceChannel.All));
        using var machine = GameCubeMachine.Load(scope.Path, trace);
        machine.Boot();

        machine.Memory.WriteUInt32(0xCC00_6008, 0xA800_0000);
        machine.Memory.WriteUInt32(0xCC00_6018, int.MaxValue);
        machine.Memory.WriteUInt32(0xCC00_601C, 3);

        Assert.Contains(
            trace.CaptureCounters(),
            counter => counter.Key == "dvd/bad-transfer");
    }

    [Fact]
    public void Boot_PointsTheOperatingSystemGlobalsAtTheFileTable()
    {
        using var scope = new DiscScope(compressed: true);
        using var machine = GameCubeMachine.Load(scope.Path, new GameCubeTraceLog(
            GameCubeTraceSettings.Disabled));

        machine.Boot();

        var tableAddress = machine.Memory.ReadUInt32(0x8000_0038);
        Assert.NotEqual(0u, tableAddress);
        Assert.Equal(tableAddress, machine.Memory.ReadUInt32(0x8000_0034));

        // The first entry of a file table is the root directory, whose length
        // field is the total entry count.
        Assert.Equal(1u, machine.Memory.ReadByte(tableAddress));
        Assert.Equal(
            (uint)machine.Disc.FileSystem.Entries.Count,
            machine.Memory.ReadUInt32(tableAddress + 8));
    }

    private sealed class DiscScope : IDisposable
    {
        public DiscScope(bool compressed)
        {
            var raw = GameCubeTestSupport.CreateDiscImage();
            var image = compressed ? GameCubeTestSupport.CreateCompressedImage(raw) : raw;
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"pixelcube-{Guid.NewGuid():N}{(compressed ? ".ciso" : ".iso")}");
            File.WriteAllBytes(Path, image);
            StoredLength = image.Length;
            Disc = GameCubeDisc.Open(Path);
        }

        public string Path { get; }

        public int StoredLength { get; }

        public GameCubeDisc Disc { get; }

        public void Dispose()
        {
            Disc.Dispose();
            File.Delete(Path);
        }
    }
}
