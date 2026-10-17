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

    public EngineOptions ToEngineOptions() =>
        new(SunriseOffsetMinutes, SunsetOffsetMinutes, CloudCoverThreshold, GloomHoldMinutes);
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
