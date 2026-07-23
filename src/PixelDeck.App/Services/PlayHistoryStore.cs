using System.Diagnostics;
using System.Text.Json;
using PixelDeck.App.Models;

namespace PixelDeck.App.Services;

public sealed class PlayHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _sync = new();
    private readonly string _historyPath;

    public PlayHistoryStore(string historyPath)
    {
        _historyPath = Path.GetFullPath(historyPath);
    }

    public static PlayHistoryStore Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixelDeck",
        "play-history.json"));

    public IReadOnlyList<GamePlayHistory> Read()
    {
        lock (_sync)
        {
            return Load().ToArray();
        }
    }

    public void RecordSession(GameEntry game, TimeSpan duration, DateTime? endedAtUtc = null)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        lock (_sync)
        {
            var entries = Load();
            var key = NormalizeKey(game.RelativePath);
            var existingIndex = entries.FindIndex(entry => entry.GameKey == key);
            var existing = existingIndex >= 0 ? entries[existingIndex] : null;
            var updated = new GamePlayHistory(
                key,
                game.RelativePath,
                game.Title,
                game.Platform,
                (existing?.TotalPlayTimeTicks ?? 0) + duration.Ticks,
                (existing?.SessionCount ?? 0) + 1,
                endedAtUtc ?? DateTime.UtcNow);

            if (existingIndex >= 0)
            {
                entries[existingIndex] = updated;
            }
            else
            {
                entries.Add(updated);
            }

            Save(entries);
        }
    }

    private List<GamePlayHistory> Load()
    {
        try
        {
            if (!File.Exists(_historyPath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<GamePlayHistory>>(File.ReadAllText(_historyPath), JsonOptions) ?? [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine(exception);
            return [];
        }
    }

    private void Save(List<GamePlayHistory> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
            var temporaryPath = _historyPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, JsonOptions));
            File.Move(temporaryPath, _historyPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(exception);
        }
    }

    private static string NormalizeKey(string relativePath) =>
        relativePath.Replace('\\', '/').ToUpperInvariant();
}

public sealed record GamePlayHistory(
    string GameKey,
    string RelativePath,
    string Title,
    string Platform,
    long TotalPlayTimeTicks,
    int SessionCount,
    DateTime LastPlayedUtc);
