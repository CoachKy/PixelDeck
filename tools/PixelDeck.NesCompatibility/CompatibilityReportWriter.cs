using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PixelDeck.NesCompatibility;

internal static class CompatibilityReportWriter
{
    public static CompatibilityReportPaths Write(
        NesCompatibilityReport report,
        string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        var jsonPath = Path.Combine(outputFolder, "report.json");
        var csvPath = Path.Combine(outputFolder, "games.csv");
        var markdownPath = Path.Combine(outputFolder, "REPORT.md");

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, jsonOptions), new UTF8Encoding(false));
        File.WriteAllText(csvPath, BuildCsv(report.Games), new UTF8Encoding(false));
        File.WriteAllText(markdownPath, BuildMarkdown(report), new UTF8Encoding(false));
        return new(jsonPath, csvPath, markdownPath);
    }

    internal static string BuildCsv(IReadOnlyList<GameCompatibilityResult> games)
    {
        var output = new StringBuilder();
        output.AppendLine(
            "status,path,sha256,mapper,submapper,timing,nes20,limited,frames,coreFps,p99Ms,cpuCycles,pc,maxColors,distinctFrames,audioSamples,audioPeak,droppedAudio,stateDeterministic,capture,findings");
        foreach (var game in games)
        {
            AppendCsvRow(
                output,
                game.Status.ToString(),
                game.RelativePath,
                game.Sha256,
                game.Mapper?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                game.Submapper?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                game.TimingMode ?? string.Empty,
                game.IsNes20.ToString(),
                game.IsLimitedCompatibility.ToString(),
                game.FramesCompleted.ToString(CultureInfo.InvariantCulture),
                game.HostFramesPerSecond.ToString("0.000", CultureInfo.InvariantCulture),
                game.P99FrameMilliseconds.ToString("0.000", CultureInfo.InvariantCulture),
                game.CpuCycles.ToString(CultureInfo.InvariantCulture),
                $"0x{game.ProgramCounter:X4}",
                game.MaximumDistinctColors.ToString(CultureInfo.InvariantCulture),
                game.DistinctCheckpointFrames.ToString(CultureInfo.InvariantCulture),
                game.AudioSamples.ToString(CultureInfo.InvariantCulture),
                game.AudioPeak.ToString("0.000000", CultureInfo.InvariantCulture),
                game.DroppedAudioSamples.ToString(CultureInfo.InvariantCulture),
                game.SaveStateDeterministic.ToString(),
                game.CapturePath ?? string.Empty,
                string.Join(" | ", game.Findings));
        }

        return output.ToString();
    }

    internal static string BuildMarkdown(NesCompatibilityReport report)
    {
        var output = new StringBuilder();
        output.AppendLine("# PixelNES compatibility report");
        output.AppendLine();
        output.AppendLine($"- PixelNES: `{report.PixelNesVersion}`");
        output.AppendLine($"- Started: `{report.StartedAtUtc:O}`");
        output.AppendLine($"- Completed: `{report.CompletedAtUtc:O}`");
        output.AppendLine($"- Games folder: `{EscapeMarkdown(report.Configuration.GamesFolder)}`");
        output.AppendLine($"- Frames per supported image: `{report.Configuration.FramesPerGame}`");
        output.AppendLine($"- Parallel emulators: `{report.Configuration.Parallelism}`");
        if (!string.IsNullOrWhiteSpace(report.Configuration.Filter))
        {
            output.AppendLine($"- Filename filter: `{EscapeMarkdown(report.Configuration.Filter)}`");
        }

        output.AppendLine();
        output.AppendLine("## Summary");
        output.AppendLine();
        output.AppendLine("| Total | Unique | Pass | Warning | Failed | Unsupported | Invalid |");
        output.AppendLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        output.AppendLine(
            $"| {report.Summary.Total} | {report.Summary.UniqueImages} | " +
            $"{report.Summary.Passed} | {report.Summary.Warnings} | " +
            $"{report.Summary.Failed} | {report.Summary.Unsupported} | " +
            $"{report.Summary.Invalid} |");

        output.AppendLine();
        output.AppendLine("## Mapper coverage");
        output.AppendLine();
        output.AppendLine("| Mapper | Submapper | Total | Pass | Warning | Failed | Unsupported |");
        output.AppendLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var mapper in report.Mappers)
        {
            output.AppendLine(
                $"| {mapper.Mapper} | {mapper.Submapper} | {mapper.Total} | " +
                $"{mapper.Passed} | {mapper.Warnings} | {mapper.Failed} | " +
                $"{mapper.Unsupported} |");
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
        AppendActionableSection(
            output,
            "Unsupported images",
            report.Games.Where(game => game.Status == CompatibilityStatus.Unsupported));

        output.AppendLine();
        output.AppendLine("## Interpretation");
        output.AppendLine();
        output.AppendLine(
            "`Pass` proves this bounded automated route, not completion of the entire game. " +
            "`Warning` identifies a result requiring review, which may be an intentional silent " +
            "or static scene. `Unsupported` is an explicit hardware-envelope gap. Full per-game " +
            "measurements remain available in `games.csv` and `report.json`.");
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

        output.AppendLine("| Game | Mapper | Finding | Capture |");
        output.AppendLine("| --- | ---: | --- | --- |");
        foreach (var game in actionable)
        {
            var mapper = game.Mapper.HasValue
                ? game.Submapper > 0
                    ? $"{game.Mapper}.{game.Submapper}"
                    : game.Mapper.Value.ToString(CultureInfo.InvariantCulture)
                : "-";
            var findings = game.Findings.Count == 0
                ? game.Status.ToString()
                : string.Join("; ", game.Findings);
            var capture = game.CapturePath is null
                ? "-"
                : $"[{EscapeMarkdown(Path.GetFileName(game.CapturePath))}]({EscapeLink(game.CapturePath)})";
            output.AppendLine(
                $"| {EscapeMarkdown(game.RelativePath)} | {mapper} | " +
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
