using System.Globalization;
using ThemeSwitch.Core;
using static ThemeSwitch.Core.Tests.TestData;

namespace ThemeSwitch.Core.Tests;

public class LocationTests
{
    static readonly DateTime Now = Utc(12);

    [Fact]
    public void ParsesIpWhoIs()
    {
        var p = IpGeo.ParseIpWhoIs("""
            {"ip":"192.0.2.1","success":true,"latitude":41.15,"longitude":-8.63,"city":"Porto","country":"Portugal",
             "timezone":{"id":"Europe/Lisbon","abbr":"WEST","utc":"+01:00"}}
            """);

        Assert.Equal(new GeoPoint(41.15, -8.63, "Porto, Portugal", "Europe/Lisbon"), p);
    }

    [Fact]
    public void ParsesIpApi()
    {
        var p = IpGeo.ParseIpApi("""{"ip":"192.0.2.1","city":"Lisbon","country_name":"Portugal","latitude":38.71,"longitude":-9.13,"timezone":"Europe/Lisbon"}""");

        Assert.Equal(new GeoPoint(38.71, -9.13, "Lisbon, Portugal", "Europe/Lisbon"), p);
    }

    [Theory]
    [InlineData("""{"success":false,"message":"Reserved range"}""")]
    [InlineData("""{"success":true,"city":"x"}""")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void IpWhoIsFailuresGiveNull(string json) => Assert.Null(IpGeo.ParseIpWhoIs(json));

    [Theory]
    [InlineData("""{"success":true,"latitude":38.6,"longitude":-9.1,"timezone":{"id":123}}""")]
    [InlineData("""{"success":true,"latitude":1e400,"longitude":-9.1}""")]
    [InlineData("""{"success":true,"latitude":91,"longitude":-9.1}""")]
    [InlineData("""{"success":true,"latitude":38.6,"longitude":-181}""")]
    public void IpWhoIsWrongShapesGiveNullInsteadOfThrowing(string json) => Assert.Null(IpGeo.ParseIpWhoIs(json));

    [Fact]
    public void ABadNameFieldIsDroppedButTheCoordinatesKept()
    {
        var p = IpGeo.ParseIpWhoIs("""{"success":true,"latitude":38.6,"longitude":-9.1,"city":42,"country":"Portugal"}""");

        Assert.Equal(new GeoPoint(38.6, -9.1, "Portugal"), p);
    }

    [Fact]
    public void IpApiRateLimitGivesNull() =>
        Assert.Null(IpGeo.ParseIpApi("""{"error":true,"reason":"RateLimited"}"""));

    [Fact]
    public void AcceptsALookupInThePcTimeZone()
    {
        var choice = LocationGuard.Choose(Lisbon, null, LisbonZone, Now);

        Assert.True(choice.Accepted);
        Assert.Equal(Lisbon, choice.Location);
    }

    [Fact]
    public void KeepsTheLastGoodLocationWhenTheLookupLooksLikeAVpn()
    {
        var tokyo = new GeoPoint(35.68, 139.69, "Tokyo, Japan", "Asia/Tokyo");

        var choice = LocationGuard.Choose(tokyo, Lisbon, LisbonZone, Now);

        Assert.False(choice.Accepted);
        Assert.Equal(Lisbon, choice.Location);
        Assert.Contains("VPN", choice.Note);
    }

    [Fact]
    public void UsesAMismatchedLookupWhenThereIsNothingBetter()
    {
        var tokyo = new GeoPoint(35.68, 139.69, "Tokyo, Japan", "Asia/Tokyo");

        Assert.True(LocationGuard.Choose(tokyo, null, LisbonZone, Now).Accepted);
    }

    [Fact]
    public void FailedLookupKeepsTheLastGoodLocation()
    {
        var choice = LocationGuard.Choose(null, Lisbon, LisbonZone, Now);

        Assert.False(choice.Accepted);
        Assert.Equal(Lisbon, choice.Location);
    }

    [Theory]
    [InlineData(-9.1, true)]   // Lisbon
    [InlineData(2.35, true)]   // Paris: solar time is ~1 h off civil time, still plausible
    [InlineData(139.7, false)] // Tokyo
    public void FallsBackToLongitudeWithoutAZoneId(double longitude, bool expected) =>
        Assert.Equal(expected, LocationGuard.MatchesTimeZone(new GeoPoint(40, longitude), LisbonZone, Now));

    [Fact]
    public void LongitudeFallbackWrapsAroundTheDateLine()
    {
        var tonga = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Tongatapu"); // UTC+13 at longitude -175

        Assert.True(LocationGuard.MatchesTimeZone(new GeoPoint(-21.1, -175.2), tonga, Now));
    }

    [Fact]
    public void DistanceLisbonToPortoIsAbout275Km() =>
        Assert.InRange(Geo.DistanceKm(Lisbon, new GeoPoint(41.1579, -8.6291)), 270, 280);
}

public class OpenMeteoTests
{
    [Fact]
    public void ParsesARealResponse()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "openmeteo-lisbon.json"));

        var forecast = OpenMeteo.Parse(json, Lisbon, Utc(12));

        Assert.Equal(72, forecast.Hours.Count); // yesterday + 2 forecast days
        Assert.All(forecast.Hours, h => Assert.Equal(DateTimeKind.Utc, h.StartUtc.Kind));
        Assert.All(forecast.Hours.Zip(forecast.Hours.Skip(1)), p => Assert.Equal(TimeSpan.FromHours(1), p.Second.StartUtc - p.First.StartUtc));
        Assert.Equal(0, forecast.Hours[0].StartUtc.Hour);
    }

    [Fact]
    public void SkipsHoursWithNullValues()
    {
        var forecast = OpenMeteo.Parse("""
            {"hourly":{"time":[1792195200,1792198800],"weather_code":[3,null],"cloud_cover":[90,50],"precipitation":[0,0]}}
            """, Lisbon, Utc(12));

        var hour = Assert.Single(forecast.Hours);
        Assert.Equal(new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc), hour.StartUtc);
    }

    [Fact]
    public void ErrorResponseThrows()
    {
        var ex = Assert.Throws<FormatException>(() => OpenMeteo.Parse("""{"error":true,"reason":"Latitude must be in range"}""", Lisbon, Utc(12)));
        Assert.Contains("Latitude", ex.Message);
    }

    [Fact]
    public void UrlUsesInvariantNumbersRegardlessOfCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            string url = OpenMeteo.ForecastUrl(Lisbon);
            Assert.Contains("latitude=38.7223&longitude=-9.1393", url);
            Assert.Contains("timeformat=unixtime", url);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ForecastIsFreshOnlyForTheSamePlaceAndRecentFetch()
    {
        var forecast = new WeatherForecast(Lisbon, Utc(12), []);
        var porto = new GeoPoint(41.1579, -8.6291);
        var hour = TimeSpan.FromHours(1);

        Assert.True(forecast.IsFreshFor(Lisbon, Utc(12, 30), hour));
        Assert.False(forecast.IsFreshFor(Lisbon, Utc(13), hour));
        Assert.False(forecast.IsFreshFor(porto, Utc(12, 30), hour));
        Assert.False(forecast.IsFreshFor(Lisbon, Utc(11), hour)); // clock went backwards
    }
}

