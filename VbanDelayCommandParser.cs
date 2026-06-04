using System.Globalization;
using System.Text.RegularExpressions;

namespace VoicemeeterDelay;

internal enum VbanDelayTargetKind
{
    Strip,
    Bus
}

internal enum VbanDelayProperty
{
    Enable,
    Delay,
    Volume,
    Route,
    RouteEnable,
    RouteMuteNormal
}

internal enum VbanDelayOperator
{
    Set,
    Add,
    Subtract
}

internal sealed record VbanDelayCommand(
    VbanDelayTargetKind TargetKind,
    string Target,
    VbanDelayChannelSelection Channels,
    VbanDelayProperty Property,
    VbanDelayOperator Operator,
    string ValueText,
    string SourceText);

internal sealed record VbanDelayChannelSelection(bool IsAll, IReadOnlyList<int> OneBasedChannels)
{
    public IEnumerable<int> GetZeroBasedChannels(int channelCount)
    {
        if (IsAll)
        {
            return Enumerable.Range(0, channelCount);
        }

        return OneBasedChannels
            .Where(channel => channel >= 1 && channel <= channelCount)
            .Select(channel => channel - 1)
            .Distinct();
    }
}

internal sealed record VbanRouteDestinationText(string BusTarget, int OneBasedChannel);

internal static partial class VbanDelayCommandParser
{
    public static IReadOnlyList<VbanDelayCommand> ParseScript(string script)
    {
        var commands = new List<VbanDelayCommand>();
        foreach (var rawCommand in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(rawCommand))
            {
                continue;
            }

            commands.Add(ParseCommand(rawCommand));
        }

        return commands;
    }

    public static double ParseNumber(string valueText, string valueName)
    {
        var text = valueText.Trim().TrimEnd('%');
        if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^2].Trim();
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            throw new InvalidOperationException($"{valueName} must be a number.");
        }

        return value;
    }

    public static bool ParseBoolean(string valueText)
    {
        return valueText.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "on" or "yes" => true,
            "0" or "false" or "off" or "no" => false,
            _ => throw new InvalidOperationException("Value must be 1/0, true/false, or on/off.")
        };
    }

    public static VbanRouteDestinationText ParseRouteDestination(string valueText)
    {
        var match = RouteDestinationPattern().Match(valueText);
        if (!match.Success)
        {
            throw new InvalidOperationException("Route destination must be Bus(B1).Ch(3).");
        }

        var busTarget = match.Groups["bus"].Value.Trim();
        if (!int.TryParse(match.Groups["channel"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel)
            || channel < 1)
        {
            throw new InvalidOperationException("Route destination channel must be 1 or higher.");
        }

        return new VbanRouteDestinationText(busTarget, channel);
    }

    private static VbanDelayCommand ParseCommand(string rawCommand)
    {
        var match = CommandPattern().Match(rawCommand);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Unsupported VBAN command: {rawCommand}");
        }

        var targetKind = ParseTargetKind(match.Groups["targetKind"].Value);
        var target = match.Groups["target"].Value.Trim();
        var channels = ParseChannels(match.Groups["channels"].Value);
        var property = ParseProperty(match.Groups["property"].Value);
        var op = ParseOperator(match.Groups["op"].Value);
        var valueText = match.Groups["value"].Value.Trim();

        return new VbanDelayCommand(targetKind, target, channels, property, op, valueText, rawCommand);
    }

    private static VbanDelayTargetKind ParseTargetKind(string value)
    {
        return value.Equals("Strip", StringComparison.OrdinalIgnoreCase)
            ? VbanDelayTargetKind.Strip
            : VbanDelayTargetKind.Bus;
    }

    private static VbanDelayProperty ParseProperty(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "enable" or "enabled" => VbanDelayProperty.Enable,
            "delay" or "delayms" or "ms" => VbanDelayProperty.Delay,
            "volume" or "vol" or "gain" => VbanDelayProperty.Volume,
            "route" => VbanDelayProperty.Route,
            "routeenable" or "routeenabled" => VbanDelayProperty.RouteEnable,
            "routemute" or "muteroute" or "mutenormal" or "routemutenormal" => VbanDelayProperty.RouteMuteNormal,
            _ => throw new InvalidOperationException($"Unsupported VBAN property: {value}")
        };
    }

    private static VbanDelayOperator ParseOperator(string value)
    {
        return value switch
        {
            "+=" => VbanDelayOperator.Add,
            "-=" => VbanDelayOperator.Subtract,
            _ => VbanDelayOperator.Set
        };
    }

    private static VbanDelayChannelSelection ParseChannels(string value)
    {
        var text = value.Trim();
        if (text.Equals("all", StringComparison.OrdinalIgnoreCase) || text == "*")
        {
            return new VbanDelayChannelSelection(IsAll: true, []);
        }

        var channels = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var rangeParts = part.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (rangeParts.Length == 2
                && int.TryParse(rangeParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)
                && int.TryParse(rangeParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end)
                && start <= end)
            {
                channels.AddRange(Enumerable.Range(start, end - start + 1));
                continue;
            }

            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel))
            {
                throw new InvalidOperationException($"Channel must be a number, range, or All: {value}");
            }

            channels.Add(channel);
        }

        if (channels.Count == 0)
        {
            throw new InvalidOperationException("At least one channel must be selected.");
        }

        return new VbanDelayChannelSelection(IsAll: false, channels);
    }

    [GeneratedRegex(
        @"^\s*VD\.(?<targetKind>Strip|Bus)\((?<target>[^)]+)\)\.Ch\((?<channels>[^)]+)\)\.(?<property>Enable|Enabled|Delay|DelayMs|Ms|Volume|Vol|Gain|Route|RouteEnable|RouteEnabled|RouteMute|MuteRoute|MuteNormal|RouteMuteNormal)\s*(?<op>\+=|-=|=)\s*(?<value>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandPattern();

    [GeneratedRegex(
        @"^\s*Bus\((?<bus>[^)]+)\)\.Ch\((?<channel>\d+)\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RouteDestinationPattern();
}
