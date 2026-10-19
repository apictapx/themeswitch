namespace ThemeSwitch.Core;

/// <summary>Where the current Windows theme lives. The app uses the registry; tests use a fake.</summary>
public interface IThemeStore
{
    ThemeChoice? Read();
    void Write(ThemeChoice theme);
}

public sealed record AutomationStatus(
    ThemeMode Mode,
    ThemeChoice? Current,
    Decision? Decision,
    ScheduledChange? NextChange,
    DateTime? OverrideUntilUtc,
    bool Applied)
{
    public DateTime? NextChangeUtc => NextChange?.AtUtc;
}

/// <summary>
/// Applies decisions to the store and handles manual overrides: a choice made by hand (tray menu or
/// Windows Settings) holds until the automatic theme would next change, then automation resumes.
/// In a fixed mode (Always Light/Dark) the same manual change flips the mode instead.
/// </summary>
public sealed class ThemeAutomation(AppSettings settings, IThemeStore store)
{
    public AppSettings Settings => settings;

    public DecisionEngine Engine => new(settings.ToEngineOptions());

    public AutomationStatus Tick(DateTime nowUtc, GeoPoint? location, WeatherForecast? weather)
    {
        var current = store.Read();
        var engine = Engine;
        var decision = location is null ? null : engine.Decide(nowUtc, location, weather);
        bool changedOutside = current is { } seen && settings.LastApplied is { } last && seen != last;
        bool applied = false;

        if (settings.Mode == ThemeMode.Auto && decision is not null)
        {
            if (settings.OverrideUntilUtc is { } until && nowUtc >= until) settings.OverrideUntilUtc = null;

            if (changedOutside) BeginOverride(current!.Value, nowUtc, location!, weather, engine, decision);

            if (settings.OverrideUntilUtc is null && current != decision.Theme)
            {
                store.Write(decision.Theme);
                current = decision.Theme;
                applied = true;
            }
        }
        else if (settings.Mode != ThemeMode.Auto)
        {
            if (changedOutside)
            {
                settings.Mode = ModeFor(current!.Value);
            }
            else if (current != FixedTheme(settings.Mode))
            {
                // Startup, or the registry was changed while the app wasn't running: enforce the mode.
                current = FixedTheme(settings.Mode);
                store.Write(current.Value);
                applied = true;
            }
        }

        if (current is { } c) settings.LastApplied = c;
        return Status(nowUtc, location, weather, engine, decision, current, applied);
    }

    /// <summary>Switch to Dark/Light in the tray menu. In Auto it's a temporary override; in a fixed mode it flips the mode.</summary>
    public AutomationStatus Toggle(DateTime nowUtc, GeoPoint? location, WeatherForecast? weather)
    {
        var target = store.Read() == ThemeChoice.Dark ? ThemeChoice.Light : ThemeChoice.Dark;
        store.Write(target);
        settings.LastApplied = target;

        var engine = Engine;
        var decision = location is null ? null : engine.Decide(nowUtc, location, weather);
        if (settings.Mode != ThemeMode.Auto)
            settings.Mode = ModeFor(target);
        else if (decision is not null)
            BeginOverride(target, nowUtc, location!, weather, engine, decision);
        else
            settings.OverrideUntilUtc = null;

        return Status(nowUtc, location, weather, engine, decision, target, applied: true);
    }

    public AutomationStatus SetMode(ThemeMode mode, DateTime nowUtc, GeoPoint? location, WeatherForecast? weather)
    {
        settings.Mode = mode;
        settings.OverrideUntilUtc = null;
        // Forget what we saw, so the next Tick enforces the new mode instead of reading the
        // current theme as a manual change.
        settings.LastApplied = null;
        return Tick(nowUtc, location, weather);
    }

    static ThemeChoice FixedTheme(ThemeMode mode) => mode == ThemeMode.Light ? ThemeChoice.Light : ThemeChoice.Dark;
    static ThemeMode ModeFor(ThemeChoice theme) => theme == ThemeChoice.Light ? ThemeMode.Light : ThemeMode.Dark;

    void BeginOverride(ThemeChoice chosen, DateTime nowUtc, GeoPoint location, WeatherForecast? weather,
        DecisionEngine engine, Decision decision)
    {
        // Choosing what automation wants anyway is not an override; it just cancels any pending one.
        settings.OverrideUntilUtc = chosen == decision.Theme
            ? null
            : engine.NextChangeUtc(nowUtc, location, weather) ?? nowUtc.AddHours(24);
    }

    AutomationStatus Status(DateTime nowUtc, GeoPoint? location, WeatherForecast? weather, DecisionEngine engine,
        Decision? decision, ThemeChoice? current, bool applied)
    {
        var next = location is null ? null : engine.NextChange(nowUtc, location, weather);
        var overrideUntil = settings.Mode == ThemeMode.Auto ? settings.OverrideUntilUtc : null;
        return new(settings.Mode, current, decision, next, overrideUntil, applied);
    }
}
