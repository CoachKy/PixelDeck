using System.Diagnostics;
using PixelDeck.Emulation.Nes;
using Xunit.Abstractions;

namespace PixelDeck.App.Tests;

public sealed class NesReleaseCertificationTests
{
    /// <summary>
    /// The mapper families the local release matrix must cover.
    /// </summary>
    private static readonly int[] RequiredReleaseMappers = [1, 2, 4, 5, 66];

    private static readonly (int Mapper, int Submapper)[] SupportedVariants =
    [
        (0, 0),
        (1, 0),
        (2, 0),
        (2, 1),
        (2, 2),
        (3, 0),
        (3, 1),
        (3, 2),
        (4, 0),
        (4, 4),
        (5, 0),
        (7, 0),
        (7, 1),
        (7, 2),
        (8, 0),
        (9, 0),
        (10, 0),
        (11, 0),
        (13, 0),
        (15, 0),
        (19, 0),
        (19, 1),
        (19, 2),
        (19, 3),
        (19, 4),
        (19, 5),
        (21, 0),
        (21, 1),
        (21, 2),
        (22, 0),
        (23, 0),
        (23, 1),
        (23, 2),
        (23, 3),
        (32, 0),
        (33, 0),
        (34, 0),
        (34, 1),
        (34, 2),
        (41, 0),
        (64, 0),
        (66, 0),
        (69, 0),
        (71, 0),
        (75, 0),
        (79, 0),
        (90, 0),
        (113, 0),
        (118, 0),
        (119, 0),
        (228, 0),
        (232, 0),
        (240, 0)
    ];

    private readonly ITestOutputHelper _output;

    public NesReleaseCertificationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void SupportedMapperContractIsExactAndEveryVariantBoots()
    {
        var actual = (
            from mapper in Enumerable.Range(0, 256)
            from submapper in Enumerable.Range(0, 16)
            where Cartridge.IsMapperSupported(mapper, submapper)
            select (mapper, submapper)).ToArray();

        Assert.Equal(SupportedVariants, actual);

        foreach (var variant in SupportedVariants)
        {
            using var image = CertificationNesImage.Create(variant.Mapper, variant.Submapper);
            var machine = NesMachine.Load(image.Path);
            for (var frame = 0; frame < 3; frame++)
            {
                Assert.Equal(256 * 240, machine.RunFrame().Length);
            }

            var samples = new float[machine.BufferedAudioSampleCount];
            Assert.True(machine.ReadAudioSamples(samples) > 0);
            Assert.All(samples, sample =>
            {
                Assert.True(float.IsFinite(sample));
                Assert.InRange(sample, -1f, 1f);
            });
            Assert.True(machine.CpuCycles > 0);
            Assert.Equal(0, machine.DroppedAudioSampleCount);
        }
    }

    [Fact]
    public void LocalGameMatrixPassesTheReleaseSoakWhenRequested()
    {
        if (!IsReleaseCertificationRequested())
        {
            _output.WriteLine(
                "Set PIXELDECK_NES_RELEASE_CERTIFICATION=1 to run the long local-game release soak.");
            return;
        }

        var gamePaths = FindLocalGames();
        Assert.True(gamePaths.Length > 0, "The release soak requires local NES games.");

        var inspectedGames = gamePaths
            .Select(path => (Path: path, Info: Cartridge.Inspect(path)))
            .ToArray();
        var unsupported = inspectedGames
            .Where(game => !game.Info.IsSupported)
            .Select(game => $"{Path.GetFileName(game.Path)}: {game.Info.CompatibilityWarning}")
            .ToArray();
        foreach (var exclusion in unsupported)
        {
            _output.WriteLine($"Excluded from supported-image soak: {exclusion}");
        }

        var supportedGames = inspectedGames.Where(game => game.Info.IsSupported).ToArray();
        Assert.True(supportedGames.Length > 0, "The release soak requires supported local NES games.");
        var representedMappers = supportedGames
            .Select(game => game.Info.MapperNumber)
            .Distinct()
            .Order()
            .ToArray();
        Assert.True(
            RequiredReleaseMappers.All(representedMappers.Contains),
            $"The local release matrix must cover mappers 1, 2, 4, 5, and 66; found {string.Join(", ", representedMappers)}.");

        var frames = ResolveSoakFrames();
        foreach (var game in supportedGames)
        {
            RunGameSoak(game.Path, frames);
        }
    }

