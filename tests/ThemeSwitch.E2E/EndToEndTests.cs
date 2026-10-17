using System.Diagnostics;

namespace ThemeSwitch.E2E;

/// <summary>
/// Drives the built exe exactly as a user or script would. Times are 2026-10-17 in Lisbon (UTC+1):
/// sunrise ~07:47, sunset ~18:56 local, so the light window is ~07:17Z-17:26Z.
/// </summary>
public class EndToEndTests : IDisposable
{
    static DateTime Utc(int hour, int minute = 0, int day = 17) => new(2026, 10, day, hour, minute, 0, DateTimeKind.Utc);

    readonly AppSandbox _box = new();

    public EndToEndTests()
    {
        _box.WriteLisbon();
        _box.WriteForecast();
    }

    [Fact]
    public void ReportShowsSunTimesAndAnHourlyPlan()
    {
        _box.WriteForecast((7, 3, 80, 0), (8, 3, 80, 0), (12, 61, 100, 1.5));

        var r = _box.Run("--report", Utc(11));

        Assert.Equal(0, r.ExitCode);
        var json = r.Json;
        Assert.Equal("Lisbon, Testland", (string?)json["location"]!["name"]);
        Assert.StartsWith("2026-10-17T07:4", (string?)json["sun"]!["sunrise"]);
        Assert.StartsWith("2026-10-17T18:5", (string?)json["sun"]!["sunset"]);
        Assert.StartsWith("2026-10-17T08:1", (string?)json["sun"]!["lightFrom"]);

        var plan = json["plan"]!.AsArray().ToDictionary(p => (string)p!["hour"]!, p => (string)p!["theme"]!);
        Assert.Equal(24, plan.Count);
        Assert.Equal("Dark", plan["06:00"]);  // night
        Assert.Equal("Dark", plan["09:00"]);  // overcast (07Z-09Z) plus the one-hour hold
        Assert.Equal("Dark", plan["10:00"]);  // still holding after the 08Z overcast hour
        Assert.Equal("Light", plan["11:00"]); // cleared up
        Assert.Equal("Dark", plan["13:00"]);  // rain at 12Z
        Assert.Equal("Light", plan["15:00"]); // clear again after the hold
        Assert.Equal("Dark", plan["19:00"]);  // after sunset - 30 min
    }

    [Fact]
    public void ApplyOnceAtNightSetsDarkAndBothRegistryValues()
    {
        _box.Theme = "Light";

        var r = _box.Run("--apply-once", Utc(21));

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("Dark", _box.Theme);
        Assert.Equal(0, _box.SystemTheme);
        Assert.Equal(true, (bool?)r.Json["applied"]);
        Assert.Equal("after sunset", (string?)r.Json["reason"]);
        Assert.Contains("Dark (after sunset) · Light at Sun 08:", r.Stdout); // UTF-8, not escaped
        Assert.Contains("+01:00", r.Stdout);
    }

    [Fact]
    public void ApplyOnceAtClearNoonSetsLight()
    {
        _box.Theme = "Dark";

        _box.Run("--apply-once", Utc(12));

        Assert.Equal("Light", _box.Theme);
        Assert.Equal(1, _box.SystemTheme);
    }

    [Fact]
    public void RainyDayStaysDarkThenHoldsThenClears()
    {
        _box.WriteForecast((12, 63, 100, 2.5));
        _box.Theme = "Dark";

        Assert.Equal("rain", (string?)_box.Run("--apply-once", Utc(12, 20)).Json["reason"]);
        Assert.Equal("Dark", _box.Theme);

        Assert.Equal("rain earlier, holding", (string?)_box.Run("--apply-once", Utc(13, 30)).Json["reason"]);
        Assert.Equal("Dark", _box.Theme);

        _box.Run("--apply-once", Utc(14, 5));
        Assert.Equal("Light", _box.Theme);
    }

    [Fact]
    public void ToggleOverrideHoldsUntilTheNextScheduledChange()
    {
        _box.Run("--apply-once", Utc(12));
        Assert.Equal("Light", _box.Theme);

        var toggled = _box.Run("--toggle", Utc(12, 5));
        Assert.Equal("Dark", _box.Theme);
        Assert.StartsWith("2026-10-17T18:2", (string?)toggled.Json["overrideUntil"]); // sunset - 30 min, local

        _box.Run("--apply-once", Utc(15));
        Assert.Equal("Dark", _box.Theme); // automation leaves the manual choice alone

        _box.Run("--apply-once", Utc(8, day: 18));
        Assert.Equal("Light", _box.Theme); // next morning automation is back in charge
    }

    [Fact]
    public void ChangingTheThemeOutsideTheAppIsRespected()
    {
        _box.Run("--apply-once", Utc(12));
        _box.Theme = "Dark"; // as if flipped in Windows Settings

        var r = _box.Run("--apply-once", Utc(12, 30));

        Assert.Equal("Dark", _box.Theme);
        Assert.NotNull((string?)r.Json["overrideUntil"]);
    }

