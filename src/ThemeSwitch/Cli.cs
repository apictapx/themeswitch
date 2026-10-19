using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using ThemeSwitch.Core;

namespace ThemeSwitch;

/// <summary>The one-shot commands. Output is JSON so scripts and the end-to-end tests can read it.</summary>
static class Cli
{
    public static async Task<int> RunAsync(CliOptions options)
    {
        using var host = new AppHost(options, broadcastInBackground: false);
        var now = host.UtcNow();
        if (options.Command is Command.LightReport or Command.BrightnessOnce or Command.BrightnessNow) return await BrightnessAsync(host, options.Command, now);
        if (options.ForceRefresh || host.NeedsRefresh(now))
            await host.RefreshAsync(now, options.ForceRefresh, CancellationToken.None);

        object output = options.Command switch
        {
            Command.Report => Report(host, now),
            Command.ApplyOnce => StatusJson(host, host.Automation.Tick(now, host.Location, host.Weather), now),
            Command.Toggle => StatusJson(host, host.Automation.Toggle(now, host.Location, host.Weather), now),
            _ => throw new InvalidOperationException(options.Command.ToString()),
        };
        host.Save();
        Console.Out.WriteLine(JsonSerializer.Serialize(output, OutputJson));
        return 0;
    }

    /// <summary>--light-report changes nothing; --brightness-once runs one automatic step; --brightness-now is the tray click.</summary>
    static async Task<int> BrightnessAsync(AppHost host, Command command, DateTime now)
    {
        object output;
        int exit = 0;
        if (command is Command.BrightnessOnce or Command.BrightnessNow)
        {
            var r = await host.AdjustBrightnessAsync(now, immediate: command == Command.BrightnessNow);
            host.Save();
            exit = r.Status is null ? 1 : 0;
            output = new
            {
                action = r.Status?.Action,
                lightLevel = r.Status?.Level,
                from = r.Status?.Current,
                to = r.Status?.Target,
                note = r.Note,
            };
        }
        else
        {
            try
            {
                var (reading, current) = await host.MeasureBrightnessAsync();
                var curve = host.Settings.ToBrightnessCurve();
                output = new
                {
                    camera = reading.Source,
                    lightLevel = Math.Round(reading.Level, 2),
                    luma = reading.Luma is { } l ? Math.Round(l, 1) : (double?)null,
                    exposure = reading.Exposure,
                    monitors = host.Display.Names(),
                    brightness = current,
                    target = host.Brightness.Target(reading.Level),
                    autoBrightness = host.Settings.AutoBrightness,
                    includesContrast = host.Settings.BrightnessIncludesContrast,
                    range = new[] { host.Settings.BrightnessMin, host.Settings.BrightnessMax },
                    curve = new { slope = Math.Round(curve.Slope, 2), intercept = Math.Round(curve.Intercept, 1), points = host.Settings.BrightnessPoints },
                };
            }
            catch (Exception ex) // camera or monitor trouble of any kind: report it, don't crash
            {
                exit = 1;
                output = new { error = ex is LightMeterException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}", monitors = host.Display.Names(), brightness = host.Display.Read() };
            }
        }
        Console.Out.WriteLine(JsonSerializer.Serialize(output, OutputJson));
        return exit;
    }

    // Plain "+01:00" and "·" for humans reading the output; it's never embedded in HTML.
    static readonly JsonSerializerOptions OutputJson = new(SettingsStore.Json)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static object StatusJson(AppHost host, AutomationStatus s, DateTime now) => new
    {
        mode = s.Mode,
        theme = s.Current,
        applied = s.Applied,
        reason = s.Decision?.Reason,
        nextChange = Local(host, s.NextChangeUtc),
        overrideUntil = Local(host, s.OverrideUntilUtc),
        status = StatusText.Line(s, host.Zone, now),
    };

    static object Report(AppHost host, DateTime now)
    {
        var loc = host.Location;
        var engine = host.Automation.Engine;
        object? sun = null, plan = null, decision = null;
        if (loc is not null)
        {
            var d = engine.Decide(now, loc, host.Weather);
            var next = engine.NextChange(now, loc, host.Weather);
            decision = new
            {
                theme = d.Theme,
                d.Reason,
                d.WeatherDriven,
                nextChange = Local(host, next?.AtUtc),
                nextChangeExact = next?.Exact,
            };
            sun = new
            {
                date = d.Sun.Date,
                sunrise = Local(host, d.Sun.SunriseUtc),
                sunset = Local(host, d.Sun.SunsetUtc),
                dayLength = $"{(int)d.Sun.DayLength.TotalHours:00}:{d.Sun.DayLength.Minutes:00}",
                polar = d.Sun.Polar,
                lightFrom = Local(host, d.LightStartUtc),
                lightUntil = Local(host, d.LightEndUtc),
            };
            plan = HourlyPlan(host, engine, loc, now);
        }

        return new
        {
            version = AppInfo.Version,
            now = Local(host, now),
            timeZone = host.Zone.Id,
            mode = host.Settings.Mode,
            currentTheme = host.Store.Read(),
            location = loc is null ? null : new
            {
                loc.Name,
                loc.Latitude,
                loc.Longitude,
                loc.TimeZoneId,
                lastLookup = host.LastLocationChoice?.Note,
            },
            weatherFetched = Local(host, host.Weather?.FetchedUtc),
            decision,
            sun,
            plan,
        };
    }

    /// <summary>What the engine would pick at the top of every local hour today.</summary>
    static object[] HourlyPlan(AppHost host, DecisionEngine engine, GeoPoint loc, DateTime now)
    {
        var localMidnight = TimeZoneInfo.ConvertTimeFromUtc(now, host.Zone).Date;
        var rows = new List<object>();
        for (int h = 0; h < 24; h++)
        {
            var local = localMidnight.AddHours(h);
            if (host.Zone.IsInvalidTime(local)) continue;
            var utc = TimeZoneInfo.ConvertTimeToUtc(local, host.Zone);
            var d = engine.Decide(utc, loc, host.Weather);
            var w = host.Weather?.At(utc);
            rows.Add(new
            {
                hour = local.ToString("HH:mm", CultureInfo.InvariantCulture),
                theme = d.Theme,
                d.Reason,
                weather = w is null ? null : WeatherCodes.Describe(w.WeatherCode),
                cloud = w?.CloudCover,
                precipitation = w?.Precipitation,
            });
        }
        return [.. rows];
    }

    static string? Local(AppHost host, DateTime? utc) => utc is { } u
        ? TimeZoneInfo.ConvertTime(new DateTimeOffset(u, TimeSpan.Zero), host.Zone)
            .ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)
        : null;
}

/// <summary>
/// A WinExe has no console. When started from a terminal, borrow the parent's so --report prints;
/// when stdout is redirected (scripts, tests), the inherited handle already works.
/// </summary>
static class ConsoleAttach
{
    public static void EnsureOutput()
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) AttachConsole(AttachParentProcess);
    }

    const int StdOutputHandle = -11;
    const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);
}
