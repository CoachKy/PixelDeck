using PixelDeck.N64Compatibility;

try
{
    var options = CompatibilityOptions.Parse(args);
    Directory.CreateDirectory(options.OutputFolder);
    Console.WriteLine("Pixel64 compatibility laboratory");
    Console.WriteLine($"Games:    {options.GamesFolder}");
    Console.WriteLine($"Output:   {options.OutputFolder}");
    Console.WriteLine($"Fields:   {options.FieldsPerGame}");
    Console.WriteLine($"Parallel: {options.Parallelism}");
    if (options.Filter is not null)
    {
        Console.WriteLine($"Filter:   {options.Filter}");
    }

    Console.WriteLine();
    var outputLock = new object();
    var runner = new N64CompatibilityRunner();
    var report = await runner.RunAsync(
        options,
        (completed, total, game) =>
        {
            lock (outputLock)
            {
                Console.WriteLine(
                    $"[{completed,3}/{total}] {game.Status,-8} " +
                    $"{game.GameCode ?? "----",-4} {game.RelativePath}");
            }
        });
    var paths = CompatibilityReportWriter.Write(report, options.OutputFolder);

    Console.WriteLine();
    Console.WriteLine(
        $"Pass {report.Summary.Passed}, warning {report.Summary.Warnings}, " +
        $"failed {report.Summary.Failed}, invalid {report.Summary.Invalid}.");
    Console.WriteLine($"Report: {paths.MarkdownPath}");
    Console.WriteLine($"CSV:    {paths.CsvPath}");
    Console.WriteLine($"JSON:   {paths.JsonPath}");
    return options.Strict && (report.Summary.Failed > 0 || report.Summary.Invalid > 0)
        ? 2
        : 0;
}
catch (CompatibilityHelpRequestedException)
{
    Console.WriteLine(CompatibilityOptions.HelpText);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Compatibility laboratory failed: {exception.Message}");
    return 1;
}