    [Fact]
    public void VpnLocationIsIgnoredInFavourOfTheLastGoodOne()
    {
        _box.Run("--report", Utc(12));
        _box.WriteLocation(35.68, 139.69, "Tokyo", "Asia/Tokyo");

        var r = _box.Run("--report", Utc(12, 30), "--refresh");

        Assert.Equal("Lisbon, Testland", (string?)r.Json["location"]!["name"]);
        Assert.Contains("VPN", (string?)r.Json["location"]!["lastLookup"]);
    }

    [Fact]
    public void WeatherOutageFallsBackToSunTimes()
    {
        _box.RemoveFixture("forecast.json");
        _box.Theme = "Dark";

        var r = _box.Run("--apply-once", Utc(12));

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("Light", _box.Theme);
        Assert.Equal("daytime, no weather data", (string?)r.Json["reason"]);
        Assert.Contains("weather failed", _box.Log);
    }

    [Fact]
    public void UnexpectedForecastShapeIsTreatedAsAnOutage()
    {
        File.WriteAllText(Path.Combine(_box.FixturesDir, "forecast.json"), """{"hourly":[]}"""); // e.g. a captive portal
        _box.Theme = "Dark";

        var r = _box.Run("--apply-once", Utc(12));

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("Light", _box.Theme);
        Assert.Contains("weather failed: InvalidOperationException", _box.Log);
    }

    [Fact]
    public void MalformedFirstLookupFallsBackToTheSecondService()
    {
        File.WriteAllText(Path.Combine(_box.FixturesDir, "ipwhois.json"),
            """{"success":true,"latitude":38.6,"longitude":-9.1,"timezone":{"id":123}}""");
        File.WriteAllText(Path.Combine(_box.FixturesDir, "ipapi.json"),
            """{"latitude":38.71,"longitude":-9.13,"city":"Lisbon","country_name":"Portugal","timezone":"Europe/Lisbon"}""");

        var r = _box.Run("--report", Utc(12));

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("Lisbon, Portugal", (string?)r.Json["location"]!["name"]);
    }

    [Fact]
    public void MidnightSunReportsATwentyFourHourDay()
    {
        _box.WriteLocation(69.6496, 18.9560, "Tromso", "Europe/Oslo");

        var r = _box.Run("--report", new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal("MidnightSun", (string?)r.Json["sun"]!["polar"]);
        Assert.Equal("24:00", (string?)r.Json["sun"]!["dayLength"]);
        Assert.Equal("Light", (string?)r.Json["decision"]!["theme"]);
    }

    [Fact]
    public void LocationOutageWithNoHistoryChangesNothing()
    {
        _box.RemoveFixture("ipwhois.json");
        _box.Theme = "Light";

        var r = _box.Run("--apply-once", Utc(21));

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("Light", _box.Theme);
        Assert.Contains("waiting for location", (string?)r.Json["status"]);
    }

    [Fact]
    public void UnknownArgumentExitsWithUsage()
    {
        var r = AppSandbox.RunRaw(["--bogus"]);

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("unknown argument: --bogus", r.Stderr);
        Assert.Contains("Usage:", r.Stderr);
    }

    [Fact]
    public void VersionPrints()
    {
        var r = AppSandbox.RunRaw(["--version"]);

        Assert.Equal(0, r.ExitCode);
        Assert.Matches(@"^\d+\.\d+\.\d+", r.Stdout.Trim());
    }

    [Fact]
    public void TrayStartsAppliesTheThemeAndIsSingleInstance()
    {
        using var tray = _box.StartTray();
        try
        {
            // The tray uses the real clock, so only check that it picked a theme, whichever it is.
            var deadline = Stopwatch.StartNew();
            while (_box.Theme is null && deadline.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(200);

            Assert.False(tray.HasExited, "tray exited early: " + _box.Log);
            Assert.NotNull(_box.Theme);
            Assert.Contains("tray started", _box.Log);

            using var second = _box.StartTray();
            Assert.True(second.WaitForExit(TimeSpan.FromSeconds(15)), "second instance should exit at once");
            Assert.False(tray.HasExited);
        }
        finally
        {
            if (!tray.HasExited) tray.Kill();
            tray.WaitForExit();
        }
    }

    public void Dispose() => _box.Dispose();
}

/// <summary>Real network. Opt in with THEMESWITCH_LIVE=1 (CI runners' IPs get rate-limited).</summary>
public class LiveTests
{
    [Fact]
    public void ReportAgainstTheRealServices()
    {
        if (Environment.GetEnvironmentVariable("THEMESWITCH_LIVE") != "1") return;
        using var box = new AppSandbox();

        var r = AppSandbox.RunRaw(["--report", "--data-dir", box.DataDir, "--theme-key", box.ThemeKey, "--no-autostart"]);

        Assert.Equal(0, r.ExitCode);
        Assert.NotNull(r.Json["location"]);
        Assert.NotNull((string?)r.Json["weatherFetched"]);
        Assert.Equal(24, r.Json["plan"]!.AsArray().Count);
    }
}
