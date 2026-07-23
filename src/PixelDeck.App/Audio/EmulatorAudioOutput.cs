using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;
using PixelDeck.Emulation.Nes;
using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.Audio;

internal sealed class EmulatorAudioOutput : IDisposable
{
    private readonly EmulatorSampleProvider _provider;
    private WaveOutEvent? _output;

    public EmulatorAudioOutput(NesMachine machine)
        : this(new EmulatorSampleProvider(machine))
    {
    }

    public EmulatorAudioOutput(SnesMachine machine)
        : this(new EmulatorSampleProvider(machine))
    {
    }

    private EmulatorAudioOutput(EmulatorSampleProvider provider)
    {
        _provider = provider;
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _output = new WaveOutEvent
            {
                DesiredLatency = 80,
                NumberOfBuffers = 3
            };
            _output.Init(_provider);
            _output.Play();
            IsAvailable = true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            _output?.Dispose();
            _output = null;
        }
    }

    public bool IsAvailable { get; }

    public long UnderrunSampleCount => _provider.UnderrunSampleCount;

    public void SetMachine(NesMachine machine) => _provider.SetMachine(machine);

    public void SetMachine(SnesMachine machine) => _provider.SetMachine(machine);

    public bool IsPaused
    {
        set => _provider.IsPaused = value;
    }

    public void Dispose()
    {
        _provider.ClearMachine();
        _output?.Stop();
        _output?.Dispose();
        _output = null;
    }

    private sealed class EmulatorSampleProvider : IWaveProvider
    {
        private NesMachine? _nesMachine;
        private SnesMachine? _snesMachine;
        private int _isPaused;
        private int _hasStarted;
        private long _underrunSampleCount;

        public EmulatorSampleProvider(NesMachine machine)
        {
            _nesMachine = machine;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(NesMachine.AudioSampleRate, 1);
        }

        public EmulatorSampleProvider(SnesMachine machine)
        {
            _snesMachine = machine;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SnesMachine.AudioSampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public long UnderrunSampleCount => Interlocked.Read(ref _underrunSampleCount);

        public bool IsPaused
        {
            set => Volatile.Write(ref _isPaused, value ? 1 : 0);
        }

        public void SetMachine(NesMachine machine)
        {
            if (WaveFormat.Channels != 1)
            {
                throw new InvalidOperationException("Cannot attach an NES machine to a stereo SNES audio stream.");
            }

            Volatile.Write(ref _snesMachine, null);
            Volatile.Write(ref _nesMachine, machine);
        }

        public void SetMachine(SnesMachine machine)
        {
            if (WaveFormat.Channels != 2)
            {
                throw new InvalidOperationException("Cannot attach an SNES machine to a mono NES audio stream.");
            }

            Volatile.Write(ref _nesMachine, null);
            Volatile.Write(ref _snesMachine, machine);
        }

        public void ClearMachine()
        {
            Volatile.Write(ref _nesMachine, null);
            Volatile.Write(ref _snesMachine, null);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var destination = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
            var samplesRead = 0;
            if (Volatile.Read(ref _isPaused) == 0)
            {
                var nesMachine = Volatile.Read(ref _nesMachine);
                var snesMachine = Volatile.Read(ref _snesMachine);
                if (nesMachine is not null)
                {
                    samplesRead = nesMachine.ReadAudioSamples(destination);
                }
                else if (snesMachine is not null)
                {
                    samplesRead = snesMachine.ReadAudioSamples(destination);
                }

                if (samplesRead > 0)
                {
                    Volatile.Write(ref _hasStarted, 1);
                }

                if (samplesRead < destination.Length && Volatile.Read(ref _hasStarted) != 0)
                {
                    Interlocked.Add(ref _underrunSampleCount, destination.Length - samplesRead);
                }
            }

            destination[samplesRead..].Clear();
            return count;
        }
    }
}