    private void RunGameSoak(string gamePath, int frames)
    {
        var machine = NesMachine.Load(gamePath);
        var samples = new float[2_048];
        var frameDurations = new long[frames];
        long samplesRead = 0;
        var peak = 0f;
        var timer = Stopwatch.StartNew();

        for (var frame = 0; frame < frames; frame++)
        {
            machine.SetControllerState(1, GetSoakInput(frame));
            var started = Stopwatch.GetTimestamp();
            var pixels = machine.RunFrame();
            frameDurations[frame] = Stopwatch.GetTimestamp() - started;
            Assert.Equal(256 * 240, pixels.Length);

            var read = machine.ReadAudioSamples(samples);
            samplesRead += read;
            for (var index = 0; index < read; index++)
            {
                var sample = samples[index];
                Assert.True(float.IsFinite(sample), $"{Path.GetFileName(gamePath)} produced a non-finite audio sample.");
                Assert.InRange(sample, -1f, 1f);
                peak = Math.Max(peak, Math.Abs(sample));
            }

            if (frame == frames / 2)
            {
                VerifyExactStateRoundTrip(machine, gamePath);
            }
        }

        timer.Stop();
        Array.Sort(frameDurations);
        var p99 = TimeSpan.FromSeconds(
            frameDurations[Math.Max(0, (int)(frames * 0.99) - 1)] / (double)Stopwatch.Frequency);
        var emulatedDuration = TimeSpan.FromSeconds(frames / 60.0988);
        var expectedSamples = (long)(frames * (NesMachine.AudioSampleRate / 60.0988) * 0.98);

        Assert.True(
            timer.Elapsed < emulatedDuration,
            $"{Path.GetFileName(gamePath)} could not sustain realtime: " +
            $"{timer.Elapsed.TotalSeconds:0.00}s host time for {emulatedDuration.TotalSeconds:0.00}s emulated.");
        Assert.True(
            p99 < TimeSpan.FromSeconds(1.0 / 60.0988),
            $"{Path.GetFileName(gamePath)} p99 core frame time was {p99.TotalMilliseconds:0.000}ms.");
        Assert.True(
            samplesRead >= expectedSamples,
            $"{Path.GetFileName(gamePath)} generated {samplesRead} samples; expected at least {expectedSamples}.");
        Assert.True(
            peak >= 0.0001f,
            $"{Path.GetFileName(gamePath)} remained silent throughout the release soak.");
        Assert.Equal(0, machine.DroppedAudioSampleCount);
        Assert.True(machine.CpuCycles > 0);

        _output.WriteLine(
            $"{Path.GetFileName(gamePath)}: mapper {machine.Cartridge.MapperNumber}, " +
            $"{frames} frames, {timer.Elapsed.TotalSeconds:0.00}s host, " +
            $"p99={p99.TotalMilliseconds:0.000}ms, audio={samplesRead} samples, peak={peak:0.000}.");
    }

    private static void VerifyExactStateRoundTrip(NesMachine machine, string gamePath)
    {
        var state = machine.SaveState();
        var expectedProgramCounter = machine.ProgramCounter;
        var expectedCycles = machine.CpuCycles;
        var expectedFrame = machine.RunFrame().ToArray();
        machine.ClearAudioSamples();

        machine.LoadState(state);

        Assert.Equal(expectedProgramCounter, machine.ProgramCounter);
        Assert.Equal(expectedCycles, machine.CpuCycles);
        Assert.Equal(expectedFrame, machine.RunFrame().ToArray());
        Assert.True(
            machine.BufferedAudioSampleCount > 0,
            $"{Path.GetFileName(gamePath)} did not resume audio generation after restoring state.");
        machine.ClearAudioSamples();
    }

    private static NesButton GetSoakInput(int frame)
    {
        if (frame is >= 120 and < 150)
        {
            return NesButton.Start;
        }

        if (frame >= 240 && frame % 900 < 30)
        {
            return NesButton.Start;
        }

        return (frame % 1_200) switch
        {
            >= 300 and < 420 => NesButton.Right | NesButton.A,
            >= 600 and < 720 => NesButton.Left | NesButton.B,
            >= 900 and < 1_020 => NesButton.Up | NesButton.A,
            _ => NesButton.None
        };
    }

