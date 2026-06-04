using System.Globalization;

namespace VoicemeeterDelay;

internal sealed class ChannelSelection
{
    private readonly ChannelRange[] _ranges;

    private ChannelSelection(bool isAll, ChannelRange[] ranges)
    {
        IsAll = isAll;
        _ranges = ranges;
    }

    public static ChannelSelection All { get; } = new(isAll: true, Array.Empty<ChannelRange>());

    public bool IsAll { get; }

    public static ChannelSelection Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return All;
        }

        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return All;
        }

        var ranges = new List<ChannelRange>();
        foreach (var part in parts)
        {
            ranges.Add(ParsePart(part));
        }

        ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return new ChannelSelection(isAll: false, MergeRanges(ranges).ToArray());
    }

    public static ChannelSelection FromZeroBasedRanges(IEnumerable<ChannelRange> ranges)
    {
        var normalizedRanges = ranges.ToList();
        if (normalizedRanges.Count == 0)
        {
            return All;
        }

        foreach (var range in normalizedRanges)
        {
            if (range.Start < 0 || range.End < range.Start || range.End > 127)
            {
                throw new ArgumentOutOfRangeException(nameof(ranges), "Channel ranges must be zero-based and between 0 and 127.");
            }
        }

        normalizedRanges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return new ChannelSelection(isAll: false, MergeRanges(normalizedRanges).ToArray());
    }

    public static ChannelSelection FromZeroBasedChannels(IEnumerable<int> channels)
    {
        return FromZeroBasedRanges(channels
            .Distinct()
            .Order()
            .Select(static channel => new ChannelRange(channel, channel)));
    }

    public bool IncludesZeroBased(int channel)
    {
        if (IsAll)
        {
            return true;
        }

        foreach (var range in _ranges)
        {
            if (channel < range.Start)
            {
                return false;
            }

            if (channel <= range.End)
            {
                return true;
            }
        }

        return false;
    }

    public override string ToString()
    {
        if (IsAll)
        {
            return "all";
        }

        return string.Join(",", _ranges.Select(static range =>
            range.Start == range.End
                ? (range.Start + 1).ToString(CultureInfo.InvariantCulture)
                : $"{range.Start + 1}-{range.End + 1}"));
    }

    private static ChannelRange ParsePart(string part)
    {
        var separator = part.IndexOf('-', StringComparison.Ordinal);
        if (separator < 0)
        {
            var channel = ParseOneBasedChannel(part);
            return new ChannelRange(channel, channel);
        }

        var start = ParseOneBasedChannel(part[..separator]);
        var end = ParseOneBasedChannel(part[(separator + 1)..]);
        if (end < start)
        {
            throw new ArgumentException($"Invalid channel range '{part}'.");
        }

        return new ChannelRange(start, end);
    }

    private static int ParseOneBasedChannel(string value)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel))
        {
            throw new ArgumentException($"Invalid channel '{value}'.");
        }

        if (channel < 1 || channel > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Channels must be between 1 and 128.");
        }

        return channel - 1;
    }

    private static IEnumerable<ChannelRange> MergeRanges(List<ChannelRange> ranges)
    {
        if (ranges.Count == 0)
        {
            yield break;
        }

        var current = ranges[0];
        for (var i = 1; i < ranges.Count; i++)
        {
            var next = ranges[i];
            if (next.Start <= current.End + 1)
            {
                current = current with { End = Math.Max(current.End, next.End) };
                continue;
            }

            yield return current;
            current = next;
        }

        yield return current;
    }

}

internal readonly record struct ChannelRange(int Start, int End);
