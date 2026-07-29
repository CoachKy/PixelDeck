using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;
using PixelDeck.Emulation.N64;
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

    public EmulatorAudioOutput(N64Machine machine)
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

    public void SetMachine(N64Machine machine) => _provider.SetMachine(machine);

    public bool IsPaused
    {
        set => _provider.IsPaused = value;
    }

    public int PlaybackRate
    {
        set => _provider.PlaybackRate = value;
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
        private const int N64PrebufferMilliseconds = 50;

        private NesMachine? _nesMachine;
        private SnesMachine? _snesMachine;
        private N64Machine? _n64Machine;
        private int _isPaused;
        private int _hasStarted;
        private int _playbackRate = 1;
        private int _sourceFramePhase;
        private long _underrunSampleCount;
        private float[] _rateSourceBuffer = new float[8_192];
        private readonly AudioUnderrunSmoother _n64UnderrunSmoother = new();
        private readonly AudioRebufferGate _n64RebufferGate = new();

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

        public EmulatorSampleProvider(N64Machine machine)
        {
            _n64Machine = machine;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(N64Machine.AudioSampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public long UnderrunSampleCount => Interlocked.Read(ref _underrunSampleCount);

        public bool IsPaused
        {
            set => Volatile.Write(ref _isPaused, value ? 1 : 0);
        }

        public int PlaybackRate
        {
            set
            {
                var normalizedRate = value >= 2 ? 2 : 1;
                if (Interlocked.Exchange(ref _playbackRate, normalizedRate) != normalizedRate)
                {
                    Volatile.Write(ref _sourceFramePhase, 0);
                }
            }
        }

        public void SetMachine(NesMachine machine)
        {
            if (WaveFormat.Channels != 1)
            {
                throw new InvalidOperationException("Cannot attach an NES machine to a stereo audio stream.");
            }

            Volatile.Write(ref _snesMachine, null);
            Volatile.Write(ref _n64Machine, null);
            Volatile.Write(ref _nesMachine, machine);
            ResetStreamState();
        }

        public void SetMachine(SnesMachine machine)
        {
            if (WaveFormat.Channels != 2 || WaveFormat.SampleRate != SnesMachine.AudioSampleRate)
            {
                throw new InvalidOperationException("Cannot attach an SNES machine to this audio stream.");
            }

            Volatile.Write(ref _nesMachine, null);
            Volatile.Write(ref _n64Machine, null);
            Volatile.Write(ref _snesMachine, machine);
            ResetStreamState();
        }

        public void SetMachine(N64Machine machine)
        {
            if (WaveFormat.Channels != 2 || WaveFormat.SampleRate != N64Machine.AudioSampleRate)
            {
                throw new InvalidOperationException("Cannot attach an N64 machine to this audio stream.");
            }

            Volatile.Write(ref _nesMachine, null);
            Volatile.Write(ref _snesMachine, null);
            Volatile.Write(ref _n64Machine, machine);
            ResetStreamState();
        }

        public void ClearMachine()
        {
            Volatile.Write(ref _nesMachine, null);
            Volatile.Write(ref _snesMachine, null);
            Volatile.Write(ref _n64Machine, null);
            ResetStreamState();
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var destination = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
            var samplesRead = 0;
            if (Volatile.Read(ref _isPaused) == 0)
            {
                var playbackRate = Volatile.Read(ref _playbackRate);
                if (ShouldWaitForN64Prebuffer(destination.Length, playbackRate))
                {
                    destination.Clear();
                    return count;
                }

                samplesRead = playbackRate == 1
                    ? ReadMachineSamples(destination)
                    : ReadRateConvertedSamples(destination, playbackRate);

                var isN64 = Volatile.Read(ref _n64Machine) is not null;
                if (samplesRead > 0)
                {
                    Volatile.Write(ref _hasStarted, 1);
                }

                if (samplesRead < destination.Length && Volatile.Read(ref _hasStarted) != 0)
                {
                    Interlocked.Add(ref _underrunSampleCount, destination.Length - samplesRead);
                    if (isN64)
                    {
                        _n64RebufferGate.OnUnderrun();
                        _n64UnderrunSmoother.Process(
                            destination,
                            samplesRead,
                            WaveFormat.Channels);
                        return count;
                    }
                }
                else if (isN64 && samplesRead > 0)
                {
                    _n64UnderrunSmoother.Process(
                        destination,
                        samplesRead,
                        WaveFormat.Channels);
                }
            }

            destination[samplesRead..].Clear();
            return count;
        }

        private bool ShouldWaitForN64Prebuffer(int requestedOutputValues, int playbackRate)
        {
            var n64Machine = Volatile.Read(ref _n64Machine);
            if (n64Machine is null)
            {
                return false;
            }

            var requestedSourceValues = playbackRate == 1
                ? requestedOutputValues
                : PlaybackRateAudioConverter.GetRequiredSourceValueCount(
                    requestedOutputValues,
                    WaveFormat.Channels,
                    playbackRate);
            var prebufferValues =
                N64Machine.AudioSampleRate *
                WaveFormat.Channels *
                N64PrebufferMilliseconds /
                1_000;
            var bufferedValues = n64Machine.BufferedAudioSampleCount;

            return _n64RebufferGate.ShouldWait(
                bufferedValues,
                Math.Max(requestedSourceValues, prebufferValues));
        }

        private void ResetStreamState()
        {
            Volatile.Write(ref _hasStarted, 0);
            Volatile.Write(ref _sourceFramePhase, 0);
            _n64RebufferGate.Reset();
            _n64UnderrunSmoother.Reset();
        }

        private int ReadRateConvertedSamples(Span<float> destination, int playbackRate)
        {
            var requiredValues = PlaybackRateAudioConverter.GetRequiredSourceValueCount(
                destination.Length,
                WaveFormat.Channels,
                playbackRate);
            if (_rateSourceBuffer.Length < requiredValues)
            {
                Array.Resize(ref _rateSourceBuffer, requiredValues);
            }

            var sourceValues = ReadMachineSamples(_rateSourceBuffer.AsSpan(0, requiredValues));
            var phase = Volatile.Read(ref _sourceFramePhase);
            var convertedValues = PlaybackRateAudioConverter.Convert(
                _rateSourceBuffer.AsSpan(0, sourceValues),
                destination,
                WaveFormat.Channels,
                playbackRate,
                ref phase);
            Volatile.Write(ref _sourceFramePhase, phase);
            return convertedValues;
        }

        private int ReadMachineSamples(Span<float> destination)
        {
            var nesMachine = Volatile.Read(ref _nesMachine);
            if (nesMachine is not null)
            {
                return nesMachine.ReadAudioSamples(destination);
            }

            var snesMachine = Volatile.Read(ref _snesMachine);
            if (snesMachine is not null)
            {
                return snesMachine.ReadAudioSamples(destination);
            }

            var n64Machine = Volatile.Read(ref _n64Machine);
            return n64Machine?.ReadAudioSamples(destination) ?? 0;
        }
    }
}
