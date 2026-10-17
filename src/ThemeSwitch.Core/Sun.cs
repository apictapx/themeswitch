namespace ThemeSwitch.Core;

public enum PolarState { None, MidnightSun, PolarNight }

/// <summary>Sunrise and sunset for one calendar day, as UTC instants (null during polar day or night).</summary>
public sealed record SunTimes(DateOnly Date, DateTime? SunriseUtc, DateTime? SunsetUtc, PolarState Polar)
{
    public TimeSpan DayLength => Polar switch
    {
        PolarState.MidnightSun => TimeSpan.FromHours(24),
        PolarState.PolarNight => TimeSpan.Zero,
        _ => SunsetUtc!.Value - SunriseUtc!.Value,
    };
}

/// <summary>
/// The sunrise equation (https://en.wikipedia.org/wiki/Sunrise_equation). Within about a minute of
/// NOAA/Open-Meteo below the polar circles, which is far more precise than theme switching needs.
/// </summary>
public static class SunCalculator
{
    static readonly DateTime J2000 = new(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    const double Deg = Math.PI / 180;

    /// <param name="date">Calendar date at the observer's location.</param>
    /// <param name="latitude">Degrees, north positive.</param>
    /// <param name="longitude">Degrees, east positive.</param>
    public static SunTimes Compute(DateOnly date, double latitude, double longitude)
    {
        double day = (date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc) - J2000).TotalDays;
        double meanNoon = day - longitude / 360;
        double anomaly = Normalize(357.5291 + 0.98560028 * meanNoon);
        double center = 1.9148 * Sin(anomaly) + 0.02 * Sin(2 * anomaly) + 0.0003 * Sin(3 * anomaly);
        double eclipticLongitude = Normalize(anomaly + center + 180 + 102.9372);
        double transit = meanNoon + 0.0053 * Sin(anomaly) - 0.0069 * Sin(2 * eclipticLongitude);

        double sinDeclination = Sin(eclipticLongitude) * Sin(23.4397);
        double cosDeclination = Math.Sqrt(1 - sinDeclination * sinDeclination);
        // -0.833° accounts for refraction and the sun's radius: "sunrise" is the upper limb touching the horizon.
        double cosHourAngle = (Sin(-0.833) - Sin(latitude) * sinDeclination) / (Cos(latitude) * cosDeclination);

        if (cosHourAngle > 1) return new(date, null, null, PolarState.PolarNight);
        if (cosHourAngle < -1) return new(date, null, null, PolarState.MidnightSun);

        double halfDay = Math.Acos(cosHourAngle) / Deg / 360;
        return new(date, J2000.AddDays(transit - halfDay), J2000.AddDays(transit + halfDay), PolarState.None);
    }

    static double Sin(double degrees) => Math.Sin(degrees * Deg);
    static double Cos(double degrees) => Math.Cos(degrees * Deg);
    static double Normalize(double degrees) => ((degrees % 360) + 360) % 360;
}
