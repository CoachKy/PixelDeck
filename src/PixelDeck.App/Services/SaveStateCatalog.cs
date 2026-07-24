using System.Globalization;

namespace PixelDeck.App.Services;

internal sealed class SaveStateCatalog
{
    private const string StateExtension = ".state";
    private readonly string _legacyStatePath;
    private readonly string _slotPathPrefix;

    public SaveStateCatalog(string legacyStatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyStatePath);
        _legacyStatePath = Path.GetFullPath(legacyStatePath);
        _slotPathPrefix = Path.Combine(
            Path.GetDirectoryName(_legacyStatePath)
                ?? throw new ArgumentException("The save-state path needs a directory.", nameof(legacyStatePath)),
            Path.GetFileNameWithoutExtension(_legacyStatePath) + ".slot-");
    }

    public IReadOnlyList<SaveStateSlot> GetSlots()
    {
        MigrateLegacyState();
        var directory = Path.GetDirectoryName(_legacyStatePath)!;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var slotNumbers = Directory
            .EnumerateFiles(directory, Path.GetFileName(_slotPathPrefix) + "*" + StateExtension + "*")
            .Select(TryParseSlotNumber)
            .Where(number => number.HasValue)
            .Select(number => number!.Value)
            .Distinct()
            .Order()
            .ToArray();

        return slotNumbers
            .Select(number => CreateSlot(number))
            .Where(slot => CrashSafeFile.Exists(slot.Path))
            .ToArray();
    }

    public SaveStateSlot CreateNextSlot()
    {
        var nextNumber = GetSlots().Select(slot => slot.Number).DefaultIfEmpty(0).Max() + 1;
        return CreateSlot(nextNumber);
    }

    internal string GetSlotPath(int number)
    {
        if (number <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(number));
        }

        return _slotPathPrefix + number.ToString("D3", CultureInfo.InvariantCulture) + StateExtension;
    }

    private SaveStateSlot CreateSlot(int number)
    {
        var path = GetSlotPath(number);
        var availablePath = File.Exists(path) ? path : CrashSafeFile.GetTemporaryPath(path);
        var lastWriteTime = File.Exists(availablePath)
            ? File.GetLastWriteTime(availablePath)
            : DateTime.MinValue;
        return new SaveStateSlot(number, path, lastWriteTime);
    }

    private int? TryParseSlotNumber(string path)
    {
        var candidate = path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            ? path[..^4]
            : path;
        if (!candidate.StartsWith(_slotPathPrefix, StringComparison.OrdinalIgnoreCase) ||
            !candidate.EndsWith(StateExtension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var numberText = candidate[
            _slotPathPrefix.Length..^StateExtension.Length];
        return int.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
               number > 0
            ? number
            : null;
    }

    private void MigrateLegacyState()
    {
        if (!CrashSafeFile.Exists(_legacyStatePath))
        {
            return;
        }

        var occupiedNumbers = DiscoverNumberedSlots();
        var targetNumber = 1;
        while (occupiedNumbers.Contains(targetNumber))
        {
            targetNumber++;
        }

        var targetPath = GetSlotPath(targetNumber);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        if (File.Exists(_legacyStatePath))
        {
            File.Move(_legacyStatePath, targetPath);
        }

        var legacyTemporaryPath = CrashSafeFile.GetTemporaryPath(_legacyStatePath);
        if (File.Exists(legacyTemporaryPath))
        {
            File.Move(legacyTemporaryPath, CrashSafeFile.GetTemporaryPath(targetPath));
        }
    }

    private HashSet<int> DiscoverNumberedSlots()
    {
        var directory = Path.GetDirectoryName(_legacyStatePath)!;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(directory, Path.GetFileName(_slotPathPrefix) + "*" + StateExtension + "*")
            .Select(TryParseSlotNumber)
            .Where(number => number.HasValue)
            .Select(number => number!.Value)
            .ToHashSet();
    }
}

internal sealed record SaveStateSlot(int Number, string Path, DateTime LastWriteTime)
{
    public string MenuText =>
        $"Slot {Number}  ·  {(LastWriteTime == DateTime.MinValue ? "Empty" : LastWriteTime.ToString("MMM d, h:mm tt"))}";
}
