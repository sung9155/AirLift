using System.Text.Json;

namespace AirLift;

public sealed class Settings
{
    public string? LastSpeakerName { get; set; }
    public string? CaptureDeviceId { get; set; }
    public double VolumeDb { get; set; } = -10;
    public bool AutoConnect { get; set; }
    /// <summary>Switch the Windows default output to the capture device while connected.</summary>
    public bool AutoSwitchDefault { get; set; } = true;
    /// <summary>"Ultra" (~150ms), "Low" (~250ms) or "Stable" (~450ms, larger jitter margin).</summary>
    public string LatencyMode { get; set; } = "Low";
    /// <summary>Automatically reconnect to the last speaker when the stream drops.</summary>
    public bool AutoReconnect { get; set; } = true;
    /// <summary>UI language: "ko" or "en".</summary>
    public string Language { get; set; } = "ko";
    /// <summary>Default device to restore on disconnect (persisted to survive crashes).</summary>
    public string? PreviousDefaultDeviceId { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AirLift", "settings.json");

    public static Settings Load()
    {
        try
        {
            // One-time migration from the pre-rename config location
            if (!File.Exists(FilePath))
            {
                var old = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AirOutput", "settings.json");
                if (File.Exists(old))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.Copy(old, FilePath);
                }
            }
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* corrupted settings -> defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }
}
