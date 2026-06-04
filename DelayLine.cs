namespace VoicemeeterDelay;

internal sealed class DelayLine
{
    private readonly float[][] _buffers;
    private readonly int[] _positions;
    private readonly int _delaySamples;

    public DelayLine(int channels, int delaySamples)
    {
        if (channels < 1 || channels > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), "Channel count must be between 1 and 128.");
        }

        if (delaySamples < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delaySamples), "Delay samples cannot be negative.");
        }

        ChannelCount = channels;
        _delaySamples = delaySamples;
        _buffers = new float[channels][];
        _positions = new int[channels];

        if (delaySamples == 0)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                _buffers[channel] = Array.Empty<float>();
            }

            return;
        }

        for (var channel = 0; channel < channels; channel++)
        {
            _buffers[channel] = new float[delaySamples];
        }
    }

    public int ChannelCount { get; }

    public int DelaySamples => _delaySamples;

    public float Process(int channel, float input)
    {
        if (_delaySamples == 0)
        {
            return input;
        }

        var buffer = _buffers[channel];
        var position = _positions[channel];
        var delayed = buffer[position];
        buffer[position] = input;

        position++;
        if (position == buffer.Length)
        {
            position = 0;
        }

        _positions[channel] = position;
        return delayed;
    }

    public static int MillisecondsToSamples(double milliseconds, int sampleRate)
    {
        return checked((int)Math.Round(sampleRate * milliseconds / 1000.0, MidpointRounding.AwayFromZero));
    }
}
