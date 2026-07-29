using System.Text.Json;
using PixelDeck.Updater;

// PixelDeck.Updater replaces an installed PixelDeck with a staged one and
// relaunches it. It runs as a separate process because it overwrites the files
// of the application that started it.
//
//   PixelDeck.Updater --staging <dir> --install <dir> --executable <name>
//                     --from <version> --to <version> [--wait-for <pid>]

var arguments = ParseArguments(args);

var logPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "PixelDeck",
    "update-diagnostics.log");

void Log(string message)
{
    var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  [updater] {message}";
    Console.WriteLine(line);
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.AppendAllText(logPath, line + Environment.NewLine);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        // Logging must never be the reason an update fails.
    }
}

if (!arguments.TryGetValue("staging", out var staging) ||
    !arguments.TryGetValue("install", out var install) ||
    !arguments.TryGetValue("executable", out var executable))
{
    Log("Missing required arguments; nothing to do.");
    return 2;
}

var previousVersion = arguments.GetValueOrDefault("from", "unknown");
var targetVersion = arguments.GetValueOrDefault("to", "unknown");
int? waitFor = arguments.TryGetValue("wait-for", out var pidText) && int.TryParse(pidText, out var pid)
    ? pid
    : null;

var installer = new UpdateInstaller(Log);
var outcome = installer.Install(new InstallRequest(
    staging, install, executable, previousVersion, targetVersion, waitFor));

// The relaunched PixelDeck reads this to confirm or report the update.
WritePendingState(outcome.Succeeded ? null : outcome.Failure);

installer.Relaunch(install, executable, previousVersion);
return outcome.Succeeded ? 0 : 1;

void WritePendingState(string? failure)
{
    var statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixelDeck",
        "pending-update.json");

    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath, JsonSerializer.Serialize(
            new { targetVersion, previousVersion, failure },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        Log($"Could not write the update result: {exception.Message}");
    }
}

static Dictionary<string, string> ParseArguments(string[] args)
{
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (args[index].StartsWith("--", StringComparison.Ordinal))
        {
            parsed[args[index][2..]] = args[index + 1];
        }
    }

    return parsed;
}
