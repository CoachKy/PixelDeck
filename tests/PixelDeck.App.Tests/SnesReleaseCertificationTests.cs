using System.Diagnostics;
using PixelDeck.Emulation.Snes;
using Xunit.Abstractions;

namespace PixelDeck.App.Tests;

public sealed class SnesReleaseCertificationTests
{
    private readonly ITestOutputHelper _output;

    public SnesReleaseCertificationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(SnesMapMode.LoRom, 0x20, 0x00)]
    [InlineData(SnesMapMode.LoRom, 0x30, 0x01)]
    [InlineData(SnesMapMode.LoRom, 0x20, 0x02)]
    [InlineData(SnesMapMode.HiRom, 0x21, 0x00)]
    [InlineData(SnesMapMode.HiRom, 0x31, 0x01)]
    [InlineData(SnesMapMode.HiRom, 0x21, 0x02)]
    [InlineData(SnesMapMode.ExHiRom, 0x35, 0x02)]
    public void StandardNtscCartridgeContractBoots(
        SnesMapMode mapMode,
        byte mapModeByte,
        byte cartridgeType)
    {
        using var image = CertificationSnesImage.Create(
            mapMode,
            mapModeByte,
            cartridgeType,
            destinationCode: 0x01);
        var info = SnesCartridge.Inspect(image.Path);

        Assert.True(info.IsSupported, info.CompatibilityMessage);
        Assert.Equal(mapMode, info.MapMode);
        Assert.Equal(cartridgeType == 0x02, info.HasBatteryBackedRam);
        Assert.False(info.IsPal);

        var machine = SnesMachine.Load(image.Path);
        for (var frame = 0; frame < 3; frame++)
        {
            Assert.Equal(256 * 224, machine.RunFrame().Length);
        }

        Assert.True(machine.CpuCycles > 0);
        Assert.Contains(machine.CurrentFrame.ToArray(), color => color != 0xFF000000u);
        Assert.Equal(0, machine.DroppedAudioSampleCount);
    }

    [Fact]
    public void ExHiRomMapsBothRomRegionsAndHiRomSaveRam()
    {
        using var image = CertificationSnesImage.Create(
            SnesMapMode.ExHiRom,
            mapModeByte: 0x35,
            cartridgeType: 0x02,
            destinationCode: 0x01);
        var bytes = File.ReadAllBytes(image.Path);
        bytes[0x000000] = 0xC0;
        bytes[0x3FFFFF] = 0xFF;
        bytes[0x400000] = 0x40;
        bytes[0x5F0000] = 0x5F;
        File.WriteAllBytes(image.Path, bytes);

        var cartridge = SnesCartridge.Load(image.Path);

        Assert.Equal(SnesMapMode.ExHiRom, cartridge.Info.MapMode);
        Assert.Equal(0xC0, cartridge.Read(0xC00000));
        Assert.Equal(0xFF, cartridge.Read(0xFFFFFF));
        Assert.Equal(0x40, cartridge.Read(0x400000));
        Assert.Equal(0x5F, cartridge.Read(0x5F0000));
        Assert.Equal(0x40, cartridge.Read(0x600000));
        Assert.Equal(0x78, cartridge.Read(0x008000));

        cartridge.Write(0x206000, 0x5A);
        Assert.Equal(0x5A, cartridge.Read(0xA06000));
    }

    [Fact]
    public void NonPowerOfTwoHiRomUsesPhysicalMirroringInsteadOfModulo()
    {
        using var image = CertificationSnesImage.Create(
            SnesMapMode.HiRom,
            mapModeByte: 0x31,
            cartridgeType: 0x00,
            destinationCode: 0x01);
        var bytes = File.ReadAllBytes(image.Path);
        Array.Resize(ref bytes, 3 * 1024 * 1024);
        bytes[0x000000] = 0x10;
        bytes[0x100000] = 0x20;
        bytes[0x200000] = 0x30;
        File.WriteAllBytes(image.Path, bytes);

        var cartridge = SnesCartridge.Load(image.Path);

        Assert.Equal(0x10, cartridge.Read(0xC00000));
        Assert.Equal(0x20, cartridge.Read(0xD00000));
        Assert.Equal(0x30, cartridge.Read(0xE00000));
        Assert.Equal(0x30, cartridge.Read(0xF00000));
    }

    [Fact]
    public void Cx4CartridgesExposeMirroredRamCommandsRomDmaAndSaveState()
    {
        using var image = CertificationSnesImage.Create(
            SnesMapMode.LoRom,
            mapModeByte: 0x20,
            cartridgeType: 0xF3,
            destinationCode: 0x01);
        var info = SnesCartridge.Inspect(image.Path);

        Assert.True(info.IsSupported, info.CompatibilityMessage);
        Assert.Contains("CX4", info.CompatibilityMessage);
        Assert.False(info.HasBatteryBackedRam);

        var cartridge = SnesCartridge.Load(image.Path);
        var bus = new SnesBus(cartridge);

        // Signed 24-bit multiply: 2 * 3 = 6.
        bus.Write(0x007F80, 0x02);
        bus.Write(0x007F81, 0x00);
        bus.Write(0x007F82, 0x00);
        bus.Write(0x007F83, 0x03);
        bus.Write(0x007F84, 0x00);
        bus.Write(0x007F85, 0x00);
        bus.Write(0x007F4D, 0x02);
        bus.Write(0x007F4F, 0x25);

        Assert.Equal(0x06, bus.Read(0x007F80));
        Assert.Equal(0x00, bus.Read(0x007F81));
        Assert.Equal(0x00, bus.Read(0x007F82));
        Assert.Equal(0x00, bus.Read(0x007F5E));
        Assert.Equal(0x06, bus.Read(0x807F80));
        Assert.Equal(1, bus.ReadCx4CommandCount(0x25));

        // CX4 ROM DMA copies from raw LoROM source $00:8000 into CX4 RAM.
        bus.Write(0x007F40, 0x00);
        bus.Write(0x007F41, 0x80);
        bus.Write(0x007F42, 0x00);
        bus.Write(0x007F43, 0x02);
        bus.Write(0x007F44, 0x00);
        bus.Write(0x007F45, 0x00);
        bus.Write(0x007F46, 0x60);
        bus.Write(0x007F47, 0x00);

        Assert.Equal(0x78, bus.Read(0x006000));
        Assert.Equal(0xA9, bus.Read(0x006001));

        using var state = new MemoryStream();
        using (var writer = new BinaryWriter(state, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bus.SaveState(writer);
        }

        bus.Write(0x006000, 0xCC);
        bus.Write(0x007F4F, 0x25);
        Assert.Equal(2, bus.ReadCx4CommandCount(0x25));
        state.Position = 0;
        using (var reader = new BinaryReader(state, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bus.LoadState(reader);
        }

        Assert.Equal(0x78, bus.Read(0x006000));
        Assert.Equal(1, bus.ReadCx4CommandCount(0x25));
    }

    [Fact]
    public void UnsupportedHardwareIsExcludedFromTheStableEnvelope()
    {
        using var enhancement = CertificationSnesImage.Create(
            SnesMapMode.LoRom,
            mapModeByte: 0x20,
            cartridgeType: 0x13,
            destinationCode: 0x01);

        var enhancementInfo = SnesCartridge.Inspect(enhancement.Path);

        Assert.False(enhancementInfo.IsSupported);
        Assert.Contains("enhancement-chip", enhancementInfo.CompatibilityMessage);
        Assert.Throws<NotSupportedException>(() => SnesMachine.Load(enhancement.Path));
    }

    [Fact]
    public void PalCartridgesAreSupportedWithPalTimingReported()
    {
        // PAL only changes field rate and scanline count (both already
        // read from SnesCartridgeInfo.IsPal); it isn't a distinct hardware
        // envelope, so PAL carts run rather than being rejected outright.
        using var pal = CertificationSnesImage.Create(
            SnesMapMode.LoRom,
            mapModeByte: 0x20,
            cartridgeType: 0x00,
            destinationCode: 0x02);

        var palInfo = SnesCartridge.Inspect(pal.Path);

        Assert.True(palInfo.IsSupported);
        Assert.True(palInfo.IsPal);
        Assert.Contains("PAL", palInfo.CompatibilityMessage);
        Assert.NotNull(SnesMachine.Load(pal.Path));
    }

    [Fact]
    public void LocalExHiRomImagesCompleteBootFramesWhenPresent()
    {
        var images = FindLocalGames()
            .Where(path =>
            {
                try
                {
                    return SnesCartridge.Inspect(path).MapMode == SnesMapMode.ExHiRom;
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            })
            .ToArray();

        foreach (var path in images)
        {
            var machine = SnesMachine.Load(path);
            for (var frame = 0; frame < 120; frame++)
            {
                Assert.Equal(256 * 224, machine.RunFrame().Length);
            }

            Assert.True(machine.CpuCycles > 0);
            Assert.Equal(0, machine.BrkCount);
            Assert.Equal(0, machine.CopCount);
            _output.WriteLine(
                $"{Path.GetFileName(path)}: ExHiROM booted 120 frames at PC ${machine.ProgramAddress:X6}.");
        }
    }

    [Fact]
    public void LocalCx4ImagesReachCommandDrivenScenesWhenPresent()
    {
        var images = FindLocalGames()
            .Where(path =>
            {
                try
                {
                    var info = SnesCartridge.Inspect(path);
                    return info.CartridgeType == 0xF3 && info.IsSupported;
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            })
            .ToArray();

        foreach (var path in images)
        {
            var machine = SnesMachine.Load(path);
            var audio = new float[2_048];
            var audioPeak = 0f;
            var maximumColors = 0;
            var visibleFrames = 0;
            const int frames = 2_400;
            for (var frame = 0; frame < frames; frame++)
            {
                machine.SetControllerState(1, GetSoakInput(frame));
                var pixels = machine.RunFrame();
                Assert.Equal(256 * 224, pixels.Length);
                if (frame % 30 == 0)
                {
                    maximumColors = Math.Max(maximumColors, CountDistinctColors(pixels));
                }
                if (!machine.IsDisplayBlanked)
                {
                    visibleFrames++;
                }

                int read;
                while ((read = machine.ReadAudioSamples(audio)) > 0)
                {
                    for (var index = 0; index < read; index++)
                    {
                        audioPeak = Math.Max(audioPeak, Math.Abs(audio[index]));
                    }
                }
            }

            var cx4Commands = Enumerable.Range(0, 256)
                .Select(command => (
                    Command: command,
                    Count: machine.ReadCx4CommandCount((byte)command)))
                .Where(entry => entry.Count != 0)
                .Select(entry => $"${entry.Command:X2}={entry.Count}");
            _output.WriteLine(
                $"{Path.GetFileName(path)}: PC=${machine.ProgramAddress:X6}, " +
                $"BRK={machine.BrkCount}, first BRK=${machine.FirstBrkAddress:X6}, " +
                $"COP={machine.CopCount}, PPU writes={machine.PpuRegisterWriteCount}, " +
                $"visible={visibleFrames}, colors={maximumColors}, audio peak={audioPeak:0.0000}, " +
                $"CX4=[{string.Join(", ", cx4Commands)}].");
            Assert.True(machine.CpuCycles > 0);
            Assert.True(
                machine.BrkCount == 0,
                $"{Path.GetFileName(path)} executed {machine.BrkCount} BRKs; " +
                $"first=${machine.FirstBrkAddress:X6}, current PC=${machine.ProgramAddress:X6}.");
            Assert.Equal(0, machine.CopCount);
            Assert.True(machine.PpuRegisterWriteCount > 0);
            Assert.False(machine.IsDisplayBlanked);
            Assert.True(
                visibleFrames >= 300 && maximumColors >= 8,
                $"{Path.GetFileName(path)} did not produce a useful visible CX4 boot sequence.");
            Assert.True(
                audioPeak >= 0.0001f,
                $"{Path.GetFileName(path)} did not produce audible S-DSP output.");
            Assert.True(
                machine.ReadCx4CommandCount(0x00) > 0,
                $"{Path.GetFileName(path)} never exercised the CX4 sprite command.");
            Assert.Equal(ushort.MaxValue, machine.ApuFirstUnsupportedAddress);
            Assert.Equal(0, machine.DroppedAudioSampleCount);
            VerifyExactStateRoundTrip(machine, path);
            _output.WriteLine(
                $"{Path.GetFileName(path)}: CX4 cartridge passed {frames} frames at PC ${machine.ProgramAddress:X6}.");
        }
    }

    [Fact]
    public void LocalGameMatrixPassesTheReleaseSoakWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PIXELDECK_SNES_RELEASE_CERTIFICATION"),
                "1",
                StringComparison.Ordinal))
        {
            _output.WriteLine(
                "Set PIXELDECK_SNES_RELEASE_CERTIFICATION=1 to run the long local-game release soak.");
            return;
        }

        var candidates = FindLocalGames();
        Assert.NotEmpty(candidates);

        var supported = new List<(string Path, SnesCartridgeInfo Info)>();
        foreach (var path in candidates)
        {
            try
            {
                var info = SnesCartridge.Inspect(path);
                if (info.IsSupported)
                {
                    supported.Add((path, info));
                }
                else
                {
                    _output.WriteLine(
                        $"Excluded {Path.GetFileName(path)}: {info.CompatibilityMessage}");
                }
            }
            catch (InvalidDataException exception)
            {
                _output.WriteLine($"Excluded {Path.GetFileName(path)}: {exception.Message}");
            }
        }

        Assert.True(
            supported.Count >= 2,
            "The local release matrix requires at least two supported standard SNES games.");
        var maps = supported.Select(item => item.Info.MapMode).Distinct().ToArray();
        Assert.Contains(SnesMapMode.LoRom, maps);
        Assert.Contains(SnesMapMode.HiRom, maps);

        var frames = ResolveSoakFrames();
        foreach (var game in supported)
        {
            RunGameSoak(game.Path, frames);
        }
    }

    private void RunGameSoak(string gamePath, int frames)
    {
        var machine = SnesMachine.Load(gamePath);
        var samples = new float[2_048];
        var frameDurations = new long[frames];
        long samplesRead = 0;
        var peak = 0f;
        var maximumColors = 0;
        var timer = Stopwatch.StartNew();

        for (var frame = 0; frame < frames; frame++)
        {
            machine.SetControllerState(1, GetSoakInput(frame));
            var started = Stopwatch.GetTimestamp();
            var pixels = machine.RunFrame();
            frameDurations[frame] = Stopwatch.GetTimestamp() - started;
            Assert.Equal(256 * 224, pixels.Length);
            if (frame % 60 == 0)
            {
                maximumColors = Math.Max(maximumColors, CountDistinctColors(pixels));
            }

            int read;
            while ((read = machine.ReadAudioSamples(samples)) > 0)
            {
                samplesRead += read;
                for (var index = 0; index < read; index++)
                {
                    var sample = samples[index];
                    Assert.True(
                        float.IsFinite(sample),
                        $"{Path.GetFileName(gamePath)} produced a non-finite audio sample.");
                    Assert.InRange(sample, -1f, 1f);
                    peak = Math.Max(peak, Math.Abs(sample));
                }
            }

            if (frame == frames / 2)
            {
                VerifyExactStateRoundTrip(machine, gamePath);
            }
        }

        timer.Stop();
        Array.Sort(frameDurations);
        var p99 = TimeSpan.FromSeconds(
            frameDurations[Math.Max(0, (int)(frames * 0.99) - 1)] /
            (double)Stopwatch.Frequency);
        var emulatedDuration = TimeSpan.FromSeconds(frames / machine.FramesPerSecond);
        var expectedSamples = (long)(
            frames *
            (SnesMachine.AudioSampleRate / machine.FramesPerSecond) *
            2 *
            0.98);

        Assert.True(
            timer.Elapsed < emulatedDuration,
            $"{Path.GetFileName(gamePath)} could not sustain realtime: " +
            $"{timer.Elapsed.TotalSeconds:0.00}s host for {emulatedDuration.TotalSeconds:0.00}s emulated.");
        Assert.True(
            p99 < TimeSpan.FromSeconds(1.0 / machine.FramesPerSecond),
            $"{Path.GetFileName(gamePath)} p99 core frame time was {p99.TotalMilliseconds:0.000}ms.");
        Assert.True(
            samplesRead >= expectedSamples,
            $"{Path.GetFileName(gamePath)} generated {samplesRead} samples; expected at least {expectedSamples}.");
        Assert.True(peak >= 0.0001f, $"{Path.GetFileName(gamePath)} remained silent.");
        Assert.True(maximumColors >= 3, $"{Path.GetFileName(gamePath)} never produced a useful visible frame.");
        Assert.Equal(ushort.MaxValue, machine.ApuFirstUnsupportedAddress);
        Assert.Equal(0, machine.DroppedAudioSampleCount);
        Assert.True(machine.CpuCycles > 0);

        _output.WriteLine(
            $"{Path.GetFileName(gamePath)}: {machine.Cartridge.Info.MapMode}, " +
            $"{frames} frames, {timer.Elapsed.TotalSeconds:0.00}s host, " +
            $"p99={p99.TotalMilliseconds:0.000}ms, audio={samplesRead} samples, " +
            $"peak={peak:0.000}, max colors={maximumColors}.");
    }

    private static void VerifyExactStateRoundTrip(SnesMachine machine, string gamePath)
    {
        var state = machine.SaveState();
        var expectedAddress = machine.ProgramAddress;
        var expectedCycles = machine.CpuCycles;
        var expectedFrame = machine.RunFrame().ToArray();
        machine.ClearAudioSamples();

        machine.LoadState(state);

        Assert.Equal(expectedAddress, machine.ProgramAddress);
        Assert.Equal(expectedCycles, machine.CpuCycles);
        Assert.Equal(expectedFrame, machine.RunFrame().ToArray());
        Assert.True(
            machine.BufferedAudioSampleCount > 0,
            $"{Path.GetFileName(gamePath)} did not resume audio after state restore.");
        machine.ClearAudioSamples();
    }

    private static int CountDistinctColors(ReadOnlySpan<uint> pixels)
    {
        var colors = new HashSet<uint>();
        foreach (var pixel in pixels)
        {
            colors.Add(pixel);
        }

        return colors.Count;
    }

    private static SnesButton GetSoakInput(int frame)
    {
        if (frame is >= 120 and < 150 || frame >= 240 && frame % 900 < 30)
        {
            return SnesButton.Start;
        }

        return (frame % 1_200) switch
        {
            >= 300 and < 420 => SnesButton.Right | SnesButton.B | SnesButton.Y,
            >= 600 and < 720 => SnesButton.Left | SnesButton.A,
            >= 900 and < 1_020 => SnesButton.Up | SnesButton.X,
            _ => SnesButton.None
        };
    }

    private static int ResolveSoakFrames()
    {
        var configured = Environment.GetEnvironmentVariable("PIXELDECK_SNES_SOAK_FRAMES");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return 18_000;
        }

        if (int.TryParse(configured, out var frames) && frames >= 3_600)
        {
            return frames;
        }

        throw new InvalidOperationException(
            "PIXELDECK_SNES_SOAK_FRAMES must be at least 3600 frames (one emulated minute).");
    }

    private static string[] FindLocalGames()
    {
        var configured = Environment.GetEnvironmentVariable("PIXELDECK_GAMES_FOLDER");
        var gamesFolder = string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Games"))
            : Path.GetFullPath(configured);
        var superNintendoFolder = Path.Combine(gamesFolder, "SuperNintendo");
        return Directory.Exists(superNintendoFolder)
            ? Directory.EnumerateFiles(superNintendoFolder, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path) is ".sfc" or ".smc")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
    }

    private sealed class CertificationSnesImage : IDisposable
    {
        private CertificationSnesImage(string directoryPath, string path)
        {
            DirectoryPath = directoryPath;
            Path = path;
        }

        public string DirectoryPath { get; }

        public string Path { get; }

        public static CertificationSnesImage Create(
            SnesMapMode mapMode,
            byte mapModeByte,
            byte cartridgeType,
            byte destinationCode)
        {
            var directoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "PixelDeck.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            var path = System.IO.Path.Combine(
                directoryPath,
                $"{mapMode}-{mapModeByte:X2}-{cartridgeType:X2}.sfc");
            var image = new byte[mapMode switch
            {
                SnesMapMode.LoRom => 32 * 1_024,
                SnesMapMode.HiRom => 64 * 1_024,
                SnesMapMode.ExHiRom => 6 * 1_024 * 1_024,
                _ => throw new ArgumentOutOfRangeException(nameof(mapMode))
            }];
            var programOffset = mapMode switch
            {
                SnesMapMode.LoRom => 0,
                SnesMapMode.HiRom => 0x8000,
                SnesMapMode.ExHiRom => 0x408000,
                _ => throw new ArgumentOutOfRangeException(nameof(mapMode))
            };
            byte[] program =
            [
                0x78,             // SEI
                0xA9, 0x0F,       // LDA #$0F
                0x8D, 0x00, 0x21, // STA $2100 - display on
                0xA9, 0x1F,
                0x8D, 0x22, 0x21, // CGRAM red, low
                0xA9, 0x00,
                0x8D, 0x22, 0x21, // CGRAM red, high
                0xDB              // STP
            ];
            program.CopyTo(image, programOffset);

            var header = mapMode switch
            {
                SnesMapMode.LoRom => 0x7FC0,
                SnesMapMode.HiRom => 0xFFC0,
                SnesMapMode.ExHiRom => 0x40FFC0,
                _ => throw new ArgumentOutOfRangeException(nameof(mapMode))
            };
            "PIXELSNES CERT ROM   ".Select(character => (byte)character).ToArray().CopyTo(image, header);
            image[header + 0x15] = mapModeByte;
            image[header + 0x16] = cartridgeType;
            image[header + 0x17] = mapMode switch
            {
                SnesMapMode.LoRom => 0x05,
                SnesMapMode.HiRom => 0x06,
                SnesMapMode.ExHiRom => 0x0D,
                _ => throw new ArgumentOutOfRangeException(nameof(mapMode))
            };
            image[header + 0x18] = cartridgeType == 0x00 ? (byte)0 : (byte)0x03;
            image[header + 0x19] = destinationCode;
            image[header + 0x1C] = 0xCB;
            image[header + 0x1D] = 0xED;
            image[header + 0x1E] = 0x34;
            image[header + 0x1F] = 0x12;
            image[header + 0x3C] = 0x00;
            image[header + 0x3D] = 0x80;
            File.WriteAllBytes(path, image);
            return new CertificationSnesImage(directoryPath, path);
        }

        public void Dispose()
        {
            var parent = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PixelDeck.Tests"))
                .TrimEnd(System.IO.Path.DirectorySeparatorChar) +
                System.IO.Path.DirectorySeparatorChar;
            var resolved = System.IO.Path.GetFullPath(DirectoryPath);
            if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to remove a directory outside the PixelDeck test area.");
            }

            if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
