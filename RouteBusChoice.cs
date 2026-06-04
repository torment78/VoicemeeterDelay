namespace VoicemeeterDelay;

internal sealed record RouteBusChoice(int Index, string Name, int ChannelCount)
{
    public override string ToString()
    {
        return Name;
    }
}
