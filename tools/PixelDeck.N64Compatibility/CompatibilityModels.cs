namespace PixelDeck.N64Compatibility;

internal enum CompatibilityStatus
{
    Pass,
    Warning,
    Failed,
    Invalid
}

internal sealed record CompatibilityConfiguration(
    string GamesFolder,
    int FieldsPerGame,
    int Parallelism,
    string? Filter,
    bool CaptureFlaggedFrames,
    bool CaptureGraphicsTasks);

internal sealed record CompatibilitySummary(
    int Total,
    int UniqueImages,
    int Passed,
    int Warnings,
    int Failed,
    int Invalid);

internal sealed record HardwareProfileSummary(
    string Cic,
    string Region,
    int Total,
    int Passed,
    int Warnings,
    int Failed);

internal sealed record BlockerSummary(string Finding, int Games);

internal sealed record N64CompatibilityReport(
    int SchemaVersion,
    string Pixel64Version,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    CompatibilityConfiguration Configuration,
    CompatibilitySummary Summary,
    IReadOnlyList<HardwareProfileSummary> HardwareProfiles,
    IReadOnlyList<BlockerSummary> Blockers,
    IReadOnlyList<GameCompatibilityResult> Games);

internal sealed record GameCompatibilityResult
{
    public required string RelativePath { get; init; }

    public required string FileName { get; init; }

    public required string Sha256 { get; init; }

    public required CompatibilityStatus Status { get; init; }

    public string? Title { get; init; }

    public string? GameCode { get; init; }

    public string? Region { get; init; }

    public string? Cic { get; init; }

    public string? SaveType { get; init; }

    public string? SourceByteOrder { get; init; }

    public bool IsVerifiedTarget { get; init; }

    public bool ReachedCartridgeEntryPoint { get; init; }

    public int FieldsCompleted { get; init; }

    public double HostFieldsPerSecond { get; init; }

    public double P99FieldMilliseconds { get; init; }

    public long InstructionsExecuted { get; init; }

    public uint ProgramCounter { get; init; }

    public long GraphicsTasks { get; init; }

    public long AudioTasks { get; init; }

    public long GraphicsCommands { get; init; }

    public long UnsupportedGraphicsCommands { get; init; }

    public string UnsupportedGraphicsOpcodes { get; init; } = string.Empty;

    public string DetectedMicrocode { get; init; } = string.Empty;

    public string GraphicsBackend { get; init; } = string.Empty;

    public uint RdpOtherModeHigh { get; init; }

    public uint RdpOtherModeLow { get; init; }

    public uint RdpCycleType { get; init; }

    public long AlphaPixelsRejected { get; init; }

    public long FramebufferPixelsBlended { get; init; }

    public string UnsupportedTextureFormats { get; init; } = string.Empty;

    public long AudioCommands { get; init; }

    public long UnsupportedAudioCommands { get; init; }

    public string UnsupportedAudioOpcodes { get; init; } = string.Empty;

    public long VerticalInterrupts { get; init; }

    public long AudioDmas { get; init; }

    public long ControllerPolls { get; init; }

    public int MaximumDistinctColors { get; init; }

    public int DistinctCheckpointFrames { get; init; }

    public long AudioSamples { get; init; }

    public float AudioPeak { get; init; }

    public long DroppedAudioSamples { get; init; }

    public bool SaveStateDeterministic { get; init; }

    public string? CapturePath { get; init; }

    public string? GraphicsCapturePath { get; init; }

    public IReadOnlyList<string> Findings { get; init; } = [];
}

internal static class CompatibilityClassifier
{
    public static CompatibilityStatus Classify(
        bool validImage,
        IReadOnlyCollection<string> failures,
        IReadOnlyCollection<string> warnings)
    {
        if (!validImage)
        {
            return CompatibilityStatus.Invalid;
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
