namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Ring buffer for audio output sample streaming from Audio Interface (AI) DMA.
/// </summary>
public sealed class GameCubeAudioOutput
{
    private readonly short[] _buffer;
    private int _head;
    private int _tail;
    private int _count;

    public int SampleRate { get; set; } = 48000;
    public int Channels => 2; // Stereo

    public GameCubeAudioOutput(int capacitySamples = 16384)
    {
        _buffer = new short[capacitySamples];
    }

    public int AvailableSamples => _count;

    /// <summary>
    /// Writes stereo 16-bit PCM samples into the audio output queue.
    /// </summary>
    public void WriteSamples(ReadOnlySpan<short> pcmStereo)
    {
        for (var i = 0; i < pcmStereo.Length; i++)
        {
            if (_count >= _buffer.Length)
            {
                break; // Buffer full; drop overflow
            }

            _buffer[_tail] = pcmStereo[i];
            _tail = (_tail + 1) % _buffer.Length;
            _count++;
        }
    }

    /// <summary>
    /// Reads stereo 16-bit PCM samples from the queue into <paramref name="destination"/>.
    /// </summary>
    public int ReadSamples(Span<short> destination)
    {
        var read = 0;
        for (var i = 0; i < destination.Length && _count > 0; i++)
        {
            destination[i] = _buffer[_head];
            _head = (_head + 1) % _buffer.Length;
            _count--;
            read++;
        }

        return read;
    }

    /// <summary>
    /// Reads float samples normalized to [-1.0, 1.0] into <paramref name="destination"/>.
    /// </summary>
    public int ReadAudioSamples(Span<float> destination)
    {
        var count = Math.Min(destination.Length, _count);
        if (count <= 0) return 0;

        Span<short> pcm = stackalloc short[128];
        var readTotal = 0;

        while (readTotal < count)
        {
            var chunk = Math.Min(pcm.Length, count - readTotal);
            var read = ReadSamples(pcm[..chunk]);
            if (read <= 0) break;

            for (var i = 0; i < read; i++)
            {
                destination[readTotal + i] = pcm[i] / 32768.0f;
            }
            readTotal += read;
        }

        return readTotal;
    }

    public void Clear()
    {
        _head = 0;
        _tail = 0;
        _count = 0;
    }
}
