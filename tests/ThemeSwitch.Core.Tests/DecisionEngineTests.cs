using ThemeSwitch.Core;
using static ThemeSwitch.Core.Tests.TestData;

namespace ThemeSwitch.Core.Tests;

public class GloomRuleTests
{
    [Theory]
    [InlineData(0, 0, 0, null)]
    [InlineData(1, 40, 0, null)]
    [InlineData(2, 84, 0, null)]
    [InlineData(2, 85, 0, "cloudy 85%")]
    [InlineData(3, 78, 0, "overcast")] // what the forecast said for this morning in Lisbon
    [InlineData(45, 100, 0, "fog")]
    [InlineData(53, 100, 0.3, "drizzle")]
    [InlineData(63, 100, 2.1, "rain")]
    [InlineData(73, 100, 0.5, "snow")]
    [InlineData(95, 100, 4, "thunderstorm")]
    [InlineData(1, 30, 0.1, "rain")] // precipitation without a rain code still counts
    public void Classifies(int code, double cloud, double precipitation, string? expected)
    {
        var hour = new HourWeather(Utc(12), code, cloud, precipitation);

        Assert.Equal(expected, GloomRule.Reason(hour, cloudCoverThreshold: 85));
    }
}

public class DecisionEngineTests
{
    [Fact]
    public void DarkBeforeSunrise() =>
        AssertDecision(Utc(5), Forecast(), ThemeChoice.Dark, "before sunrise");

    [Fact]
    public void DarkAfterSunset() =>
        AssertDecision(Utc(20), Forecast(), ThemeChoice.Dark, "after sunset");

    [Fact]
    public void LightAtClearNoon() =>
        AssertDecision(Utc(12), Forecast(), ThemeChoice.Light, "clear");

    [Fact]
    public void ClearDaylightIsSunDrivenNotWeatherDriven() =>
        Assert.False(Engine().Decide(Utc(12), Lisbon, Forecast()).WeatherDriven);

    [Fact]
    public void GloomIsWeatherDriven() =>
        Assert.True(Engine().Decide(Utc(12), Lisbon, Forecast((12, 3, 90, 0))).WeatherDriven);

    [Fact]
    public void LightWithoutWeatherData()
    {
        var d = Engine().Decide(Utc(12), Lisbon, null);

        Assert.Equal(ThemeChoice.Light, d.Theme);
        Assert.False(d.WeatherDriven);
    }

    [Fact]
    public void DarkWhileOvercastDuringTheDay() =>
        AssertDecision(Utc(12, 30), Forecast((12, 3, 80, 0)), ThemeChoice.Dark, "overcast");

    [Fact]
    public void GloomIsHeldForAnHourAfterItEnds()
    {
        var forecast = Forecast((12, 61, 100, 1.2));

        AssertDecision(Utc(13, 30), forecast, ThemeChoice.Dark, "rain earlier, holding");
        AssertDecision(Utc(14), forecast, ThemeChoice.Light, "clear");
    }

    [Fact]
    public void ZeroHoldSwitchesBackAsSoonAsTheGloomyHourEnds()
    {
        var d = Engine(new EngineOptions(GloomHoldMinutes: 0)).Decide(Utc(13), Lisbon, Forecast((12, 61, 100, 1.2)));

        Assert.Equal(ThemeChoice.Light, d.Theme);
    }

    [Fact]
    public void OneClearHourBetweenRainyHoursDoesNotFlicker()
    {
        var forecast = Forecast((11, 61, 100, 1), (13, 61, 100, 1));

        Assert.Equal(ThemeChoice.Dark, Engine().Decide(Utc(12, 30), Lisbon, forecast).Theme);
    }

    [Fact]
    public void LightWindowUsesOffsets()
    {
        var engine = Engine();
        var date = new DateOnly(2026, 10, 17);
        var sun = SunCalculator.Compute(date, Lisbon.Latitude, Lisbon.Longitude);

        var (start, end) = engine.LightWindow(date, sun, Lisbon);

        Assert.Equal(sun.SunriseUtc!.Value.AddMinutes(30), start);
        Assert.Equal(sun.SunsetUtc!.Value.AddMinutes(-30), end);
        Assert.Equal(ThemeChoice.Dark, engine.Decide(start!.Value.AddSeconds(-1), Lisbon, Forecast()).Theme);
        Assert.Equal(ThemeChoice.Light, engine.Decide(start.Value, Lisbon, Forecast()).Theme);
        Assert.Equal(ThemeChoice.Light, engine.Decide(end!.Value.AddSeconds(-1), Lisbon, Forecast()).Theme);
        Assert.Equal(ThemeChoice.Dark, engine.Decide(end.Value, Lisbon, Forecast()).Theme);
    }

