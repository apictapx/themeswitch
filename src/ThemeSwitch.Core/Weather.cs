using System.Globalization;
using System.Text.Json;

namespace ThemeSwitch.Core;

/// <summary>One forecast hour, [StartUtc, StartUtc + 1h).</summary>
public sealed record HourWeather(DateTime StartUtc, int WeatherCode, double CloudCover, double Precipitation)
{
    public DateTime EndUtc => StartUtc.AddHours(1);
}

/// <param name="Location">Where the forecast was requested for (not Open-Meteo's grid-snapped point).</param>
public sealed record WeatherForecast(GeoPoint Location, DateTime FetchedUtc, IReadOnlyList<HourWeather> Hours)
{
    public HourWeather? At(DateTime utc) => Hours.FirstOrDefault(h => h.StartUtc <= utc && utc < h.EndUtc);

    public bool IsFreshFor(GeoPoint location, DateTime nowUtc, TimeSpan maxAge) =>
        nowUtc >= FetchedUtc
        && nowUtc - FetchedUtc < maxAge
        && Geo.DistanceKm(Location, location) < 25;
}

/// <summary>WMO weather interpretation codes, as used by Open-Meteo.</summary>
public static class WeatherCodes
{
    public static string Describe(int code) => code switch
    {
        0 => "clear",
        1 => "mainly clear",
        2 => "partly cloudy",
        3 => "overcast",
        45 or 48 => "fog",
        >= 51 and <= 57 => "drizzle",
        >= 61 and <= 67 => "rain",
        >= 71 and <= 77 => "snow",
        >= 80 and <= 82 => "rain showers",
        85 or 86 => "snow showers",
        >= 95 => "thunderstorm",
        _ => $"weather code {code}",
    };
}

/// <summary>Decides whether an hour is gloomy enough to keep the dark theme during daytime.</summary>
public static class GloomRule
{
    /// <returns>A short human-readable reason, or null when the hour is bright enough for light theme.</returns>
    public static string? Reason(HourWeather hour, int cloudCoverThreshold)
    {
        // Fog (45, 48) and every precipitation code (51+) count, whatever the cloud percentage says.
        if (hour.WeatherCode is 45 or 48 or >= 51) return WeatherCodes.Describe(hour.WeatherCode);
        if (hour.Precipitation > 0) return "rain";
        // The model can label an hour "overcast" at 78-80 % cover, so code 3 counts on its own.
        if (hour.WeatherCode == 3) return "overcast";
        if (hour.CloudCover >= cloudCoverThreshold) return $"cloudy {hour.CloudCover:0}%";
        return null;
    }
}

public static class OpenMeteo
{
    /// <summary>Hourly data in UTC (unix seconds), from yesterday so a gloom hold survives midnight.</summary>
    public static string ForecastUrl(GeoPoint location) =>
        "https://api.open-meteo.com/v1/forecast?"
        + string.Create(CultureInfo.InvariantCulture, $"latitude={location.Latitude:0.####}&longitude={location.Longitude:0.####}")
        + "&hourly=weather_code,cloud_cover,precipitation&timezone=GMT&timeformat=unixtime&past_days=1&forecast_days=2";

    public static WeatherForecast Parse(string json, GeoPoint requested, DateTime fetchedUtc)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
        {
            string reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            throw new FormatException($"Open-Meteo error: {reason}");
        }

        var hourly = root.GetProperty("hourly");
        var time = hourly.GetProperty("time");
        var code = hourly.GetProperty("weather_code");
        var cloud = hourly.GetProperty("cloud_cover");
        var precipitation = hourly.GetProperty("precipitation");

        var hours = new List<HourWeather>(time.GetArrayLength());
        for (int i = 0; i < time.GetArrayLength(); i++)
        {
            // Open-Meteo returns null for hours a model doesn't cover; skip rather than guess.
            if (code[i].ValueKind != JsonValueKind.Number
                || cloud[i].ValueKind != JsonValueKind.Number
                || precipitation[i].ValueKind != JsonValueKind.Number)
                continue;
            hours.Add(new HourWeather(
                DateTime.UnixEpoch.AddSeconds(time[i].GetInt64()),
                code[i].GetInt32(),
                cloud[i].GetDouble(),
                precipitation[i].GetDouble()));
        }
        return new WeatherForecast(requested, fetchedUtc, hours);
    }
}

/// <summary>On-disk weather cache: the raw response plus what it was fetched for.</summary>
public sealed record WeatherCacheEntry(GeoPoint Location, DateTime FetchedUtc, string Raw)
{
    public WeatherForecast ToForecast() => OpenMeteo.Parse(Raw, Location, FetchedUtc);
}
