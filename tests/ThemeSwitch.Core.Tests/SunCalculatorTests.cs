using ThemeSwitch.Core;

namespace ThemeSwitch.Core.Tests;

public class SunCalculatorTests
{
    // Reference times from the Open-Meteo archive API (minute precision), local clock time. For Lisbon in
    // winter the archive answered 08:51/18:18 - one hour off, as if still on summer time - so that row uses
    // the published civil times instead (Portugal is UTC+0 in December).
    [Theory]
    [InlineData(38.7223, -9.1393, "Europe/Lisbon", "2026-06-21", "06:12", "21:04")]
    [InlineData(38.7223, -9.1393, "Europe/Lisbon", "2025-12-21", "07:51", "17:18")]
    [InlineData(-33.8688, 151.2093, "Australia/Sydney", "2025-12-21", "05:40", "20:05")]
    [InlineData(40.7128, -74.0060, "America/New_York", "2026-03-20", "06:59", "19:08")]
    public void MatchesReferenceWithinTwoMinutes(double lat, double lon, string zoneId, string date, string rise, string set)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var day = DateOnly.Parse(date);

        var sun = SunCalculator.Compute(day, lat, lon);

        Assert.Equal(PolarState.None, sun.Polar);
        AssertNear(day.ToDateTime(TimeOnly.Parse(rise)), TimeZoneInfo.ConvertTimeFromUtc(sun.SunriseUtc!.Value, zone));
        AssertNear(day.ToDateTime(TimeOnly.Parse(set)), TimeZoneInfo.ConvertTimeFromUtc(sun.SunsetUtc!.Value, zone));
    }

    [Fact]
    public void TromsoHasPolarNightInDecember()
    {
        var sun = SunCalculator.Compute(new DateOnly(2025, 12, 21), 69.6496, 18.9560);

        Assert.Equal(PolarState.PolarNight, sun.Polar);
        Assert.Null(sun.SunriseUtc);
        Assert.Equal(TimeSpan.Zero, sun.DayLength);
    }

    [Fact]
    public void TromsoHasMidnightSunInJune()
    {
        var sun = SunCalculator.Compute(new DateOnly(2026, 6, 21), 69.6496, 18.9560);

        Assert.Equal(PolarState.MidnightSun, sun.Polar);
        Assert.Equal(TimeSpan.FromHours(24), sun.DayLength);
    }

    [Fact]
    public void DayLengthIsSunsetMinusSunrise()
    {
        var sun = SunCalculator.Compute(new DateOnly(2026, 10, 17), 38.7223, -9.1393);

        Assert.Equal(sun.SunsetUtc!.Value - sun.SunriseUtc!.Value, sun.DayLength);
        Assert.InRange(sun.DayLength.TotalHours, 11, 11.3);
    }

    static void AssertNear(DateTime expected, DateTime actual) =>
        Assert.True(Math.Abs((expected - actual).TotalMinutes) <= 2, $"expected ~{expected:HH:mm}, got {actual:HH:mm:ss}");
}
