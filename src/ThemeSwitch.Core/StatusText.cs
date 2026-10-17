using System.Globalization;

namespace ThemeSwitch.Core;

public static class StatusText
{
    /// <summary>One line for the tray tooltip, e.g. "Dark (overcast) · Light at ~11:00".</summary>
    public static string Line(AutomationStatus status, TimeZoneInfo zone, DateTime nowUtc)
    {
        string theme = status.Current?.ToString() ?? "Unknown";
        if (status.Mode == ThemeMode.Light) return "Always Light";
        if (status.Mode == ThemeMode.Dark) return "Always Dark";
        if (status.Decision is not { } decision) return $"{theme} · waiting for location";
        if (status.OverrideUntilUtc is { } until) return $"{theme} (manual) until {Clock(until, zone, nowUtc)}";

        string line = $"{theme} ({decision.Reason})";
        if (status.NextChange is not { } next) return line;
        // Changes caused by a forecast hour are only as good as the forecast, so mark them "~".
        string approx = next.Exact ? "" : "~";
        return $"{line} · {next.To} at {approx}{Clock(next.AtUtc, zone, nowUtc)}";
    }

    /// <summary>"14:00" today, "Sat 08:17" on another day.</summary>
    public static string Clock(DateTime utc, TimeZoneInfo zone, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
        var today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date;
        string format = local.Date == today ? "HH:mm" : "ddd HH:mm";
        return local.ToString(format, CultureInfo.InvariantCulture);
    }
}