    [Fact]
    public void GloomBeforeTheLightWindowDoesNotMatterAtNight() =>
        AssertDecision(Utc(5, 30), Forecast((5, 61, 100, 3)), ThemeChoice.Dark, "before sunrise");

    [Fact]
    public void NextChangeOnAClearDayIsTheEndOfTheLightWindow()
    {
        var engine = Engine();
        var end = engine.Decide(Utc(12), Lisbon, Forecast()).LightEndUtc;

        Assert.Equal(end, engine.NextChangeUtc(Utc(12), Lisbon, Forecast()));
    }

    [Fact]
    public void NextChangeAtNightIsTomorrowsLightStart()
    {
        var engine = Engine();
        var tomorrow = engine.Decide(Utc(12, day: 18), Lisbon, null).LightStartUtc;

        Assert.Equal(tomorrow, engine.NextChangeUtc(Utc(22), Lisbon, null));
    }

    [Fact]
    public void NextChangeFollowsTheForecast()
    {
        var next = Engine().NextChange(Utc(12), Lisbon, Forecast((14, 3, 90, 0)));

        Assert.Equal(new ScheduledChange(Utc(14), ThemeChoice.Dark, Exact: false), next);
    }

    [Fact]
    public void SunDrivenChangesAreExact()
    {
        var next = Engine().NextChange(Utc(12), Lisbon, Forecast());

        Assert.NotNull(next);
        Assert.True(next.Exact);
        Assert.Equal(ThemeChoice.Dark, next.To);
    }

    [Fact]
    public void ClearingWeatherInTheMorningIsAnApproximateChange()
    {
        // Dark at night; the light window opens ~07:17Z but it's overcast until 10Z.
        var next = Engine().NextChange(Utc(3), Lisbon, Forecast((7, 3, 90, 0), (8, 3, 90, 0)));

        Assert.Equal(new ScheduledChange(Utc(10), ThemeChoice.Light, Exact: false), next);
    }

    [Fact]
    public void UsesTheDayAtTheLocationNotThePcsDay()
    {
        // 22:00Z on Oct 17 is still Oct 17 in Lisbon but 07:00 on Oct 18 in Tokyo, after sunrise.
        var tokyo = new GeoPoint(35.68, 139.69, "Tokyo", "Asia/Tokyo");
        var engine = new DecisionEngine(new EngineOptions(SunriseOffsetMinutes: 0));

        var d = engine.Decide(Utc(22), tokyo, null);

        Assert.Equal(ThemeChoice.Light, d.Theme);
        Assert.Equal(new DateOnly(2026, 10, 18), d.Sun.Date);
    }

    [Fact]
    public void PolarNightIsDarkAllDay()
    {
        var tromso = new GeoPoint(69.6496, 18.9560);

        var d = new DecisionEngine(new EngineOptions()).Decide(new DateTime(2025, 12, 21, 11, 0, 0, DateTimeKind.Utc), tromso, null);

        Assert.Equal(ThemeChoice.Dark, d.Theme);
        Assert.Equal("polar night", d.Reason);
    }

    [Fact]
    public void MidnightSunIsLightEvenAtNight()
    {
        var tromso = new GeoPoint(69.6496, 18.9560);

        var d = new DecisionEngine(new EngineOptions()).Decide(new DateTime(2026, 6, 21, 23, 30, 0, DateTimeKind.Utc), tromso, null);

        Assert.Equal(ThemeChoice.Light, d.Theme);
    }

    static void AssertDecision(DateTime nowUtc, WeatherForecast forecast, ThemeChoice theme, string reason)
    {
        var d = Engine().Decide(nowUtc, Lisbon, forecast);
        Assert.Equal(theme, d.Theme);
        Assert.Equal(reason, d.Reason);
    }
}
