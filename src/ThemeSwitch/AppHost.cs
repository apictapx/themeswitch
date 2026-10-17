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
