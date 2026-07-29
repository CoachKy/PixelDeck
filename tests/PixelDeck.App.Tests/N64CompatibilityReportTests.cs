using PixelDeck.N64Compatibility;

namespace PixelDeck.App.Tests;

public sealed class N64CompatibilityReportTests
{
    [Fact]
    public void ClassifierKeepsInvalidImagesSeparateFromRuntimeFailures()
    {
        Assert.Equal(
            CompatibilityStatus.Invalid,
            CompatibilityClassifier.Classify(false, [], []));
        Assert.Equal(
            CompatibilityStatus.Failed,
            CompatibilityClassifier.Classify(true, ["crash"], []));
        Assert.Equal(
            CompatibilityStatus.Warning,
            CompatibilityClassifier.Classify(true, [], ["unverified"]));
        Assert.Equal(
            CompatibilityStatus.Pass,
            CompatibilityClassifier.Classify(true, [], []));
    }

    [Fact]
    public void ReportSummarizesHardwareProfilesDuplicatesAndFirstBlockers()
    {
        var options = new CompatibilityOptions(
            GamesFolder: @"C:\Games",
            OutputFolder: @"C:\Reports",
            FieldsPerGame: 120,
            Parallelism: 4,
            Filter: null,
            CaptureFlaggedFrames: true,
            CaptureGraphicsTasks: false,
            Strict: false);
        var games = new[]
        {
            CreateGame("Mario, 64.z64", "AAA", CompatibilityStatus.Pass, "Cic6102"),
            CreateGame("Duplicate.z64", "AAA", CompatibilityStatus.Warning, "Cic6102", "unverified"),
            CreateGame("Crash.z64", "BBB", CompatibilityStatus.Failed, "Cic6105", "bad opcode")
        };

        var report = N64CompatibilityRunner.CreateReport(
            options,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            games);
        var csv = CompatibilityReportWriter.BuildCsv(games);
        var markdown = CompatibilityReportWriter.BuildMarkdown(report);

        Assert.Equal(3, report.Summary.Total);
        Assert.Equal(2, report.Summary.UniqueImages);
        Assert.Equal(1, report.Summary.Passed);
        Assert.Equal(1, report.Summary.Warnings);
        Assert.Equal(1, report.Summary.Failed);
        Assert.Equal(2, report.HardwareProfiles.Count);
        Assert.Equal("bad opcode", Assert.Single(report.Blockers).Finding);
        Assert.Contains("\"Mario, 64.z64\"", csv);
        Assert.Contains("rdpOtherModeLow", csv);
        Assert.Contains("framebufferBlended", csv);
        Assert.Contains("graphicsCapture", csv);
        Assert.Contains("Hardware profile coverage", markdown);
        Assert.Contains("First blockers", markdown);
    }

    [Fact]
    public void ReportWriterCreatesJsonCsvMarkdownAndDynamicSizeBitmap()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "PixelDeck-N64CompatibilityTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var options = new CompatibilityOptions(
                temporaryRoot,
                temporaryRoot,
                120,
                1,
                null,
                true,
                false,
                false);
            var report = N64CompatibilityRunner.CreateReport(
                options,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                [CreateGame("Game.z64", "ABC", CompatibilityStatus.Pass, "Cic6102")]);

            var paths = CompatibilityReportWriter.Write(report, temporaryRoot);
            var bitmapPath = Path.Combine(temporaryRoot, "frame.bmp");
            FrameCapture.WriteBitmap(
                bitmapPath,
                [0xFF112233, 0xFF445566, 0xFF778899, 0xFFAABBCC],
                width: 2,
                height: 2);

            Assert.True(File.Exists(paths.JsonPath));
            Assert.True(File.Exists(paths.CsvPath));
            Assert.True(File.Exists(paths.MarkdownPath));
            Assert.Equal([(byte)'B', (byte)'M'], File.ReadAllBytes(bitmapPath)[..2]);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static GameCompatibilityResult CreateGame(
        string path,
        string hash,
        CompatibilityStatus status,
        string cic,
        params string[] findings) =>
        new()
        {
            RelativePath = path,
            FileName = Path.GetFileName(path),
            Sha256 = hash,
            Status = status,
            Title = Path.GetFileNameWithoutExtension(path),
            GameCode = "NSME",
            Region = "Ntsc",
            Cic = cic,
            SaveType = "Eeprom4Kbit",
            SourceByteOrder = "BigEndian",
            ReachedCartridgeEntryPoint = true,
            FieldsCompleted = status is CompatibilityStatus.Pass or CompatibilityStatus.Warning
                ? 120
                : 30,
            HostFieldsPerSecond = 120,
            P99FieldMilliseconds = 10,
            InstructionsExecuted = 1,
            ProgramCounter = 0x80000400,
            MaximumDistinctColors = 4,
            DistinctCheckpointFrames = 2,
            AudioSamples = 1,
            AudioPeak = 0.5f,
            SaveStateDeterministic = true,
            Findings = findings
        };
}
