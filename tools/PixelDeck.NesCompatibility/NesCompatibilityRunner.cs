using System.Diagnostics;
using System.Security.Cryptography;
using PixelDeck.Emulation.Nes;

namespace PixelDeck.NesCompatibility;

internal sealed class NesCompatibilityRunner
{
    private const double FramesPerSecond = 60.0988;

    public async Task<NesCompatibilityReport> RunAsync(
        CompatibilityOptions options,
        Action<int, int, GameCompatibilityResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(options.GamesFolder))
        {
            throw new DirectoryNotFoundException(
                $"The NES games folder does not exist: {options.GamesFolder}");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var gamePaths = Directory
            .EnumerateFiles(options.GamesFolder, "*.nes", SearchOption.AllDirectories)
            .Where(path =>
                options.Filter is null ||
                Path.GetFileNameWithoutExtension(path).Contains(
                    options.Filter,
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var results = new GameCompatibilityResult[gamePaths.Length];
        var completed = 0;
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = options.Parallelism
        };

        await Parallel.ForEachAsync(
            Enumerable.Range(0, gamePaths.Length),
            parallelOptions,
            (index, _) =>
            {
                var result = AuditGame(index, gamePaths[index], options);
                results[index] = result;
                var completedCount = Interlocked.Increment(ref completed);
                progress?.Invoke(completedCount, gamePaths.Length, result);
                return ValueTask.CompletedTask;
            });

        var orderedResults = results
            .OrderBy(result => result.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return CreateReport(
            options,
            startedAt,
            DateTimeOffset.UtcNow,
            orderedResults);
    }

    internal static NesCompatibilityReport CreateReport(
        CompatibilityOptions options,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        IReadOnlyList<GameCompatibilityResult> results)
    {
        var summary = new CompatibilitySummary(
            results.Count,
            results.Select(result => result.Sha256).Distinct(StringComparer.Ordinal).Count(),
            results.Count(result => result.Status == CompatibilityStatus.Pass),
            results.Count(result => result.Status == CompatibilityStatus.Warning),
            results.Count(result => result.Status == CompatibilityStatus.Failed),
            results.Count(result => result.Status == CompatibilityStatus.Unsupported),
            results.Count(result => result.Status == CompatibilityStatus.Invalid));
        var mappers = results
            .Where(result => result.Mapper.HasValue && result.Submapper.HasValue)
            .GroupBy(result => (Mapper: result.Mapper!.Value, Submapper: result.Submapper!.Value))
            .OrderBy(group => group.Key.Mapper)
            .ThenBy(group => group.Key.Submapper)
            .Select(group => new MapperCompatibilitySummary(
                group.Key.Mapper,
                group.Key.Submapper,
                group.Count(),
                group.Count(result => result.Status == CompatibilityStatus.Pass),
                group.Count(result => result.Status == CompatibilityStatus.Warning),
                group.Count(result => result.Status == CompatibilityStatus.Failed),
                group.Count(result => result.Status == CompatibilityStatus.Unsupported)))
            .ToArray();

        return new(
            SchemaVersion: 1,
            PixelNesVersion: FormatVersion(typeof(NesMachine).Assembly.GetName().Version),
            StartedAtUtc: startedAt,
            CompletedAtUtc: completedAt,
            Configuration: new(
                options.GamesFolder,
                options.FramesPerGame,
                options.Parallelism,
                options.Filter,
                options.CaptureFlaggedFrames),
            Summary: summary,
            Mappers: mappers,
            Games: results);
    }

    private static GameCompatibilityResult AuditGame(
        int index,
        string gamePath,
        CompatibilityOptions options)
    {
        var relativePath = Path.GetRelativePath(options.GamesFolder, gamePath)
            .Replace('\\', '/');
        var fileName = Path.GetFileName(gamePath);
        var hash = HashFile(gamePath);
        CartridgeInfo info;
        try
        {
            info = Cartridge.Inspect(gamePath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new GameCompatibilityResult
            {
                RelativePath = relativePath,
                FileName = fileName,
                Sha256 = hash,
                Status = CompatibilityStatus.Invalid,
                Findings = [$"Image inspection failed: {exception.Message}"]
            };
        }

        if (!info.IsSupported)
        {
            return new GameCompatibilityResult
            {
                RelativePath = relativePath,
                FileName = fileName,
                Sha256 = hash,
                Status = CompatibilityStatus.Unsupported,
                Mapper = info.MapperNumber,
                Submapper = info.SubmapperNumber,
                TimingMode = info.TimingMode.ToString(),
                IsNes20 = info.IsNes20,
                IsLimitedCompatibility = info.IsLimitedCompatibility,
                Findings =
                [
                    info.CompatibilityWarning ??
                    $"Mapper {info.MapperNumber} submapper {info.SubmapperNumber} is unsupported."
                ]
            };
        }

        var failures = new List<string>();
        var warnings = new List<string>();
        var frameDurations = new long[options.FramesPerGame];
        var audioBuffer = new float[2_048];
        var checkpointHashes = new HashSet<ulong>();
        var colors = new HashSet<uint>();
        uint[]? lastFrame = null;
        var maximumDistinctColors = 0;
        long audioSamples = 0;
        var audioPeak = 0f;
        var saveStateDeterministic = false;
        var framesCompleted = 0;
        NesMachine? machine = null;

        try
        {
            machine = NesMachine.Load(gamePath);
            var startingCycles = machine.CpuCycles;
            for (var frame = 0; frame < options.FramesPerGame; frame++)
            {
                machine.SetControllerState(1, GetAuditInput(frame));
                var frameStarted = Stopwatch.GetTimestamp();
                var pixels = machine.RunFrame();
                frameDurations[frame] = Stopwatch.GetTimestamp() - frameStarted;
                framesCompleted++;
                DrainAudio(machine, audioBuffer, ref audioSamples, ref audioPeak, failures);

                if (frame == options.FramesPerGame / 2)
                {
                    saveStateDeterministic = VerifyStateRoundTrip(
                        machine,
                        audioBuffer,
                        ref audioSamples,
                        ref audioPeak,
                        failures);
                }

                if (frame % 60 == 0 || frame == options.FramesPerGame - 1)
                {
                    colors.Clear();
                    foreach (var pixel in pixels)
                    {
                        colors.Add(pixel);
                    }

                    maximumDistinctColors = Math.Max(maximumDistinctColors, colors.Count);
                    checkpointHashes.Add(HashFrame(pixels));
                    lastFrame = pixels.ToArray();
                }
            }

            if (machine.CpuCycles <= startingCycles)
            {
                failures.Add("The CPU cycle count did not advance.");
            }

            if (machine.DroppedAudioSampleCount > 0)
            {
                failures.Add(
                    $"The core dropped {machine.DroppedAudioSampleCount} queued audio samples.");
            }

            var hostFramesPerSecond = CalculateCoreFramesPerSecond(frameDurations, framesCompleted);
            var p99Milliseconds = CalculateP99Milliseconds(frameDurations, framesCompleted);
            if (hostFramesPerSecond < FramesPerSecond * 0.98)
            {
                failures.Add(
                    $"Core throughput was {hostFramesPerSecond:0.0} FPS, below realtime.");
            }
            else if (
                options.FramesPerGame >= 300 &&
                p99Milliseconds > (1_000 / FramesPerSecond))
            {
                warnings.Add(
                    $"The p99 core frame was {p99Milliseconds:0.000} ms.");
            }

            var hasMeaningfulLivenessWindow = options.FramesPerGame >= 300;
            if (hasMeaningfulLivenessWindow && maximumDistinctColors < 2)
            {
                warnings.Add("No active multicolor video appeared at a checkpoint.");
            }

            if (hasMeaningfulLivenessWindow && checkpointHashes.Count < 2)
            {
                warnings.Add("Checkpoint frames remained visually static.");
            }

            if (hasMeaningfulLivenessWindow && audioPeak < 0.0001f)
            {
                warnings.Add("No audible output appeared during the audit window.");
            }

            if (info.IsLimitedCompatibility && !string.IsNullOrWhiteSpace(info.CompatibilityWarning))
            {
                warnings.Add(info.CompatibilityWarning);
            }

            var status = CompatibilityClassifier.Classify(
                validImage: true,
                supported: true,
                failures,
                warnings);
            var capturePath = WriteCaptureWhenNeeded(
                index,
                relativePath,
                hash,
                status,
                lastFrame,
                options);
            return new GameCompatibilityResult
            {
                RelativePath = relativePath,
                FileName = fileName,
                Sha256 = hash,
                Status = status,
                Mapper = info.MapperNumber,
                Submapper = info.SubmapperNumber,
                TimingMode = info.TimingMode.ToString(),
                IsNes20 = info.IsNes20,
                IsLimitedCompatibility = info.IsLimitedCompatibility,
                FramesCompleted = framesCompleted,
                HostFramesPerSecond = hostFramesPerSecond,
                P99FrameMilliseconds = p99Milliseconds,
                CpuCycles = machine.CpuCycles,
                ProgramCounter = machine.ProgramCounter,
                MaximumDistinctColors = maximumDistinctColors,
                DistinctCheckpointFrames = checkpointHashes.Count,
                AudioSamples = audioSamples,
                AudioPeak = audioPeak,
                DroppedAudioSamples = machine.DroppedAudioSampleCount,
                SaveStateDeterministic = saveStateDeterministic,
                CapturePath = capturePath,
                Findings = [.. failures, .. warnings]
            };
        }
        catch (Exception exception)
        {
            failures.Add($"{exception.GetType().Name}: {exception.Message}");
            var capturePath = WriteCaptureWhenNeeded(
                index,
                relativePath,
                hash,
                CompatibilityStatus.Failed,
                lastFrame,
                options);
            return new GameCompatibilityResult
            {
                RelativePath = relativePath,
                FileName = fileName,
                Sha256 = hash,
                Status = CompatibilityStatus.Failed,
                Mapper = info.MapperNumber,
                Submapper = info.SubmapperNumber,
                TimingMode = info.TimingMode.ToString(),
                IsNes20 = info.IsNes20,
                IsLimitedCompatibility = info.IsLimitedCompatibility,
                FramesCompleted = framesCompleted,
                HostFramesPerSecond = CalculateCoreFramesPerSecond(frameDurations, framesCompleted),
                P99FrameMilliseconds = CalculateP99Milliseconds(frameDurations, framesCompleted),
                CpuCycles = machine?.CpuCycles ?? 0,
                ProgramCounter = machine?.ProgramCounter ?? 0,
                MaximumDistinctColors = maximumDistinctColors,
                DistinctCheckpointFrames = checkpointHashes.Count,
                AudioSamples = audioSamples,
                AudioPeak = audioPeak,
                DroppedAudioSamples = machine?.DroppedAudioSampleCount ?? 0,
                SaveStateDeterministic = saveStateDeterministic,
                CapturePath = capturePath,
                Findings = failures
            };
        }
    }

    private static bool VerifyStateRoundTrip(
        NesMachine machine,
        float[] audioBuffer,
        ref long audioSamples,
        ref float audioPeak,
        List<string> failures)
    {
        machine.ClearAudioSamples();
        var state = machine.SaveState();
        var expectedProgramCounter = machine.ProgramCounter;
        var expectedCycles = machine.CpuCycles;
        var expectedFrame = machine.RunFrame().ToArray();
        var expectedAudio = DrainAudioSequence(machine, audioBuffer);

        machine.LoadState(state);
        var restoredProgramCounter = machine.ProgramCounter;
        var restoredCycles = machine.CpuCycles;
        var restoredFrame = machine.RunFrame().ToArray();
        var restoredAudio = DrainAudioSequence(machine, audioBuffer);

        AccumulateAudio(expectedAudio, ref audioSamples, ref audioPeak, failures);
        AccumulateAudio(restoredAudio, ref audioSamples, ref audioPeak, failures);
        var deterministic =
            expectedProgramCounter == restoredProgramCounter &&
            expectedCycles == restoredCycles &&
            expectedFrame.AsSpan().SequenceEqual(restoredFrame) &&
            expectedAudio.AsSpan().SequenceEqual(restoredAudio);
        if (!deterministic)
        {
            failures.Add("Save-state restoration did not reproduce the exact next frame and audio.");
        }

        return deterministic;
    }

    private static float[] DrainAudioSequence(NesMachine machine, float[] buffer)
    {
        var samples = new List<float>(1_024);
        int read;
        while ((read = machine.ReadAudioSamples(buffer)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return samples.ToArray();
    }

    private static void DrainAudio(
        NesMachine machine,
        float[] buffer,
        ref long sampleCount,
        ref float peak,
        List<string> failures)
    {
        int read;
        while ((read = machine.ReadAudioSamples(buffer)) > 0)
        {
            AccumulateAudio(buffer.AsSpan(0, read), ref sampleCount, ref peak, failures);
        }
    }

    private static void AccumulateAudio(
        ReadOnlySpan<float> samples,
        ref long sampleCount,
        ref float peak,
        List<string> failures)
    {
        sampleCount += samples.Length;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample))
            {
                if (!failures.Contains("The core produced a non-finite audio sample."))
                {
                    failures.Add("The core produced a non-finite audio sample.");
                }

                continue;
            }

            peak = Math.Max(peak, Math.Abs(sample));
        }
    }

    private static NesButton GetAuditInput(int frame)
    {
        if (frame is >= 30 and < 42 ||
            frame is >= 120 and < 132)
        {
            return NesButton.Start;
        }

        return (frame % 360) switch
        {
            >= 60 and < 72 => NesButton.A,
            >= 150 and < 190 => NesButton.Right | NesButton.A,
            >= 210 and < 240 => NesButton.Left | NesButton.B,
            >= 270 and < 300 => NesButton.Down | NesButton.A,
            >= 330 and < 350 => NesButton.Up | NesButton.B,
            _ => NesButton.None
        };
    }

    private static double CalculateP99Milliseconds(long[] durations, int count)
    {
        if (count == 0)
        {
            return 0;
        }

        var measured = durations.AsSpan(0, count).ToArray();
        Array.Sort(measured);
        var index = Math.Clamp((int)Math.Ceiling(count * 0.99) - 1, 0, count - 1);
        return (measured[index] * 1_000.0) / Stopwatch.Frequency;
    }

    private static double CalculateCoreFramesPerSecond(long[] durations, int count)
    {
        if (count == 0)
        {
            return 0;
        }

        long totalTicks = 0;
        for (var index = 0; index < count; index++)
        {
            totalTicks += durations[index];
        }

        var seconds = totalTicks / (double)Stopwatch.Frequency;
        return count / Math.Max(seconds, 0.000_001);
    }

    private static ulong HashFrame(ReadOnlySpan<uint> pixels)
    {
        const ulong offsetBasis = 14_695_981_039_346_656_037;
        const ulong prime = 1_099_511_628_211;
        var hash = offsetBasis;
        foreach (var pixel in pixels)
        {
            hash ^= pixel;
            hash *= prime;
        }

        return hash;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string? WriteCaptureWhenNeeded(
        int index,
        string relativePath,
        string hash,
        CompatibilityStatus status,
        uint[]? frame,
        CompatibilityOptions options)
    {
        if (!options.CaptureFlaggedFrames ||
            frame is null ||
            status is not (CompatibilityStatus.Warning or CompatibilityStatus.Failed))
        {
            return null;
        }

        var safeName = string.Concat(
            Path.GetFileNameWithoutExtension(relativePath)
                .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        if (safeName.Length > 80)
        {
            safeName = safeName[..80];
        }

        var relativeCapture = Path.Combine(
            "captures",
            $"{index:D4}-{safeName}-{hash[..8]}.bmp");
        FrameCapture.WriteBitmap(
            Path.Combine(options.OutputFolder, relativeCapture),
            frame,
            width: 256,
            height: 240);
        return relativeCapture.Replace('\\', '/');
    }

    private static string FormatVersion(Version? version) =>
        version is null
            ? "unknown"
            : $"{version.Major}.{version.Minor}.{version.Build:D3}";
}
