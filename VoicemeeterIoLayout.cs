namespace VoicemeeterDelay;

internal enum VoicemeeterKind
{
    Unknown = 0,
    Standard = 1,
    Banana = 2,
    Potato = 3
}

internal static class VoicemeeterKindInfo
{
    public static VoicemeeterKind FromApiValue(int value)
    {
        return value switch
        {
            1 => VoicemeeterKind.Standard,
            2 => VoicemeeterKind.Banana,
            3 => VoicemeeterKind.Potato,
            _ => VoicemeeterKind.Unknown
        };
    }

    public static string DisplayName(VoicemeeterKind kind)
    {
        return kind switch
        {
            VoicemeeterKind.Standard => "Voicemeeter Standard",
            VoicemeeterKind.Banana => "Voicemeeter Banana",
            VoicemeeterKind.Potato => "Voicemeeter Potato",
            _ => "Voicemeeter not detected"
        };
    }
}

internal sealed record IoEndpoint(string Name, ChannelRange Range)
{
    public int ChannelCount => Range.End - Range.Start + 1;

    public string DisplayName => Range.Start == Range.End
        ? $"{Name} ({Range.Start + 1})"
        : $"{Name} ({Range.Start + 1}-{Range.End + 1})";
}

internal static class VoicemeeterIoLayout
{
    public static IReadOnlyList<IoEndpoint> GetEndpoints(CallbackMode mode, VoicemeeterKind kind)
    {
        return mode == CallbackMode.Input
            ? BuildInputEndpoints(kind)
            : BuildOutputEndpoints(kind);
    }

    private static IReadOnlyList<IoEndpoint> BuildInputEndpoints(VoicemeeterKind kind)
    {
        var spec = GetSpec(kind);
        var endpoints = new List<IoEndpoint>();
        var channel = 0;

        for (var hardware = 1; hardware <= spec.HardwareInputs; hardware++)
        {
            endpoints.Add(new IoEndpoint($"Hardware In {hardware}", new ChannelRange(channel, channel + 1)));
            channel += 2;
        }

        foreach (var virtualInput in spec.VirtualInputs)
        {
            endpoints.Add(new IoEndpoint(virtualInput, new ChannelRange(channel, channel + 7)));
            channel += 8;
        }

        return endpoints;
    }

    private static IReadOnlyList<IoEndpoint> BuildOutputEndpoints(VoicemeeterKind kind)
    {
        var spec = GetSpec(kind);
        var endpoints = new List<IoEndpoint>();
        var channel = 0;

        for (var hardware = 1; hardware <= spec.HardwareOutputs; hardware++)
        {
            endpoints.Add(new IoEndpoint($"A{hardware} 8ch", new ChannelRange(channel, channel + 7)));
            channel += 8;
        }

        for (var virtualBus = 1; virtualBus <= spec.VirtualOutputs; virtualBus++)
        {
            endpoints.Add(new IoEndpoint($"B{virtualBus} 8ch", new ChannelRange(channel, channel + 7)));
            channel += 8;
        }

        return endpoints;
    }

    private static VoicemeeterLayoutSpec GetSpec(VoicemeeterKind kind)
    {
        return kind switch
        {
            VoicemeeterKind.Standard => new VoicemeeterLayoutSpec(2, ["VAIO"], 2, 1),
            VoicemeeterKind.Banana => new VoicemeeterLayoutSpec(3, ["VAIO", "AUX"], 3, 2),
            _ => new VoicemeeterLayoutSpec(5, ["VAIO", "AUX", "VAIO3"], 5, 3)
        };
    }

    private sealed record VoicemeeterLayoutSpec(
        int HardwareInputs,
        IReadOnlyList<string> VirtualInputs,
        int HardwareOutputs,
        int VirtualOutputs);
}
