namespace ThemeSwitch.Core;

public enum ThemeChoice { Light, Dark }

/// <param name="SunriseOffsetMinutes">Light starts this long after sunrise (negative = before).</param>
/// <param name="SunsetOffsetMinutes">Light ends this long after sunset (negative = before).</param>
/// <param name="GloomHoldMinutes">After a gloomy hour ends, stay dark this much longer to avoid flicker.</param>
public sealed record EngineOptions(
    int SunriseOffsetMinutes = 30,
    int SunsetOffsetMinutes = -30,
    int CloudCoverThreshold = 85,
    int GloomHoldMinutes = 60);

/// <param name="WeatherDriven">True when gloomy weather turned daytime dark; false when the sun decided.</param>
public sealed record Decision(
    ThemeChoice Theme,
    string Reason,
    bool WeatherDriven,
    SunTimes Sun,
    DateTime? LightStartUtc,
    DateTime? LightEndUtc);

/// <param name="Exact">True for sun-driven changes (computed); false when a forecast hour causes it.</param>
public sealed record ScheduledChange(DateTime AtUtc, ThemeChoice To, bool Exact);

/// <summary>
/// Stateless: the theme at any instant is a pure function of time, place and forecast, which makes
/// the rules testable and lets <see cref="NextChange"/> look ahead by simply asking again.
/// Days are solar days at the location (by longitude), so the answer doesn't depend on the PC's
/// time zone even when it disagrees with where the PC is.
/// </summary>
public sealed class DecisionEngine(EngineOptions options)
{
    public EngineOptions Options => options;

    public Decision Decide(DateTime nowUtc, GeoPoint location, WeatherForecast? weather)
    {
        var date = SolarDate(nowUtc, location);
        var sun = SunCalculator.Compute(date, location.Latitude, location.Longitude);
        var (start, end) = LightWindow(date, sun, location);

        if (start is null || end is null)
        {
            string reason = sun.Polar == PolarState.PolarNight ? "polar night" : "too little daylight";
            return new(ThemeChoice.Dark, reason, false, sun, null, null);
        }
        if (nowUtc < start) return new(ThemeChoice.Dark, "before sunrise", false, sun, start, end);
        if (nowUtc >= end) return new(ThemeChoice.Dark, "after sunset", false, sun, start, end);

        if (GloomAt(nowUtc, weather) is { } gloom) return new(ThemeChoice.Dark, gloom, true, sun, start, end);

        var hour = weather?.At(nowUtc);
        string light = hour is null ? "daytime, no weather data" : WeatherCodes.Describe(hour.WeatherCode);
        return new(ThemeChoice.Light, light, false, sun, start, end);
    }

    /// <summary>The first instant within <paramref name="horizon"/> (default 48 h) where the theme flips.</summary>
    public ScheduledChange? NextChange(DateTime nowUtc, GeoPoint location, WeatherForecast? weather, TimeSpan? horizon = null)
    {
        var limit = nowUtc + (horizon ?? TimeSpan.FromHours(48));
        var current = Decide(nowUtc, location, weather).Theme;
        var exact = new HashSet<DateTime>();
        foreach (var t in Candidates(nowUtc, limit, location, weather, exact))
        {
            var theme = Decide(t, location, weather).Theme;
            if (theme != current) return new(t, theme, exact.Contains(t));
        }
        return null;
    }

    public DateTime? NextChangeUtc(DateTime nowUtc, GeoPoint location, WeatherForecast? weather, TimeSpan? horizon = null) =>
        NextChange(nowUtc, location, weather, horizon)?.AtUtc;

    public (DateTime? StartUtc, DateTime? EndUtc) LightWindow(DateOnly solarDate, SunTimes sun, GeoPoint location)
    {
        if (sun.Polar == PolarState.MidnightSun)
            return (SolarMidnightUtc(solarDate, location), SolarMidnightUtc(solarDate.AddDays(1), location));
        if (sun.Polar == PolarState.PolarNight) return (null, null);
        var start = sun.SunriseUtc!.Value.AddMinutes(options.SunriseOffsetMinutes);
        var end = sun.SunsetUtc!.Value.AddMinutes(options.SunsetOffsetMinutes);
        return start < end ? (start, end) : (null, null);
    }

    /// <summary>The calendar date in local mean solar time — what "today" means for the sun there.</summary>
    public static DateOnly SolarDate(DateTime utc, GeoPoint location) =>
        DateOnly.FromDateTime(utc.AddHours(location.Longitude / 15));

    static DateTime SolarMidnightUtc(DateOnly date, GeoPoint location) =>
        date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-location.Longitude / 15);

    string? GloomAt(DateTime nowUtc, WeatherForecast? weather)
    {
        if (weather is null) return null;
        var hold = TimeSpan.FromMinutes(options.GloomHoldMinutes);
        string? held = null;
        foreach (var hour in weather.Hours)
        {
            if (hour.StartUtc > nowUtc || nowUtc >= hour.EndUtc + hold) continue;
            if (GloomRule.Reason(hour, options.CloudCoverThreshold) is not { } reason) continue;
            if (nowUtc < hour.EndUtc) return reason;
            held = $"{reason} earlier, holding";
        }
        return held;
    }

    // Every instant where the answer can change: hour boundaries (new forecast hour), the end of
    // each gloom hold, light-window edges and solar midnights (a new day means new sun times).
    // Sun-driven instants are also collected into `exact`.
    IEnumerable<DateTime> Candidates(DateTime nowUtc, DateTime limit, GeoPoint location, WeatherForecast? weather,
        HashSet<DateTime> exact)
    {
        var set = new SortedSet<DateTime>();
        var hour = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
        for (; hour <= limit; hour = hour.AddHours(1)) set.Add(hour);

        if (weather is not null)
        {
            var hold = TimeSpan.FromMinutes(options.GloomHoldMinutes);
            foreach (var h in weather.Hours) set.Add(h.EndUtc + hold);
        }

        var today = SolarDate(nowUtc, location);
        int days = (int)Math.Ceiling((limit - nowUtc).TotalDays) + 1;
        for (int d = -1; d <= days; d++)
        {
            var date = today.AddDays(d);
            var (start, end) = LightWindow(date, SunCalculator.Compute(date, location.Latitude, location.Longitude), location);
            foreach (var t in new[] { start, end, SolarMidnightUtc(date, location) })
            {
                if (t is not { } edge) continue;
                set.Add(edge);
                exact.Add(edge);
            }
        }
        return set.Where(t => t > nowUtc && t <= limit);
    }
}
