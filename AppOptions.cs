using System.Globalization;

namespace VoicemeeterDelay;

[Flags]
internal enum CallbackMode
{
    None = 0,
    Input = 1,
    Output = 2,
    Main = 4
}

internal sealed record DelayTarget(
    CallbackMode Mode,
    string Name,
    ChannelSelection Channels,
    double DelayMilliseconds,
    double Gain);

internal sealed record AudioRoute(
    string Name,
    int SourceChannel,
    int DestinationChannel,
    double DelayMilliseconds,
    double Gain,
    bool MuteSource);

internal sealed record AppOptions(
    IReadOnlyList<DelayTarget> Targets,
    IReadOnlyList<AudioRoute> Routes,
    CallbackMode RegisterMode,
    string? DllPath,
    bool SelfTest)
{
    public const double MaxDelayMilliseconds = 10_000;

    public static AppOptions Parse(string[] args)
    {
        var mode = CallbackMode.Output;
        var delayMilliseconds = 250.0;
        var channels = ChannelSelection.All;
        string? dllPath = null;
        var selfTest = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--help":
                case "-h":
                    throw new HelpRequestedException();

                case "--self-test":
                    selfTest = true;
                    break;

                case "--mode":
                case "-m":
                    mode = ParseMode(ReadValue(args, ref i, arg));
                    break;

                case "--delay-ms":
                case "-d":
                    delayMilliseconds = ParseDelay(ReadValue(args, ref i, arg));
                    break;

                case "--channels":
                case "-c":
                    channels = ChannelSelection.Parse(ReadValue(args, ref i, arg));
                    break;

                case "--dll":
                    dllPath = ReadValue(args, ref i, arg);
                    break;

                default:
                    throw new ArgumentException($"Unknown option '{arg}'. Run with --help for usage.");
            }
        }

        var targets = delayMilliseconds > 0
            ? new[] { new DelayTarget(mode, "Command line", channels, delayMilliseconds, Gain: 1.0) }
            : [];
        var registerMode = delayMilliseconds > 0 ? mode : CallbackMode.None;
        return new AppOptions(targets, Routes: [], registerMode, dllPath, selfTest);
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }

    private static CallbackMode ParseMode(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "input" or "in" => CallbackMode.Input,
            "output" or "out" => CallbackMode.Output,
            "main" or "all" => CallbackMode.Main,
            _ => throw new ArgumentException("Mode must be input, output, or main.")
        };
    }

    private static double ParseDelay(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var delay))
        {
            throw new ArgumentException("Delay must be a number in milliseconds.");
        }

        if (delay < 0 || delay > MaxDelayMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Delay must be between 0 and {MaxDelayMilliseconds:0} ms.");
        }

        return delay;
    }

    public static string Usage =>
        """
        VoicemeeterDelay

        Usage:
          VoicemeeterDelay --delay-ms 250 [--mode output] [--channels 1,2] [--dll PATH]

        Options:
          -d, --delay-ms   Delay amount in milliseconds. Default: 250.
          -m, --mode       Voicemeeter callback point: input, output, or main. Default: output.
          -c, --channels   One-based channel list/ranges to delay. Default: all writable callback channels.
              --dll        Explicit path to VoicemeeterRemote64.dll.
              --self-test  Run delay-line checks without loading Voicemeeter.
          -h, --help       Show this help.

        Examples:
          VoicemeeterDelay --delay-ms 120 --mode input --channels 1,2
          VoicemeeterDelay --delay-ms 500 --mode output --channels 3-4
        """;
}

internal sealed class HelpRequestedException : Exception;
