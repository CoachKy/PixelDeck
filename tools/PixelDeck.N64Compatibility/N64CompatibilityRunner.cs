using System.Diagnostics;
using System.Security.Cryptography;
using PixelDeck.Emulation.N64;

namespace PixelDeck.N64Compatibility;

internal sealed class N64CompatibilityRunner
{
    private static readonly HashSet<string> CartridgeExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".z64", ".v64", ".n64" };

    public async Task<N64CompatibilityReport> RunAsync(
        CompatibilityOptions options,
        Action<int, int, GameCompatibilityResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(options.GamesFolder))
        {
            throw new DirectoryNotFoundException(
                $"The Nintendo 64 games folder does not exist: {options.GamesFolder}");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var gamePaths = Directory
            .EnumerateFiles(options.GamesFolder, "*", SearchOption.AllDirectories)
            .Where(path => CartridgeExtensions.Contains(Path.GetExtension(path)))
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
        return CreateReport(options, startedAt, DateTimeOffset.UtcNow, orderedResults);
    }

    internal static N64CompatibilityReport CreateReport(
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
            results.Count(result => result.Status == CompatibilityStatus.Invalid));
        var profiles = results
            .Where(result => result.Cic is not null && result.Region is not null)
            .GroupBy(result => (Cic: result.Cic!, Region: result.Region!))
            .OrderBy(group => group.Key.Cic, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Region, StringComparer.Ordinal)
            .Select(group => new HardwareProfileSummary(
                group.Key.Cic,
                group.Key.Region,
                group.Count(),
                group.Count(result => result.Status == CompatibilityStatus.Pass),
                group.Count(result => result.Status == CompatibilityStatus.Warning),
                group.Count(result => result.Status == CompatibilityStatus.Failed)))
            .ToArray();
        var blockers = results
            .Where(result => result.Status is CompatibilityStatus.Failed or CompatibilityStatus.Invalid)
            .Select(result =>
                result.Findings.Count > 0
                    ? result.Findings[0]
                    : result.Status.ToString())
            .GroupBy(finding => finding, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new BlockerSummary(group.Key, group.Count()))
            .ToArray();

        return new(
            SchemaVersion: 2,
            Pixel64Version: FormatVersion(typeof(N64Machine).Assembly.GetName().Version),
            StartedAtUtc: startedAt,
            CompletedAtUtc: completedAt,
            Configuration: new(
                options.GamesFolder,
                options.FieldsPerGame,
                options.Parallelism,
                options.Filter,
                options.CaptureFlaggedFrames,
                options.CaptureGraphicsTasks),
            Summary: summary,
            HardwareProfiles: profiles,
            Blockers: blockers,
            Games: results);
    }

    private static GameCompatibilityResult AuditGame(
        int index,
        string gamePath,
        CompatibilityOptions options)
    {
        var relativePath = Path.GetRelativePath(options.GamesFolder, gamePath).Replace('\\', '/');
        var fileName = Path.GetFileName(gamePath);
        string hash;
        N64Cartridge cartridge;
        try
        {
            hash = HashFile(gamePath);
            cartridge = N64Cartridge.Inspect(gamePath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new GameCompatibilityResult
            {
                RelativePath = relativePath,
                FileName = fileName,
                Sha256 = "UNAVAILABLE",
                Status = CompatibilityStatus.Invalid,
                Findings = [$"Image inspection failed: {exception.Message}"]
            };
        }

        var failures = new List<string>();
        var warnings = new List<string>();
        var fieldDurations = new long[options.FieldsPerGame];
        var audioBuffer = new float[4_096];
        var checkpointHashes = new HashSet<ulong>();
        var colors = new HashSet<uint>();
        uint[]? lastFrame = null;
        var lastFrameWidth = 0;
        var lastFrameHeight = 0;
        var maximumDistinctColors = 0;
        long audioSamples = 0;
        var audioPeak = 0f;
        var saveStateDeterministic = false;
        var fieldsCompleted = 0;
        N64Machine? machine = null;

        try
        {
            // A null save path deliberately keeps this audit read-only. ROM
            // battery data is neither loaded from nor written to the user's
            // Saves folder.
            machine = N64Machine.Create(cartridge);
            if (options.CaptureGraphicsTasks)
            {
                machine.RequestGraphicsTaskCapture();
            }

            var startingInstructions = machine.Cpu.InstructionsExecuted;
            for (var field = 0; field < options.FieldsPerGame; field++)
            {
                machine.SetControllerState(1, GetAuditInput(field));
                var fieldStarted = Stopwatch.GetTimestamp();
                var pixels = machine.RunFrame();
                fieldDurations[field] = Stopwatch.GetTimestamp() - fieldStarted;
                fieldsCompleted++;
                DrainAudio(machine, audioBuffer, ref audioSamples, ref audioPeak, failures);

                if (field == options.FieldsPerGame / 2)
                {
                    saveStateDeterministic = VerifyStateRoundTrip(
                        machine,
                        audioBuffer,
                        ref audioSamples,
                        ref audioPeak,
                        failures);
                }

                if (field % 30 == 0 || field == options.FieldsPerGame - 1)
                {
                    colors.Clear();
                    foreach (var pixel in pixels)
                    {
                        colors.Add(pixel);
                    }

                    maximumDistinctColors = Math.Max(maximumDistinctColors, colors.Count);
                    checkpointHashes.Add(HashFrame(pixels));
                    lastFrame = pixels.ToArray();
                    lastFrameWidth = machine.Width;
                    lastFrameHeight = machine.Height;
                }
            }

            EvaluateMachine(
                machine,
                cartridge,
                options,
                fieldDurations,
                fieldsCompleted,
                startingInstructions,
                maximumDistinctColors,
                checkpointHashes.Count,
                audioPeak,
                failures,
                warnings);
            if (options.CaptureGraphicsTasks && machine.LastGraphicsCapture is null)
            {
                warnings.Add("No graphics task was submitted during the bounded capture route.");
            }

            var status = CompatibilityClassifier.Classify(true, failures, warnings);
            var capturePath = WriteCaptureWhenNeeded(
                index,
                relativePath,
                hash,
                status,
                lastFrame,
                lastFrameWidth,
                lastFrameHeight,
                options);
            var graphicsCapturePath = WriteGraphicsCapture(
                index,
                relativePath,
                hash,
                machine.LastGraphicsCapture,
                options);
            return CreateGameResult(
                relativePath,
                fileName,
                hash,
                status,
                cartridge,
                machine,
                fieldDurations,
                fieldsCompleted,
                maximumDistinctColors,
                checkpointHashes.Count,
                audioSamples,
                audioPeak,
                saveStateDeterministic,
                capturePath,
                graphicsCapturePath,
                [.. failures, .. warnings]);
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
                lastFrameWidth,
                lastFrameHeight,
                options);
            var graphicsCapturePath = WriteGraphicsCapture(
                index,
                relativePath,
                hash,
                machine?.LastGraphicsCapture,
                options);
            return CreateGameResult(
                relativePath,
                fileName,
                hash,
                CompatibilityStatus.Failed,
                cartridge,
                machine,
                fieldDurations,
                fieldsCompleted,
                maximumDistinctColors,
                checkpointHashes.Count,
                audioSamples,
                audioPeak,
                saveStateDeterministic,
                capturePath,
                graphicsCapturePath,
                failures);
        }
    }

    private static void EvaluateMachine(
        N64Machine machine,
        N64Cartridge cartridge,
        CompatibilityOptions options,
        long[] fieldDurations,
        int fieldsCompleted,
        long startingInstructions,
        int maximumDistinctColors,
        int distinctCheckpointFrames,
        float audioPeak,
        List<string> failures,
        List<string> warnings)
    {
        if (machine.Cpu.InstructionsExecuted <= startingInstructions)
        {
            failures.Add("The CPU instruction count did not advance.");
        }

        if (machine.Cpu.UnsupportedInstructionCount > 0)
        {
            failures.Add(
                $"The CPU encountered {machine.Cpu.UnsupportedInstructionCount} unsupported instruction(s).");
        }

        if (machine.DroppedAudioSampleCount > 0)
        {
            failures.Add($"The core dropped {machine.DroppedAudioSampleCount} queued audio samples.");
        }

        if (!machine.ReachedCartridgeEntryPoint)
        {
            warnings.Add("The cartridge entry point was not reached during the audit window.");
        }

        if (machine.Renderer.UnsupportedCommands > 0)
        {
            warnings.Add(
                $"The graphics HLE skipped {machine.Renderer.UnsupportedCommands} command(s): " +
                FormatByteCounts(machine.Renderer.UnsupportedCommandCounts));
        }

        if (machine.Renderer.UnsupportedTextureFormatCounts.Count > 0)
        {
            warnings.Add(
                "Unsupported texture formats were configured: " +
                FormatStringCounts(machine.Renderer.UnsupportedTextureFormatCounts));
        }

        if (machine.AudioProcessor.UnsupportedCommands > 0)
        {
            warnings.Add(
                $"The audio HLE skipped {machine.AudioProcessor.UnsupportedCommands} command(s): " +
                FormatUIntCounts(machine.AudioProcessor.UnsupportedCommandCounts));
        }

        var hostFieldsPerSecond = CalculateCoreFieldsPerSecond(fieldDurations, fieldsCompleted);
        if (hostFieldsPerSecond < machine.FramesPerSecond * 0.98)
        {
            warnings.Add(
                $"Core throughput was {hostFieldsPerSecond:0.0} fields/s, below " +
                $"{machine.FramesPerSecond:0.0} fields/s realtime.");
        }

        var hasMeaningfulLivenessWindow = options.FieldsPerGame >= 120;
        if (hasMeaningfulLivenessWindow && machine.GraphicsTasksSubmitted == 0)
        {
            warnings.Add("No graphics RSP task was submitted during the audit window.");
        }

        if (hasMeaningfulLivenessWindow && maximumDistinctColors < 2)
        {
            warnings.Add("No active multicolor video appeared at a checkpoint.");
        }

        if (hasMeaningfulLivenessWindow && distinctCheckpointFrames < 2)
        {
            warnings.Add("Checkpoint frames remained visually static.");
        }

        if (hasMeaningfulLivenessWindow && machine.AudioTasksSubmitted > 0 && audioPeak < 0.0001f)
        {
            warnings.Add("Audio tasks ran but produced no audible output.");
        }

        if (!cartridge.IsPixel64VerifiedTarget)
        {
            warnings.Add(cartridge.CompatibilityMessage);
        }
    }

    private static GameCompatibilityResult CreateGameResult(
        string relativePath,
        string fileName,
        string hash,
        CompatibilityStatus status,
        N64Cartridge cartridge,
        N64Machine? machine,
        long[] fieldDurations,
        int fieldsCompleted,
        int maximumDistinctColors,
        int distinctCheckpointFrames,
        long audioSamples,
        float audioPeak,
        bool saveStateDeterministic,
        string? capturePath,
        string? graphicsCapturePath,
        IReadOnlyList<string> findings) =>
        new()
        {
            RelativePath = relativePath,
            FileName = fileName,
            Sha256 = hash,
            Status = status,
            Title = cartridge.Title,
            GameCode = cartridge.GameCode,
            Region = cartridge.VideoRegion.ToString(),
            Cic = cartridge.Cic.ToString(),
            SaveType = cartridge.SaveType.ToString(),
            SourceByteOrder = cartridge.SourceByteOrder.ToString(),
            IsVerifiedTarget = cartridge.IsPixel64VerifiedTarget,
            ReachedCartridgeEntryPoint = machine?.ReachedCartridgeEntryPoint ?? false,
            FieldsCompleted = fieldsCompleted,
            HostFieldsPerSecond = CalculateCoreFieldsPerSecond(fieldDurations, fieldsCompleted),
            P99FieldMilliseconds = CalculateP99Milliseconds(fieldDurations, fieldsCompleted),
            InstructionsExecuted = machine?.Cpu.InstructionsExecuted ?? 0,
            ProgramCounter = machine?.Cpu.ProgramCounter ?? 0,
            GraphicsTasks = machine?.GraphicsTasksSubmitted ?? 0,
            AudioTasks = machine?.AudioTasksSubmitted ?? 0,
            GraphicsCommands = machine?.Renderer.CommandsProcessed ?? 0,
            UnsupportedGraphicsCommands = machine?.Renderer.UnsupportedCommands ?? 0,
            UnsupportedGraphicsOpcodes = machine is null
                ? string.Empty
                : FormatByteCounts(machine.Renderer.UnsupportedCommandCounts),
            DetectedMicrocode = machine?.Renderer.DetectedMicrocodeName ?? string.Empty,
            GraphicsBackend = machine?.Renderer.Name ?? string.Empty,
            RdpOtherModeHigh = machine?.Renderer.RdpState.OtherModeHigh ?? 0,
            RdpOtherModeLow = machine?.Renderer.RdpState.OtherModeLow ?? 0,
            RdpCycleType = machine?.Renderer.RdpState.CycleType ?? 0,
            AlphaPixelsRejected = machine?.Renderer.AlphaPixelsRejected ?? 0,
            FramebufferPixelsBlended = machine?.Renderer.FramebufferPixelsBlended ?? 0,
            UnsupportedTextureFormats = machine is null
                ? string.Empty
                : FormatStringCounts(machine.Renderer.UnsupportedTextureFormatCounts),
            AudioCommands = machine?.AudioProcessor.CommandsProcessed ?? 0,
            UnsupportedAudioCommands = machine?.AudioProcessor.UnsupportedCommands ?? 0,
            UnsupportedAudioOpcodes = machine is null
                ? string.Empty
                : FormatUIntCounts(machine.AudioProcessor.UnsupportedCommandCounts),
            VerticalInterrupts = machine?.Memory.VerticalInterruptsRaised ?? 0,
            AudioDmas = machine?.Memory.AudioDmasCompleted ?? 0,
            ControllerPolls = machine?.Memory.ControllerPolls ?? 0,
            MaximumDistinctColors = maximumDistinctColors,
            DistinctCheckpointFrames = distinctCheckpointFrames,
            AudioSamples = audioSamples,
            AudioPeak = audioPeak,
            DroppedAudioSamples = machine?.DroppedAudioSampleCount ?? 0,
            SaveStateDeterministic = saveStateDeterministic,
            CapturePath = capturePath,
            GraphicsCapturePath = graphicsCapturePath,
            Findings = findings
        };

    private static bool VerifyStateRoundTrip(
        N64Machine machine,
        float[] audioBuffer,
        ref long audioSamples,
        ref float audioPeak,
        List<string> failures)
    {
        machine.ClearAudioSamples();
        var state = machine.SaveState();
        var expectedProgramCounter = machine.Cpu.ProgramCounter;
        var expectedInstructions = machine.Cpu.InstructionsExecuted;
        var expectedFrameNumber = machine.FrameNumber;
        var expectedFrame = machine.RunFrame().ToArray();
        var expectedAudio = DrainAudioSequence(machine, audioBuffer);

        machine.LoadState(state);
        var restoredProgramCounter = machine.Cpu.ProgramCounter;
        var restoredInstructions = machine.Cpu.InstructionsExecuted;
        var restoredFrameNumber = machine.FrameNumber;
        var restoredFrame = machine.RunFrame().ToArray();
        var restoredAudio = DrainAudioSequence(machine, audioBuffer);

        AccumulateAudio(expectedAudio, ref audioSamples, ref audioPeak, failures);
        AccumulateAudio(restoredAudio, ref audioSamples, ref audioPeak, failures);
        var deterministic =
            expectedProgramCounter == restoredProgramCounter &&
            expectedInstructions == restoredInstructions &&
            expectedFrameNumber == restoredFrameNumber &&
            expectedFrame.AsSpan().SequenceEqual(restoredFrame) &&
            expectedAudio.AsSpan().SequenceEqual(restoredAudio);
        if (!deterministic)
        {
            failures.Add("Save-state restoration did not reproduce the exact next field and audio.");
        }

        return deterministic;
    }

    private static float[] DrainAudioSequence(N64Machine machine, float[] buffer)
    {
        var samples = new List<float>(4_096);
        int read;
        while ((read = machine.ReadAudioSamples(buffer)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return samples.ToArray();
    }

    private static void DrainAudio(
        N64Machine machine,
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

    private static N64ControllerState GetAuditInput(int field)
    {
        if (field is >= 15 and < 23 || field is >= 90 and < 98)
        {
            return new(N64Button.Start, 0, 0);
        }

        return (field % 240) switch
        {
            >= 30 and < 42 => new(N64Button.A, 0, 0),
            >= 60 and < 80 => new(N64Button.A, 70, 0),
            >= 120 and < 145 => new(N64Button.B, -70, 0),
            >= 165 and < 190 => new(N64Button.Z, 0, -70),
            >= 210 and < 230 => new(N64Button.A, 0, 70),
            _ => N64ControllerState.Neutral
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

    private static double CalculateCoreFieldsPerSecond(long[] durations, int count)
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

    private static string FormatByteCounts(IReadOnlyDictionary<byte, long> values) =>
        string.Join(
            ", ",
            values
                .Where(entry => entry.Value > 0)
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key)
                .Select(entry => $"0x{entry.Key:X2}={entry.Value}"));

    private static string FormatUIntCounts(IReadOnlyDictionary<uint, long> values) =>
        string.Join(
            ", ",
            values
                .Where(entry => entry.Value > 0)
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key)
                .Select(entry => $"0x{entry.Key:X2}={entry.Value}"));

    private static string FormatStringCounts(IReadOnlyDictionary<string, long> values) =>
        string.Join(
            ", ",
            values
                .Where(entry => entry.Value > 0)
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => $"{entry.Key}={entry.Value}"));

    private static string? WriteCaptureWhenNeeded(
        int index,
        string relativePath,
        string hash,
        CompatibilityStatus status,
        uint[]? frame,
        int width,
        int height,
        CompatibilityOptions options)
    {
        if (!options.CaptureFlaggedFrames ||
            frame is null ||
            width <= 0 ||
            height <= 0 ||
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
            width,
            height);
        return relativeCapture.Replace('\\', '/');
    }

    private static string? WriteGraphicsCapture(
        int index,
        string relativePath,
        string hash,
        N64GraphicsTaskCapture? capture,
        CompatibilityOptions options)
    {
        if (!options.CaptureGraphicsTasks || capture is null)
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
            "graphics-tasks",
            $"{index:D4}-{safeName}-{hash[..8]}.p64gfx");
        capture.Save(Path.Combine(options.OutputFolder, relativeCapture));
        return relativeCapture.Replace('\\', '/');
    }

    private static string FormatVersion(Version? version) =>
        version is null
            ? "unknown"
            : $"{version.Major}.{version.Minor}.{version.Build:D3}";
}
