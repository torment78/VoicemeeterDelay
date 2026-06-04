using System.IO;
using System.Text.Json;

namespace VoicemeeterDelay;

internal sealed class MainWindowSettings
{
    public string? DllPath { get; set; }

    public bool ArmBothStreams { get; set; }

    public bool VbanControlEnabled { get; set; }

    public int VbanControlPort { get; set; } = 6981;

    public string VbanControlStreamName { get; set; } = "Command1";

    public bool VbanControlLocalOnly { get; set; } = true;

    public VoicemeeterKind LastProfileKind { get; set; } = VoicemeeterKind.Potato;

    public List<MainWindowProfileSettings> Profiles { get; set; } = [];

    // Legacy fields kept so older settings files can migrate into a profile.
    public CallbackMode SelectedMode { get; set; } = CallbackMode.None;

    public string? SelectedEndpointName { get; set; }

    public double InputPathLatencyMilliseconds { get; set; }

    public double OutputPathLatencyMilliseconds { get; set; }

    public List<EndpointDelaySettingsSnapshot> Endpoints { get; set; } = [];
}

internal sealed class MainWindowProfileSettings
{
    public VoicemeeterKind Kind { get; set; } = VoicemeeterKind.Potato;

    public CallbackMode SelectedMode { get; set; } = CallbackMode.None;

    public string? SelectedEndpointName { get; set; }

    public double InputPathLatencyMilliseconds { get; set; }

    public double OutputPathLatencyMilliseconds { get; set; }

    public List<EndpointDelaySettingsSnapshot> Endpoints { get; set; } = [];
}

internal sealed class EndpointDelaySettingsSnapshot
{
    public CallbackMode Mode { get; set; }

    public string EndpointName { get; set; } = string.Empty;

    public int ChannelCount { get; set; }

    public bool[] Enabled { get; set; } = [];

    public double[] DelayMilliseconds { get; set; } = [];

    public double[] VolumePercent { get; set; } = [];

    public bool[] RouteEnabled { get; set; } = [];

    public int[] RouteDestinationBusIndex { get; set; } = [];

    public int[] RouteDestinationChannelOffset { get; set; } = [];

    public bool[] RouteMuteNormal { get; set; } = [];

    public List<List<RouteDestinationSnapshot>> RouteDestinations { get; set; } = [];
}

internal sealed class RouteDestinationSnapshot
{
    public int BusIndex { get; set; }

    public int ChannelOffset { get; set; }
}

internal static class MainWindowSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static MainWindowSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new MainWindowSettings();
            }

            return JsonSerializer.Deserialize<MainWindowSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                ?? new MainWindowSettings();
        }
        catch
        {
            return new MainWindowSettings();
        }
    }

    public static void Save(MainWindowSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Settings persistence should never interrupt audio processing.
        }
    }

    private static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoicemeeterDelay");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "main-window.json");
}
