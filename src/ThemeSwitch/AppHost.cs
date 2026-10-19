using System.Reflection;
using System.Text.Json;
using ThemeSwitch.Core;

namespace ThemeSwitch;

static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";

    public const string RepositoryUrl = "https://github.com/apictapx/themeswitch";
}

/// <summary>
/// Wires settings, theme store, data source and caches together for both the tray app and the
/// one-shot CLI commands, and owns the "refresh location and weather when stale" logic.
/// </summary>
sealed class AppHost : IDisposable
{
    static readonly TimeSpan LocationMaxAge = TimeSpan.FromHours(3);
    static readonly TimeSpan WeatherMaxAge = TimeSpan.FromMinutes(60);
    static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);

    readonly SettingsStore _settingsStore;
    readonly string _weatherCachePath;
    readonly IDataSource _source;
    DateTime? _lastFailureUtc;

    public CliOptions Options { get; }
    public string DataDir { get; }
    public AppSettings Settings { get; }
    public RegistryThemeStore Store { get; }
    public TimeZoneInfo Zone { get; private set; }
    public ThemeAutomation Automation { get; }
    public BrightnessAutomation Brightness { get; }
    public ILightMeter Meter { get; }
    public IBrightnessDisplay Display { get; }
    public WeatherForecast? Weather { get; private set; }
    public LocationChoice? LastLocationChoice { get; private set; }
    public Log Log { get; }

    public GeoPoint? Location => Settings.LastGoodLocation;

    public AppHost(CliOptions options, bool broadcastInBackground)
    {
        Options = options;
        DataDir = options.DataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ThemeSwitch");
        Directory.CreateDirectory(DataDir);
        Log = new Log(Path.Combine(DataDir, "themeswitch.log"));

        _settingsStore = new SettingsStore(Path.Combine(DataDir, "settings.json"));
        Settings = _settingsStore.Load();
        _weatherCachePath = Path.Combine(DataDir, "weather.json");

        Zone = options.TimeZoneId is { } id ? TimeZoneInfo.FindSystemTimeZoneById(id) : TimeZoneInfo.Local;
        Store = new RegistryThemeStore(options.ThemeKey ?? RegistryThemeStore.WindowsKey, Settings, broadcastInBackground);
        _source = options.FixturesDir is { } dir ? new FixtureDataSource(dir) : new HttpDataSource();
        Automation = new ThemeAutomation(Settings, Store);
        Brightness = new BrightnessAutomation(Settings);
        Meter = options.Light is { } level ? new FixedLightMeter(level) : new CameraLightMeter(Settings.BrightnessCamera);
        Display = options.MonitorFile is { } file ? new FileDisplay(file) : new DdcDisplays(Settings.BrightnessMonitor);
        Weather = LoadWeatherCache();
    }

    /// <summary>The fixed --now in tests, otherwise the real clock.</summary>
    public DateTime UtcNow() => Options.NowUtc ?? DateTime.UtcNow;

    public void Save() => _settingsStore.Save(Settings);

    /// <summary>
    /// Picks up a Windows time zone change (e.g. after travel). TimeZoneInfo.Local is cached for the
    /// process lifetime, so it has to be cleared explicitly. A new zone also means the location
    /// may be stale: re-check it now rather than reject the new place as a VPN.
    /// </summary>
    public bool RefreshTimeZone()
    {
        if (Options.TimeZoneId is not null) return false;
        TimeZoneInfo.ClearCachedData();
        var local = TimeZoneInfo.Local;
        if (local.Id == Zone.Id) return false;
        Log.Write($"time zone changed: {Zone.Id} -> {local.Id}");
        Zone = local;
        Settings.LocationCheckedUtc = null;
        _lastFailureUtc = null;
        return true;
    }

    public bool NeedsRefresh(DateTime nowUtc) =>
        (_lastFailureUtc is not { } failed || nowUtc - failed >= RetryAfterFailure)
        && (LocationStale(nowUtc) || Location is not { } loc || Weather?.IsFreshFor(loc, nowUtc, WeatherMaxAge) != true);

    /// <summary>Refreshes whatever is stale. Never throws: failures fall back to cached data.</summary>
    public async Task RefreshAsync(DateTime nowUtc, bool force, CancellationToken ct)
    {
        bool failed = false;
        try
        {
            failed = await RefreshCoreAsync(nowUtc, force, ct);
        }
        catch (Exception ex)
        {
            failed = true;
            Log.Write($"refresh failed: {ex}");
        }
        _lastFailureUtc = failed ? nowUtc : null;
        try
        {
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"saving settings failed: {ex.Message}");
        }
    }

    /// <returns>True when something could not be refreshed.</returns>
    async Task<bool> RefreshCoreAsync(DateTime nowUtc, bool force, CancellationToken ct)
    {
        bool failed = false;
        if (force || LocationStale(nowUtc))
        {
            var candidate = await LookupLocationAsync(ct);
            LastLocationChoice = LocationGuard.Choose(candidate, Settings.LastGoodLocation, Zone, nowUtc);
            if (LastLocationChoice.Accepted) Settings.LastGoodLocation = LastLocationChoice.Location;
            if (candidate is null) failed = true; else Settings.LocationCheckedUtc = nowUtc;
            Log.Write($"location: {Describe(candidate)} -> {LastLocationChoice.Note}");
        }

        if (Location is { } loc && (force || Weather?.IsFreshFor(loc, nowUtc, WeatherMaxAge) != true))
        {
            try
            {
                string raw = await _source.GetAsync(OpenMeteo.ForecastUrl(loc), ct);
                var forecast = OpenMeteo.Parse(raw, loc, nowUtc);
                Weather = forecast;
                File.WriteAllText(_weatherCachePath,
                    JsonSerializer.Serialize(new WeatherCacheEntry(loc, nowUtc, raw), SettingsStore.Json));
                Log.Write($"weather: {forecast.Hours.Count} hours for {Describe(loc)}");
            }
            // Anything can come back from the network (captive portals, API changes), and a full disk
            // or locked file can break the cache write. All of it means "no fresh weather this time".
            catch (Exception ex)
            {
                failed = true;
                Log.Write($"weather failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return failed;
    }

    /// <summary>Room light and the monitor's current brightness. Slow (camera, DDC/CI): runs on a worker thread.</summary>
    /// <exception cref="LightMeterException">The camera couldn't be used.</exception>
    public Task<(LightReading Reading, int? Current)> MeasureBrightnessAsync() =>
        Task.Run(() => (Meter.Measure(), Display.Read()));

    /// <summary>
    /// Measures, decides, and fades the monitors when the decision says so. The decision runs on the
    /// caller's thread (the tray's UI thread owns Settings); camera and monitor I/O run on a worker.
    /// Never throws for camera or monitor trouble: it's logged and the next run tries again.
    /// </summary>
    /// <param name="immediate">Adjust right away to the measured light, over a manual setting (tray click), instead of the scheduled step.</param>
    /// <param name="stillWanted">Asked after measuring; false (auto brightness switched off meanwhile) drops the result.</param>
    public async Task<BrightnessResult> AdjustBrightnessAsync(DateTime nowUtc, bool immediate = false, Func<bool>? stillWanted = null)
    {
        LightReading reading;
        int? current;
        try
        {
            (reading, current) = await MeasureBrightnessAsync();
        }
        catch (Exception ex) // camera or DDC trouble of any kind: skip this run, try again next time
        {
            string why = ex is LightMeterException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            Log.Write($"brightness: skipped, {why}");
            return new(null, null, $"skipped: {why}");
        }
        if (stillWanted?.Invoke() == false) return new(reading, null, "cancelled");

        int? lastTarget = Settings.BrightnessLastTarget;
        var status = immediate ? Brightness.AdjustNow(nowUtc, reading.Level, current) : Brightness.Tick(nowUtc, reading.Level, current);
        string note = Describe(reading, status);
        if (status.Write is { } target && current is { } from)
        {
            bool contrast = Settings.BrightnessIncludesContrast;
            var (ok, monitorNow) = await Task.Run(() => Fade(from, target, contrast));
            if (!ok)
            {
                Brightness.WriteFailed(monitorNow, lastTarget);
                note += ", write failed";
            }
        }
        if (status.Action != BrightnessAction.None || immediate) Log.Write($"brightness{(immediate ? " (now)" : "")}: {note}");
        return new(reading, status, note);
    }

    (bool Ok, int? MonitorNow) Fade(int from, int target, bool contrast)
    {
        try
        {
            if (BrightnessAutomation.FadeSteps(from, target, Settings.BrightnessStep).All(step => Display.Write(step, contrast))) return (true, target);
        }
        catch (Exception ex)
        {
            Log.Write($"brightness: write failed: {ex.GetType().Name}: {ex.Message}");
        }
        try
        {
            return (false, Display.Read());
        }
        catch (Exception)
        {
            return (false, null);
        }
    }

    static string Describe(LightReading r, BrightnessStatus s)
    {
        string light = r.Luma is { } luma
            ? FormattableString.Invariant($"light {s.Level:0.00} ({(Math.Abs(s.Level - r.Level) >= 0.005 ? $"smoothed, now {r.Level:0.00}, " : "")}luma {luma:0} at 2^{r.Exposure})")
            : FormattableString.Invariant($"light {s.Level:0.00}");
        string monitor = s.Action switch
        {
            BrightnessAction.Apply => $"{s.Current}% -> {s.Target}%",
            BrightnessAction.Learned => FormattableString.Invariant($"learned {s.Current}% (curve {s.Curve.Slope:0.0}%/step)"),
            BrightnessAction.MonitorUnavailable => "no monitor answers DDC/CI",
            _ => $"{s.Current}%, target {s.Target}%",
        };
        return $"{light} · {monitor}";
    }

    bool LocationStale(DateTime nowUtc) =>
        Settings.LocationCheckedUtc is not { } checkedAt || nowUtc - checkedAt >= LocationMaxAge || nowUtc < checkedAt;

    async Task<GeoPoint?> LookupLocationAsync(CancellationToken ct)
    {
        foreach (var (url, parse) in new (string, Func<string, GeoPoint?>)[]
                 { (IpGeo.IpWhoIsUrl, IpGeo.ParseIpWhoIs), (IpGeo.IpApiUrl, IpGeo.ParseIpApi) })
        {
            try
            {
                if (parse(await _source.GetAsync(url, ct)) is { } point) return point;
                Log.Write($"location: {url} gave no usable answer");
            }
            catch (Exception ex)
            {
                Log.Write($"location: {url} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return null;
    }

    WeatherForecast? LoadWeatherCache()
    {
        try
        {
            if (!File.Exists(_weatherCachePath)) return null;
            return JsonSerializer.Deserialize<WeatherCacheEntry>(File.ReadAllText(_weatherCachePath), SettingsStore.Json)
                ?.ToForecast();
        }
        catch (Exception ex) // a bad cache must never stop the app from starting
        {
            Log.Write($"weather cache unreadable, ignoring: {ex.Message}");
            return null;
        }
    }

    static string Describe(GeoPoint? p) =>
        p is null ? "none" : FormattableString.Invariant($"{p.Name ?? "?"} ({p.Latitude:0.###}, {p.Longitude:0.###}, {p.TimeZoneId ?? "no tz"})");

    public void Dispose() => (_source as IDisposable)?.Dispose();
}

/// <summary>Small rolling text log next to the settings, for "why did it switch?" questions.</summary>
sealed class Log(string path)
{
    const long MaxBytes = 256 * 1024;
    readonly Lock _gate = new();

    public string Path => path;

    public void Write(string message)
    {
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes) File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
        }
    }
}

sealed record BrightnessResult(LightReading? Reading, BrightnessStatus? Status, string Note);