public class SettingsStoreTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("themeswitch-tests-").FullName;
    string SettingsPath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var settings = new SettingsStore(SettingsPath).Load();

        Assert.Equal(ThemeMode.Auto, settings.Mode);
        Assert.Equal(30, settings.SunriseOffsetMinutes);
        Assert.Equal(-30, settings.SunsetOffsetMinutes);
        Assert.True(settings.SwitchSystemTheme);
    }

    [Fact]
    public void RoundTrips()
    {
        var original = new AppSettings
        {
            Mode = ThemeMode.Dark,
            LastGoodLocation = Lisbon,
            OverrideUntilUtc = Utc(17),
            LastApplied = ThemeChoice.Light,
        };
        new SettingsStore(SettingsPath).Save(original);

        var loaded = new SettingsStore(SettingsPath).Load();

        Assert.Equal(ThemeMode.Dark, loaded.Mode);
        Assert.Equal(Lisbon, loaded.LastGoodLocation);
        Assert.Equal(Utc(17), loaded.OverrideUntilUtc);
        Assert.Equal(DateTimeKind.Utc, loaded.OverrideUntilUtc!.Value.Kind);
        Assert.Equal(ThemeChoice.Light, loaded.LastApplied);
        Assert.Contains("\"mode\": \"Dark\"", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void CorruptFileIsSetAsideAndDefaultsUsed()
    {
        File.WriteAllText(SettingsPath, "{ not json");

        var settings = new SettingsStore(SettingsPath).Load();

        Assert.Equal(ThemeMode.Auto, settings.Mode);
        Assert.Equal("{ not json", File.ReadAllText(SettingsPath + ".bad"));
    }

    [Fact]
    public void UnchangedSettingsAreNotRewritten()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = store.Load();
        store.Save(settings);
        File.Delete(SettingsPath);

        store.Save(settings);

        Assert.False(File.Exists(SettingsPath));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
