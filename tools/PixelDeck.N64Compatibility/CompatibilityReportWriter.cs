using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PixelDeck.N64Compatibility;

internal static class CompatibilityReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

    public static CompatibilityReportPaths Write(
        N64CompatibilityReport report,
        string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        var jsonPath = Path.Combine(outputFolder, "report.json");
        var csvPath = Path.Combine(outputFolder, "games.csv");
        var markdownPath = Path.Combine(outputFolder, "REPORT.md");

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
        File.WriteAllText(csvPath, BuildCsv(report.Games), new UTF8Encoding(false));
        File.WriteAllText(markdownPath, BuildMarkdown(report), new UTF8Encoding(false));
        return new(jsonPath, csvPath, markdownPath);
    }

    internal static string BuildCsv(IReadOnlyList<GameCompatibilityResult> games)
    {
        var output = new StringBuilder();
        output.AppendLine(
            "status,path,title,gameCode,sha256,region,cic,saveType,byteOrder,verified," +
            "entryPoint,fields,coreFieldsPerSecond,p99Ms,instructions,pc,graphicsTasks," +
            "audioTasks,graphicsCommands,unsupportedGraphics,graphicsOpcodes,microcode,microcodeCrc32,audioMicrocode," +
            "graphicsBackend,rdpOtherModeHigh,rdpOtherModeLow,rdpCycleType,alphaRejected," +
            "framebufferBlended,unsupportedTextures,audioCommands,unsupportedAudio,audioOpcodes,viInterrupts," +
            "audioDmas,controllerPolls,maxColors,distinctFrames,audioSamples,audioPeak," +
            "droppedAudio,stateDeterministic,capture,graphicsCapture,findings");
        foreach (var game in games)
        {
            AppendCsvRow(
                output,
                game.Status.ToString(),
                game.RelativePath,
                game.Title ?? string.Empty,
                game.GameCode ?? string.Empty,
                game.Sha256,
                game.Region ?? string.Empty,
                game.Cic ?? string.Empty,
                game.SaveType ?? string.Empty,
                game.SourceByteOrder ?? string.Empty,
                game.IsVerifiedTarget.ToString(),
                game.ReachedCartridgeEntryPoint.ToString(),
                game.FieldsCompleted.ToString(CultureInfo.InvariantCulture),
                game.HostFieldsPerSecond.ToString("0.000", CultureInfo.InvariantCulture),
                game.P99FieldMilliseconds.ToString("0.000", CultureInfo.InvariantCulture),
                game.InstructionsExecuted.ToString(CultureInfo.InvariantCulture),
                $"0x{game.ProgramCounter:X8}",
                game.GraphicsTasks.ToString(CultureInfo.InvariantCulture),
                game.AudioTasks.ToString(CultureInfo.InvariantCulture),
                game.GraphicsCommands.ToString(CultureInfo.InvariantCulture),
                game.UnsupportedGraphicsCommands.ToString(CultureInfo.InvariantCulture),
                game.UnsupportedGraphicsOpcodes,
                game.DetectedMicrocode,
                $"0x{game.GraphicsMicrocodeCrc32:X8}",
                game.DetectedAudioMicrocode,
                game.GraphicsBackend,
                $"0x{game.RdpOtherModeHigh:X8}",
                $"0x{game.RdpOtherModeLow:X8}",
                game.RdpCycleType.ToString(CultureInfo.InvariantCulture),
                game.AlphaPixelsRejected.ToString(CultureInfo.InvariantCulture),
                game.FramebufferPixelsBlended.ToString(CultureInfo.InvariantCulture),
                game.UnsupportedTextureFormats,
                game.AudioCommands.ToString(CultureInfo.InvariantCulture),
                game.UnsupportedAudioCommands.ToString(CultureInfo.InvariantCulture),
                game.UnsupportedAudioOpcodes,
                game.VerticalInterrupts.ToString(CultureInfo.InvariantCulture),
                game.AudioDmas.ToString(CultureInfo.InvariantCulture),
                game.ControllerPolls.ToString(CultureInfo.InvariantCulture),
                game.MaximumDistinctColors.ToString(CultureInfo.InvariantCulture),
                game.DistinctCheckpointFrames.ToString(CultureInfo.InvariantCulture),
                game.AudioSamples.ToString(CultureInfo.InvariantCulture),
                game.AudioPeak.ToString("0.000000", CultureInfo.InvariantCulture),
                game.DroppedAudioSamples.ToString(CultureInfo.InvariantCulture),
                game.SaveStateDeterministic.ToString(),
                game.CapturePath ?? string.Empty,
                game.GraphicsCapturePath ?? string.Empty,
                string.Join(" | ", game.Findings));
        }

        return output.ToString();
    }

    internal static string BuildMarkdown(N64CompatibilityReport report)
    {
        var output = new StringBuilder();
        output.AppendLine("# Pixel64 compatibility report");
        output.AppendLine();
        output.AppendLine($"- Pixel64: `{report.Pixel64Version}`");
        output.AppendLine($"- Started: `{report.StartedAtUtc:O}`");
        output.AppendLine($"- Completed: `{report.CompletedAtUtc:O}`");
        output.AppendLine($"- Games folder: `{EscapeMarkdown(report.Configuration.GamesFolder)}`");
        output.AppendLine($"- Video fields per image: `{report.Configuration.FieldsPerGame}`");
        output.AppendLine($"- Parallel emulators: `{report.Configuration.Parallelism}`");
        output.AppendLine(
            $"- Graphics-task captures: `{report.Configuration.CaptureGraphicsTasks}`");
        if (!string.IsNullOrWhiteSpace(report.Configuration.Filter))
        {
            output.AppendLine($"- Filename filter: `{EscapeMarkdown(report.Configuration.Filter)}`");
        }

        output.AppendLine();
        output.AppendLine("## Summary");
        output.AppendLine();
        output.AppendLine("| Total | Unique | Pass | Warning | Failed | Invalid |");
        output.AppendLine("| ---: | ---: | ---: | ---: | ---: | ---: |");
        output.AppendLine(
            $"| {report.Summary.Total} | {report.Summary.UniqueImages} | " +
            $"{report.Summary.Passed} | {report.Summary.Warnings} | " +
            $"{report.Summary.Failed} | {report.Summary.Invalid} |");

        output.AppendLine();
        output.AppendLine("## Hardware profile coverage");
        output.AppendLine();
        output.AppendLine("| CIC | Region | Total | Pass | Warning | Failed |");
        output.AppendLine("| --- | --- | ---: | ---: | ---: | ---: |");
        foreach (var profile in report.HardwareProfiles)
        {
            output.AppendLine(
                $"| {EscapeMarkdown(profile.Cic)} | {EscapeMarkdown(profile.Region)} | " +
                $"{profile.Total} | {profile.Passed} | {profile.Warnings} | {profile.Failed} |");
        }

        output.AppendLine();
        output.AppendLine("## First blockers");
        output.AppendLine();
        if (report.Blockers.Count == 0)
        {
            output.AppendLine("None.");
        }
        else
        {
            output.AppendLine("| Games | First failure |");
            output.AppendLine("| ---: | --- |");
            foreach (var blocker in report.Blockers)
            {
                output.AppendLine($"| {blocker.Games} | {EscapeMarkdown(blocker.Finding)} |");
            }
        }

        AppendActionableSection(
            output,
            "Failures",
            report.Games.Where(game =>
                game.Status is CompatibilityStatus.Failed or CompatibilityStatus.Invalid));
        AppendActionableSection(
            output,
            "Warnings",
            report.Games.Where(game => game.Status == CompatibilityStatus.Warning));

        output.AppendLine();
        output.AppendLine("## Interpretation");
        output.AppendLine();
        output.AppendLine(
            "`Pass` proves only this bounded automated route; it does not certify the whole game. " +
            "`Warning` highlights unverified cartridges, missing activity, unsupported HLE work, " +
            "or performance below realtime. `Failed` is a runtime, CPU, audio-integrity, or exact " +
            "save-state failure. The audit creates no battery-save files and never modifies ROMs. " +
            "Full counters remain in `games.csv` and `report.json`.");
        return output.ToString();
    }

    private static void AppendActionableSection(
        StringBuilder output,
        string heading,
        IEnumerable<GameCompatibilityResult> games)
    {
        var actionable = games.ToArray();
        output.AppendLine();
        output.AppendLine($"## {heading}");
        output.AppendLine();
        if (actionable.Length == 0)
        {
            output.AppendLine("None.");
            return;
        }

        output.AppendLine("| Game | Code | Fields | PC | Finding | Capture |");
        output.AppendLine("| --- | --- | ---: | --- | --- | --- |");
        foreach (var game in actionable)
        {
            var findings = game.Findings.Count == 0
                ? game.Status.ToString()
                : string.Join("; ", game.Findings);
            var capture = game.CapturePath is null
                ? "-"
                : $"[{EscapeMarkdown(Path.GetFileName(game.CapturePath))}]({EscapeLink(game.CapturePath)})";
            output.AppendLine(
                $"| {EscapeMarkdown(game.RelativePath)} | {EscapeMarkdown(game.GameCode ?? "-")} | " +
                $"{game.FieldsCompleted} | `0x{game.ProgramCounter:X8}` | " +
                $"{EscapeMarkdown(findings)} | {capture} |");
        }
    }

    private static void AppendCsvRow(StringBuilder output, params string[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                output.Append(',');
            }

            var value = values[index];
            if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
            {
                output.Append('"');
                output.Append(value.Replace("\"", "\"\"", StringComparison.Ordinal));
                output.Append('"');
            }
            else
            {
                output.Append(value);
            }
        }

        output.AppendLine();
    }

    private static string EscapeMarkdown(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static string EscapeLink(string value) =>
        value.Replace(" ", "%20", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal);
}

internal sealed record CompatibilityReportPaths(
    string JsonPath,
    string CsvPath,
    string MarkdownPath);
