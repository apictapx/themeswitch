using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThemeSwitch.Core;

public enum ThemeMode { Auto, Light, Dark }

/// <summary>Everything persisted between runs. Plain mutable class so System.Text.Json can round-trip it.</summary>
public sealed class AppSettings
{
    public ThemeMode Mode { get; set; } = ThemeMode.Auto;
    public int SunriseOffsetMinutes { get; set; } = 30;
    public int SunsetOffsetMinutes { get; set; } = -30;
    public int CloudCoverThreshold { get; set; } = 85;
    public int GloomHoldMinutes { get; set; } = 60;
    /// <summary>Also switch the taskbar and Start (SystemUsesLightTheme), not just app windows.</summary>
    public bool SwitchSystemTheme { get; set; } = true;

    public GeoPoint? LastGoodLocation { get; set; }
    public DateTime? LocationCheckedUtc { get; set; }

    /// <summary>While set and in the future, a manual choice beats the automatic one.</summary>
    public DateTime? OverrideUntilUtc { get; set; }
    /// <summary>The theme this app last saw or set; a different value in the registry means someone else changed it.</summary>
    public ThemeChoice? LastApplied { get; set; }

    /// <summary>Autostart is turned on once on first run; after that the user's menu choice sticks.</summary>
    public bool AutostartConfigured { get; set; }

    /// <summary>Measure room light with the webcam and set monitor brightness (DDC/CI) to match. Opt-in.</summary>
    public bool AutoBrightness { get; set; }
    public int BrightnessIntervalMinutes { get; set; } = 5;
    public int BrightnessMin { get; set; } = 50;
    public int BrightnessMax { get; set; } = 80;
    /// <summary>Brightness is always a multiple of this (50, 55 … 80) and fades one step at a time.</summary>
    public int BrightnessStep { get; set; } = 5;
    /// <summary>Percent per light step (a doubling of room light) until the user's own adjustments say otherwise.</summary>
    public double BrightnessSlope { get; set; } = BrightnessCurve.DefaultSlope;
    /// <summary>Set contrast to the same value as brightness.</summary>
    public bool BrightnessIncludesContrast { get; set; } = true;
    /// <summary>Only monitors whose name contains this; empty = every monitor that answers DDC/CI.</summary>
    public string BrightnessMonitor { get; set; } = "";
    /// <summary>Camera whose name contains this; empty = the first one.</summary>
    public string BrightnessCamera { get; set; } = "";

    /// <summary>Manual adjustments, learned as "at this light level I want this much".</summary>
    public List<BrightnessPoint> BrightnessPoints { get; set; } = [];
    /// <summary>What the monitor should read if nobody touched it; anything else is a manual change.</summary>
    public int? BrightnessLastSet { get; set; }
    /// <summary>The curve's answer (on the step grid) at the last decision; brightness moves only when this moves.</summary>
    public int? BrightnessLastTarget { get; set; }

    public EngineOptions ToEngineOptions() =>
        new(SunriseOffsetMinutes, SunsetOffsetMinutes, CloudCoverThreshold, GloomHoldMinutes);

    public BrightnessCurve ToBrightnessCurve() => BrightnessCurve.Fit(BrightnessPoints, BrightnessSlope);
}

public sealed class SettingsStore(string path)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    string? _lastSaved;

    public string Path => path;

    /// <summary>Missing file gives defaults; a corrupt file is set aside as *.bad so it can be inspected.</summary>
    public AppSettings Load()
    {
        if (!File.Exists(path)) return new AppSettings();
        string text = File.ReadAllText(path);
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(text, Json) ?? new AppSettings();
            _lastSaved = JsonSerializer.Serialize(settings, Json);
            return settings;
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".bad", overwrite: true);
            return new AppSettings();
        }
    }

    /// <summary>Writes only when something changed, via a temp file so a crash can't leave half a file.</summary>
    public void Save(AppSettings settings)
    {
        string text = JsonSerializer.Serialize(settings, Json);
        if (text == _lastSaved) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        // Per-process temp name: the tray and a CLI command may save at the same moment.
        string temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
        _lastSaved = text;
    }
}
