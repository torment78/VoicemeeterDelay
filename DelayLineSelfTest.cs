namespace VoicemeeterDelay;

internal static class DelayLineSelfTest
{
    public static void Run()
    {
        AssertZeroDelayPassesThrough();
        AssertDelayedSamplesAppearAtExpectedOffset();
        AssertChannelsStayIndependent();
    }

    private static void AssertZeroDelayPassesThrough()
    {
        var delay = new DelayLine(channels: 1, delaySamples: 0);
        Expect(0.25f, delay.Process(0, 0.25f), "Zero-delay sample 1");
        Expect(-0.5f, delay.Process(0, -0.5f), "Zero-delay sample 2");
    }

    private static void AssertDelayedSamplesAppearAtExpectedOffset()
    {
        var delay = new DelayLine(channels: 1, delaySamples: 3);

        Expect(0, delay.Process(0, 1), "Delayed sample 1");
        Expect(0, delay.Process(0, 2), "Delayed sample 2");
        Expect(0, delay.Process(0, 3), "Delayed sample 3");
        Expect(1, delay.Process(0, 4), "Delayed sample 4");
        Expect(2, delay.Process(0, 5), "Delayed sample 5");
    }

    private static void AssertChannelsStayIndependent()
    {
        var delay = new DelayLine(channels: 2, delaySamples: 1);

        Expect(0, delay.Process(0, 10), "Channel 0 priming");
        Expect(0, delay.Process(1, 20), "Channel 1 priming");
        Expect(10, delay.Process(0, 11), "Channel 0 delayed");
        Expect(20, delay.Process(1, 21), "Channel 1 delayed");
    }

    private static void Expect(float expected, float actual, string label)
    {
        if (MathF.Abs(expected - actual) > 0.000_001f)
        {
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}.");
        }
    }
}
