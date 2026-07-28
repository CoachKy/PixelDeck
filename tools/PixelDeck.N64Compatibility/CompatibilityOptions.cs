namespace PixelDeck.N64Compatibility;

internal sealed record CompatibilityOptions(
    string GamesFolder,
    string OutputFolder,
    int FieldsPerGame,
    int Parallelism,
    string? Filter,
    bool CaptureFlaggedFrames,
    bool Strict)
{
    public static CompatibilityOptions Parse(string[] args)
    {
        var repositoryRoot = FindRepositoryRoot(Environment.CurrentDirectory);
        var gamesFolder = Path.Combine(repositoryRoot, "Games", "Nintendo64");
        var outputFolder = Path.Combine(
            repositoryRoot,
            "artifacts",
            "n64-compatibility",
            $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        var fields = 600;
        var parallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        string? filter = null;
        var captureFlaggedFrames = true;
        var strict = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--games":
                    gamesFolder = ReadValue(args, ref index, "--games");
                    break;
                case "--output":
                    outputFolder = ReadValue(args, ref index, "--output");
                    break;
                case "--fields":
                    fields = ParseNumber(ReadValue(args, ref index, "--fields"), "--fields", 2, 3_600);
                    break;
                case "--parallel":
                    parallelism = ParseNumber(
                        ReadValue(args, ref index, "--parallel"),
                        "--parallel",
                        1,
                        Math.Max(1, Environment.ProcessorCount));
                    break;
                case "--filter":
                    filter = ReadValue(args, ref index, "--filter");
                    break;
                case "--no-captures":
                    captureFlaggedFrames = false;
                    break;
                case "--strict":
                    strict = true;
                    break;
                case "--help":
                case "-h":
                    throw new CompatibilityHelpRequestedException();
                default:
                    throw new ArgumentException($"Unknown compatibility option '{args[index]}'.");
            }
        }

        return new(
            Path.GetFullPath(gamesFolder),
            Path.GetFullPath(outputFolder),
            fields,
            parallelism,
            string.IsNullOrWhiteSpace(filter) ? null : filter,
            captureFlaggedFrames,
            strict);
    }

    public static string HelpText =>
        """
        Pixel64 compatibility laboratory

          --games <folder>    N64 image folder (default: Games/Nintendo64)
          --output <folder>   Report folder (default: artifacts/n64-compatibility/run-*)
          --fields <count>    Video fields per image, 2-3600 (default: 600)
          --parallel <count>  Concurrent emulators (default: up to 4)
          --filter <text>     Audit matching filenames only
          --no-captures       Do not create BMP captures for warnings/failures
          --strict            Return a failure exit code for failed/invalid images
          --help              Show this help
        """;

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return args[index];
    }

    private static int ParseNumber(string value, string option, int minimum, int maximum)
    {
        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
        {
            throw new ArgumentOutOfRangeException(
                option,
                value,
                $"{option} must be between {minimum} and {maximum}.");
        }

        return parsed;
    }

    private static string FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PixelDeck.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Path.GetFullPath(start);
    }
}

internal sealed class CompatibilityHelpRequestedException : Exception;
