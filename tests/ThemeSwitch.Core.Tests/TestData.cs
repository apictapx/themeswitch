using ThemeSwitch.Core;

namespace ThemeSwitch.Core.Tests;

static class TestData
{
    public static readonly GeoPoint Lisbon = new(38.7223, -9.1393, "Lisbon, Portugal", "Europe/Lisbon");
    public static readonly TimeZoneInfo LisbonZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");

    /// <summary>2026-10-17 is summer time in Lisbon (UTC+1); light window is roughly 07:17Z-17:26Z.</summary>
    public static DateTime Utc(int hour, int minute = 0, int day = 17) => new(2026, 10, day, hour, minute, 0, DateTimeKind.Utc);

    /// <summary>48 clear hours from 2026-10-16T12:00Z, with the given UTC hours of 2026-10-17 overridden.</summary>
    public static WeatherForecast Forecast(params (int HourUtc, int Code, double Cloud, double Precipitation)[] overrides)
    {
        var start = Utc(12, day: 16);
        var hours = Enumerable.Range(0, 48).Select(i =>
        {
            var t = start.AddHours(i);
            var o = overrides.FirstOrDefault(x => t == Utc(x.HourUtc));
            return o == default ? new HourWeather(t, 0, 0, 0) : new HourWeather(t, o.Code, o.Cloud, o.Precipitation);
        }).ToList();
        return new WeatherForecast(Lisbon, start, hours);
    }

    public static DecisionEngine Engine(EngineOptions? options = null) => new(options ?? new EngineOptions());
}

sealed class FakeThemeStore(ThemeChoice? initial = null) : IThemeStore
{
    public ThemeChoice? Value { get; set; } = initial;
    public int Writes { get; private set; }

    public ThemeChoice? Read() => Value;

    public void Write(ThemeChoice theme)
    {
        Value = theme;
        Writes++;
    }
}
