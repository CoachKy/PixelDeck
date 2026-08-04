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

    public void Clear()
    {
        _head = 0;
        _tail = 0;
        _count = 0;
    }
}
