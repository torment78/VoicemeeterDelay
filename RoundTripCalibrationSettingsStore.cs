using System;
using System.IO;
using System.Text.Json;

namespace VoicemeeterDelay;

internal sealed class RoundTripCalibrationSettings
{
    public string? PingInputId { get; set; }

    public string? ReturnOutputId { get; set; }

    public int PingChannelIndex { get; set; }

    public int ReturnChannelIndex { get; set; }

    public double WaitSeconds { get; set; } = RoundTripMeasurementEngine.DefaultTimeoutSeconds;

    public int CalibrationPingCount { get; set; } = 10;

    public string? FullCalibrationStartInputId { get; set; }

    public string? FullCalibrationEndInputId { get; set; }

    public string? FullCalibrationReturnOutputId { get; set; }

    public int FullCalibrationReturnChannelIndex { get; set; }
}

internal static class RoundTripCalibrationSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static RoundTripCalibrationSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new RoundTripCalibrationSettings();
            }

            var settings = JsonSerializer.Deserialize<RoundTripCalibrationSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                ?? new RoundTripCalibrationSettings();
            return settings;
        }
        catch
        {
            return new RoundTripCalibrationSettings();
        }
    }

    public static void Save(RoundTripCalibrationSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Remembering calibration choices is helpful, but it should never block audio control.
        }
    }

    private static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoicemeeterDelay");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "round-trip-calibration.json");
}
