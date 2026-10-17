# Architecture

ThemeSwitch is two projects: a pure decision library and a thin Windows shell around it.

```text
src/ThemeSwitch.Core     net10.0          no Windows APIs — all the rules, fully unit-tested
  Sun.cs                 SunCalculator: sunrise equation → SunTimes (UTC instants, polar states)
  Weather.cs             HourWeather, WeatherForecast, GloomRule, OpenMeteo URL + parser
  Location.cs            GeoPoint, IpGeo parsers, LocationGuard (VPN check)
  DecisionEngine.cs      time + place + forecast → Decision; NextChangeUtc
  ThemeAutomation.cs     applies decisions through IThemeStore; manual overrides
  Settings.cs            AppSettings + SettingsStore (settings.json)
  StatusText.cs          the tooltip line

src/ThemeSwitch          net10.0-windows  WinForms tray app + CLI
  Program.cs             argument parsing, single-instance mutex, tray vs one-shot command
  AppHost.cs             wiring + "refresh location/weather when stale" + log
  RegistryThemeStore.cs  IThemeStore over HKCU\…\Themes\Personalize + WM_SETTINGCHANGE broadcast
  DataSources.cs         HTTP, or canned files for tests (--fixtures)
  TrayApp.cs             NotifyIcon, menu, 30 s timer, sleep/clock-change events
  Cli.cs                 --report / --apply-once / --toggle (JSON output)
  Autostart.cs           HKCU\…\Run entry
  TrayIcons.cs           sun/moon drawn at the tray's pixel size
```

## The decision

`DecisionEngine.Decide(nowUtc, location, forecast)` is a pure function:

1. Take the **solar date at the location** (UTC shifted by longitude/15 hours) and compute that day's
   sunrise/sunset. Using the location's own day, not the PC's, keeps the answer right even when the PC
   clock and the location disagree (travel, VPN, `--tz`).
2. The **light window** is `[sunrise + SunriseOffset, sunset + SunsetOffset)` — by default
   `[sunrise + 30 min, sunset − 30 min)`. Outside it → **Dark** (`before sunrise` / `after sunset`).
   Polar night → no window. Midnight sun → the whole solar day. If offsets make the window empty → Dark.
3. Inside the window, look at forecast hours that **started at or before now and ended less than
   `GloomHoldMinutes` ago** (plus the current hour). If any is gloomy → **Dark**, with the current
   hour's reason, or `"<reason> earlier, holding"` for a held one.
4. Otherwise → **Light**.

`Decision.WeatherDriven` is true only when gloom turned daytime dark.

An hour is gloomy (`GloomRule`) if its WMO weather code is fog (45, 48) or any precipitation (51+),
precipitation is above 0 mm, the code is 3 ("overcast"), or cloud cover ≥ `CloudCoverThreshold`.
Code 3 counts on its own because Open-Meteo labels hours "overcast" at as little as ~78 % cover.

Because the engine is stateless, "when will this change next?" is answered by asking again at every
instant where the answer could change — hour boundaries, gloom-hold ends, window edges, solar
midnights — and returning the first that differs (`NextChange`, 48 h horizon). The result says whether
it's `Exact` (a sun edge) or forecast-driven; the tooltip marks the latter with `~`.

All instants are UTC `DateTime`s internally; local time appears only at the edges (date selection,
display, which uses the PC's time zone). The forecast is requested in UTC (`timezone=GMT&timeformat=unixtime`) so its hours line up
with UTC hour boundaries and DST never has to be reasoned about.

## Overrides

`ThemeAutomation` remembers the theme it last saw or set (`LastApplied`). Each tick:

1. Expire `OverrideUntilUtc` if it's in the past.
2. If the registry differs from `LastApplied`, someone changed it outside the app → start an override.
3. Unless an override is active, write the decided theme if it differs.

An override (tray click, `--toggle`, or the above) lasts until `NextChangeUtc` — the next time the
*automatic* answer flips — after which automation simply resumes. Choosing the theme automation wants
anyway cancels any override.

In *Always Light/Dark* mode the chosen theme is enforced (also at startup, in case it was changed while
the app wasn't running). A tray click — or a change in Windows Settings — flips the mode instead.

## Location

`IpGeo` parses ipwho.is (primary) and ipapi.co (fallback). `LocationGuard.Choose` accepts a lookup
whose IANA time zone has the same UTC offset as the PC right now; otherwise it keeps the last good
location (VPN exits usually sit in another zone). With no history yet it accepts the lookup anyway —
something is better than nothing. Without a zone id it falls back to "longitude/15 is within 3 h of
the PC's offset". Lookups are repeated every 3 hours.

## Refresh and failure handling

`AppHost.NeedsRefresh` is true when the location is older than 3 h or the forecast is older than 60 min
or for a place more than 25 km away. After a failure it waits 10 minutes before retrying. The tray
decides **first** with whatever it has (cached forecast, last location) and refreshes afterwards, so a
slow network never delays a switch. With no forecast the light window alone decides; with no location
at all nothing is changed and the tooltip says "waiting for location".

Every tick also re-reads the Windows time zone (`TimeZoneInfo.Local` is cached per process, so it is
cleared first). If it changed — typically after travel — the location is re-checked immediately rather
than the new place being rejected as a VPN against the old offset.

Every network or cache error (including well-formed JSON of the wrong shape) is caught, logged, and
treated as "no fresh data"; the tray's menu handlers log instead of crashing. The tray and one-shot CLI
commands share `settings.json`; each writes through its own temp file, and the tray's state wins.

## Applying a theme

Windows keeps the theme in `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`:
`AppsUseLightTheme` (apps) and `SystemUsesLightTheme` (taskbar, Start). The app writes both (the second
only if `switchSystemTheme`), then broadcasts `WM_SETTINGCHANGE("ImmersiveColorSet")` so classic Win32
apps repaint. The broadcast waits for every top-level window (~0.6 s), so the tray runs it on a
background thread. Any other key path (tests) is written but never broadcast.

## Files

| Path | |
|---|---|
| `%APPDATA%\ThemeSwitch\settings.json` | `AppSettings`, camelCase JSON, written atomically and only when changed |
| `%APPDATA%\ThemeSwitch\weather.json` | last raw Open-Meteo response + where/when it was fetched |
| `%APPDATA%\ThemeSwitch\themeswitch.log` | decisions and failures; rotated to `.old` at 256 KB |
| `HKCU\…\CurrentVersion\Run\ThemeSwitch` | autostart: `"…\ThemeSwitch.exe" --tray` |

## Reusing the core

`ThemeSwitch.Core` has no Windows dependencies. To drive something other than the Windows theme —
a smart bulb, an editor theme, a Linux desktop — implement `IThemeStore` and call
`ThemeAutomation.Tick` on a timer with a location and (optionally) a forecast.

```csharp
var automation = new ThemeAutomation(new AppSettings(), myStore);
var forecast = OpenMeteo.Parse(json, location, DateTime.UtcNow);
automation.Tick(DateTime.UtcNow, location, forecast);
```