    private static bool IsReleaseCertificationRequested() =>
        string.Equals(
            Environment.GetEnvironmentVariable("PIXELDECK_NES_RELEASE_CERTIFICATION"),
            "1",
            StringComparison.Ordinal);

    private static int ResolveSoakFrames()
    {
        var configured = Environment.GetEnvironmentVariable("PIXELDECK_NES_SOAK_FRAMES");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return 18_000;
        }

        if (int.TryParse(configured, out var frames) && frames >= 3_600)
        {
            return frames;
        }

        throw new InvalidOperationException(
            "PIXELDECK_NES_SOAK_FRAMES must be at least 3600 frames (one emulated minute).");
    }

    private static string[] FindLocalGames()
    {
        var configured = Environment.GetEnvironmentVariable("PIXELDECK_GAMES_FOLDER");
        var gamesFolder = string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Games"))
            : Path.GetFullPath(configured);
        var nintendoFolder = Path.Combine(gamesFolder, "Nintendo");
        return Directory.Exists(nintendoFolder)
            ? Directory.EnumerateFiles(nintendoFolder, "*.nes", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
    }

    private sealed class CertificationNesImage : IDisposable
    {
        private CertificationNesImage(string directoryPath, string path)
        {
            DirectoryPath = directoryPath;
            Path = path;
        }

        public string DirectoryPath { get; }

        public string Path { get; }

        public static CertificationNesImage Create(int mapper, int submapper)
        {
            var (programBanks, characterBanks) = mapper switch
            {
                0 => (1, 1),
                1 => (4, 1),
                2 => (4, 1),
                3 => (2, 4),
                4 => (4, 2),
                5 => (16, 16),
                7 => (8, 0),
                8 => (8, 4),
                9 => (8, 16),
                10 => (8, 16),
                11 => (8, 16),
                13 => (2, 0),
                15 => (16, 0),
                19 => (8, 16),
                21 => (16, 16),
                22 => (8, 16),
                23 => (8, 16),
                32 => (8, 8),
                33 => (8, 8),
                34 => (8, 8),
                41 => (8, 8),
                64 => (8, 8),
                66 => (8, 4),
                69 => (8, 16),
                71 => (8, 0),
                75 => (8, 16),
                79 => (8, 8),
                90 => (16, 16),
                113 => (8, 16),
                118 => (8, 8),
                119 => (8, 8),
                228 => (8, 8),
                232 => (16, 0),
                240 => (8, 8),
                _ => throw new ArgumentOutOfRangeException(nameof(mapper))
            };
            var directoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "PixelDeck.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            var path = System.IO.Path.Combine(directoryPath, $"mapper-{mapper}-{submapper}.nes");
            var programLength = programBanks * 16_384;
            var image = new byte[16 + programLength + (characterBanks * 8_192)];
            image[0] = (byte)'N';
            image[1] = (byte)'E';
            image[2] = (byte)'S';
            image[3] = 0x1A;
            image[4] = (byte)programBanks;
            image[5] = (byte)characterBanks;
            image[6] = (byte)((mapper & 0x0F) << 4);
            image[7] = (byte)((mapper & 0xF0) | 0x08);
            image[8] = (byte)((submapper << 4) | ((mapper >> 8) & 0x0F));

            for (var offset = 16; offset < 16 + programLength; offset += 8_192)
            {
                image[offset] = 0x78; // SEI
                image[offset + 1] = 0xEA; // NOP
                image[offset + 2] = 0x4C; // JMP $8001
                image[offset + 3] = 0x01;
                image[offset + 4] = 0x80;
            }

            for (var bankOffset = 0; bankOffset < programLength; bankOffset += 16_384)
            {
                var vectorOffset = 16 + bankOffset + 0x3FFA;
                WriteVector(image, vectorOffset, 0x8001);
                WriteVector(image, vectorOffset + 2, 0x8000);
                WriteVector(image, vectorOffset + 4, 0x8001);
            }

            File.WriteAllBytes(path, image);
            return new CertificationNesImage(directoryPath, path);
        }

        public void Dispose()
        {
            var parent = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PixelDeck.Tests"))
                .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
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

        private static void WriteVector(byte[] image, int offset, ushort address)
        {
            image[offset] = (byte)address;
            image[offset + 1] = (byte)(address >> 8);
        }
    }
}
