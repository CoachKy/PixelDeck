using PixelDeck.NesCompatibility;

namespace PixelDeck.App.Tests;

public sealed class NesCompatibilityReportTests
{
    [Fact]
    public void ClassifierKeepsUnsupportedAndInvalidSeparateFromRuntimeFailures()
    {
        Assert.Equal(
            CompatibilityStatus.Invalid,
            CompatibilityClassifier.Classify(false, false, [], []));
        Assert.Equal(
            CompatibilityStatus.Unsupported,
            CompatibilityClassifier.Classify(true, false, [], []));
        Assert.Equal(
            CompatibilityStatus.Failed,
            CompatibilityClassifier.Classify(true, true, ["crash"], []));
        Assert.Equal(
            CompatibilityStatus.Warning,
            CompatibilityClassifier.Classify(true, true, [], ["silent"]));
        Assert.Equal(
            CompatibilityStatus.Pass,
            CompatibilityClassifier.Classify(true, true, [], []));
    }

    [Fact]
    public void ReportSummarizesStatusesMappersDuplicatesAndEscapesCsv()
    {
        var options = new CompatibilityOptions(
            GamesFolder: @"C:\Games",
            OutputFolder: @"C:\Reports",
            FramesPerGame: 600,
            Parallelism: 4,
            Filter: null,
            CaptureFlaggedFrames: true,
            Strict: false);
        var games = new[]
        {
            CreateGame("A, Game.nes", "AAA", CompatibilityStatus.Pass, mapper: 4),
            CreateGame("Duplicate.nes", "AAA", CompatibilityStatus.Warning, mapper: 4, "silent"),
            CreateGame("Rare.nes", "BBB", CompatibilityStatus.Unsupported, mapper: 552, "unsupported")
        };

        var report = NesCompatibilityRunner.CreateReport(
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
        Assert.Equal(1, report.Summary.Unsupported);
        Assert.Contains("\"A, Game.nes\"", csv);
        Assert.Contains("## Warnings", markdown);
        Assert.Contains("Mapper coverage", markdown);
        Assert.Contains("552", markdown);
    }

    [Fact]
    public void ReportWriterCreatesJsonCsvMarkdownAndStandardBitmapEvidence()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "PixelDeck-CompatibilityTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var options = new CompatibilityOptions(
                temporaryRoot,
                temporaryRoot,
                600,
                1,
                null,
                true,
                false);
            var report = NesCompatibilityRunner.CreateReport(
                options,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                [CreateGame("Game.nes", "ABC", CompatibilityStatus.Pass, mapper: 0)]);

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
        int mapper,
        params string[] findings) =>
        new()
        {
            RelativePath = path,
            FileName = Path.GetFileName(path),
            Sha256 = hash,
            Status = status,
            Mapper = mapper,
            Submapper = 0,
            TimingMode = "Ntsc",
            FramesCompleted = status is CompatibilityStatus.Pass or CompatibilityStatus.Warning
                ? 600
                : 0,
            HostFramesPerSecond = 300,
            P99FrameMilliseconds = 4,
            CpuCycles = 1,
            MaximumDistinctColors = 4,
            DistinctCheckpointFrames = 2,
            AudioSamples = 1,
            AudioPeak = 0.5f,
            SaveStateDeterministic = true,
            Findings = findings
        };
}
