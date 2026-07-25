namespace PixelDeck.NesCompatibility;

internal enum CompatibilityStatus
{
    Pass,
    Warning,
    Failed,
    Unsupported,
    Invalid
}

internal sealed record CompatibilityConfiguration(
    string GamesFolder,
    int FramesPerGame,
    int Parallelism,
    string? Filter,
    bool CaptureFlaggedFrames);

internal sealed record CompatibilitySummary(
    int Total,
    int UniqueImages,
    int Passed,
    int Warnings,
    int Failed,
    int Unsupported,
    int Invalid);

internal sealed record MapperCompatibilitySummary(
    int Mapper,
    int Submapper,
    int Total,
    int Passed,
    int Warnings,
    int Failed,
    int Unsupported);

internal sealed record NesCompatibilityReport(
    int SchemaVersion,
    string PixelNesVersion,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    CompatibilityConfiguration Configuration,
    CompatibilitySummary Summary,
    IReadOnlyList<MapperCompatibilitySummary> Mappers,
    IReadOnlyList<GameCompatibilityResult> Games);

internal sealed record GameCompatibilityResult
{
    public required string RelativePath { get; init; }

    public required string FileName { get; init; }

    public required string Sha256 { get; init; }

    public required CompatibilityStatus Status { get; init; }

    public int? Mapper { get; init; }

    public int? Submapper { get; init; }

    public string? TimingMode { get; init; }

    public bool IsNes20 { get; init; }

    public bool IsLimitedCompatibility { get; init; }

    public int FramesCompleted { get; init; }

    public double HostFramesPerSecond { get; init; }

    public double P99FrameMilliseconds { get; init; }

    public long CpuCycles { get; init; }

    public ushort ProgramCounter { get; init; }

    public int MaximumDistinctColors { get; init; }

    public int DistinctCheckpointFrames { get; init; }

    public long AudioSamples { get; init; }

    public float AudioPeak { get; init; }

    public long DroppedAudioSamples { get; init; }

    public bool SaveStateDeterministic { get; init; }

    public string? CapturePath { get; init; }

    public IReadOnlyList<string> Findings { get; init; } = [];
}

internal static class CompatibilityClassifier
{
    public static CompatibilityStatus Classify(
        bool validImage,
        bool supported,
        IReadOnlyCollection<string> failures,
        IReadOnlyCollection<string> warnings)
    {
        if (!validImage)
        {
            return CompatibilityStatus.Invalid;
        }

        if (!supported)
        {
            return CompatibilityStatus.Unsupported;
        }

        if (failures.Count > 0)
        {
            return CompatibilityStatus.Failed;
        }

        return warnings.Count > 0
            ? CompatibilityStatus.Warning
            : CompatibilityStatus.Pass;
    }
}
