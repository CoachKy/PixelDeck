using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PixelDeck.App.Models;

public sealed record GameEntry(
    string Title,
    string Platform,
    string PlatformCode,
    string FileName,
    string FullPath,
    string RelativePath,
    string SizeText,
    string ModifiedText,
    Color AccentColor) : IDisposable, INotifyPropertyChanged
{
    private long _totalPlayTimeTicks;
    private int _sessionCount;
    private DateTime? _lastPlayedUtc;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? ScreenshotPath { get; init; }

    public Bitmap? Screenshot { get; private set; }

    public string ScreenshotCachePath { get; init; } = string.Empty;

    public string SaveRamPath { get; init; } = string.Empty;

    public int? MapperNumber { get; init; }

    public int SubmapperNumber { get; init; }

    public string? CartridgeDescription { get; init; }

    public bool IsCoreSupported { get; init; }

    public bool IsLimitedCompatibility { get; init; }

    public string CompatibilityText { get; init; } = "DASHBOARD ONLY";

    public string MapperText => CartridgeDescription ?? (MapperNumber is null
        ? "-"
        : SubmapperNumber == 0
            ? $"MAPPER {MapperNumber}"
            : $"MAPPER {MapperNumber} / SUB {SubmapperNumber}");

    public string LaunchBadgeText => IsCoreSupported
        ? IsLimitedCompatibility ? "PARTIAL" : "READY"
        : "UNSUPPORTED";

    public bool HasScreenshot => Screenshot is not null;

    public bool HasNoScreenshot => Screenshot is null;

    public bool CanLaunch => IsCoreSupported;

    public TimeSpan TotalPlayTime => TimeSpan.FromTicks(_totalPlayTimeTicks);

    public int SessionCount => _sessionCount;

    public DateTime? LastPlayedUtc => _lastPlayedUtc;

    public string PlayTimeText => TotalPlayTime.TotalSeconds <= 0
        ? "NOT PLAYED YET"
        : TotalPlayTime.TotalMinutes < 1
            ? "< 1 MIN PLAYED"
            : TotalPlayTime.TotalHours < 1
                ? $"{(int)TotalPlayTime.TotalMinutes} MIN PLAYED"
                : TotalPlayTime.Minutes == 0
                    ? $"{(long)TotalPlayTime.TotalHours}H PLAYED"
                    : $"{(long)TotalPlayTime.TotalHours}H {TotalPlayTime.Minutes}M PLAYED";

    public string LastPlayedText
    {
        get
        {
            if (LastPlayedUtc is null)
            {
                return "NEVER PLAYED";
            }

            var local = LastPlayedUtc.Value.ToLocalTime();
            if (local.Date == DateTime.Today)
            {
                return $"TODAY / {local:h:mm tt}".ToUpperInvariant();
            }

            if (local.Date == DateTime.Today.AddDays(-1))
            {
                return $"YESTERDAY / {local:h:mm tt}".ToUpperInvariant();
            }

            return local.ToString("MMM d / h:mm tt").ToUpperInvariant();
        }
    }

    public void UpdatePlayHistory(long totalPlayTimeTicks, int sessionCount, DateTime? lastPlayedUtc)
    {
        var safeTicks = Math.Max(0, totalPlayTimeTicks);
        var safeSessionCount = Math.Max(0, sessionCount);
        if (_totalPlayTimeTicks == safeTicks && _sessionCount == safeSessionCount && _lastPlayedUtc == lastPlayedUtc)
        {
            return;
        }

        _totalPlayTimeTicks = safeTicks;
        _sessionCount = safeSessionCount;
        _lastPlayedUtc = lastPlayedUtc;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TotalPlayTime)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SessionCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastPlayedUtc)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayTimeText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastPlayedText)));
    }

    public void LoadScreenshot()
    {
        if (ScreenshotPath is null || Screenshot is not null)
        {
            return;
        }

        try
        {
            Screenshot = new Bitmap(ScreenshotPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Screenshot = null;
        }
    }

    public void Dispose()
    {
        Screenshot?.Dispose();
        Screenshot = null;
    }
}
