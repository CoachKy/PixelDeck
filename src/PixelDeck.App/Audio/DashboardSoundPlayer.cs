using System.Diagnostics;
using NAudio.Wave;

namespace PixelDeck.App.Audio;

internal sealed class DashboardSoundPlayer : IDisposable
{
    private const int SampleRate = 44_100;
    private static readonly WaveFormat Format = new(SampleRate, 16, 1);

    private readonly object _sync = new();
    private WaveOutEvent? _output;
    private RawSourceWaveStream? _source;
    private long _lastNavigationTone;
    private bool _isUnavailable;

    public void PlayNavigation()
    {
        lock (_sync)
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastNavigationTone != 0 && Stopwatch.GetElapsedTime(_lastNavigationTone, now) < TimeSpan.FromMilliseconds(25))
            {
                return;
            }

            _lastNavigationTone = now;
            PlayLocked([
                new ToneSegment(720, 900, 48, 0.15, 0)
            ]);
        }
    }

    public void PlayConfirm()
    {
        lock (_sync)
        {
            PlayLocked([
                new ToneSegment(540, 660, 55, 0.16, 10),
                new ToneSegment(820, 1_040, 92, 0.18, 0)
            ]);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            DisposePlaybackLocked();
        }
    }

    private void PlayLocked(IReadOnlyList<ToneSegment> tones)
    {
        if (_isUnavailable || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            DisposePlaybackLocked();
            var samples = Render(tones);
            _source = new RawSourceWaveStream(new MemoryStream(samples, writable: false), Format);
            _output = new WaveOutEvent
            {
                DesiredLatency = 50,
                NumberOfBuffers = 2,
                Volume = 0.55f
            };
            _output.Init(_source);
            _output.Play();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            DisposePlaybackLocked();
            _isUnavailable = true;
        }
    }

    private void DisposePlaybackLocked()
    {
        _output?.Stop();
        _output?.Dispose();
        _source?.Dispose();
        _output = null;
        _source = null;
    }

    private static byte[] Render(IReadOnlyList<ToneSegment> tones)
    {
        var sampleCount = tones.Sum(tone => MillisecondsToSamples(tone.DurationMilliseconds + tone.GapMilliseconds));
        var pcm = new byte[sampleCount * sizeof(short)];
        var writeIndex = 0;
        var phase = 0d;

        foreach (var tone in tones)
        {
            var toneSamples = MillisecondsToSamples(tone.DurationMilliseconds);
            var attackSamples = Math.Max(1, MillisecondsToSamples(5));
            var releaseSamples = Math.Max(1, MillisecondsToSamples(12));

            for (var index = 0; index < toneSamples; index++)
            {
                var progress = toneSamples <= 1 ? 1d : index / (double)(toneSamples - 1);
                var frequency = tone.StartFrequency + ((tone.EndFrequency - tone.StartFrequency) * progress);
                phase += Math.Tau * frequency / SampleRate;

                var attack = Math.Min(1d, index / (double)attackSamples);
                var release = Math.Min(1d, (toneSamples - index - 1) / (double)releaseSamples);
                var envelope = Math.Min(attack, release);
                var sample = (short)(Math.Sin(phase) * short.MaxValue * tone.Amplitude * envelope);
                pcm[writeIndex++] = (byte)(sample & 0xFF);
                pcm[writeIndex++] = (byte)((sample >> 8) & 0xFF);
            }

            writeIndex += MillisecondsToSamples(tone.GapMilliseconds) * sizeof(short);
        }

        return pcm;
    }

    private static int MillisecondsToSamples(int milliseconds) =>
        (int)Math.Round(SampleRate * milliseconds / 1_000d);

    private readonly record struct ToneSegment(
        double StartFrequency,
        double EndFrequency,
        int DurationMilliseconds,
        double Amplitude,
        int GapMilliseconds);
}
