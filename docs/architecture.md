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
  Brightness.cs          BrightnessCurve (fit), BrightnessAutomation (smoothing, grid, learning, adjust-now)
  Hardware.cs            profiles (CameraProfile, DisplayProfile), channel interfaces (ICameraSession,
                         IMonitorChannel), LightMeasurement + LightLevel, DisplayController

src/ThemeSwitch          net10.0-windows  WinForms tray app + CLI
  Program.cs             argument parsing, single-instance mutex, tray vs one-shot command
  AppHost.cs             wiring + "refresh location/weather when stale" + log
  RegistryThemeStore.cs  IThemeStore over HKCU\…\Themes\Personalize + WM_SETTINGCHANGE broadcast
  DataSources.cs         HTTP, or canned files for tests (--fixtures)
  TrayApp.cs             NotifyIcon, menu, 30 s timer, sleep/clock-change events
  Cli.cs                 --report / --apply-once / --toggle (JSON output)
  Autostart.cs           HKCU\…\Run entry
  TrayIcons.cs           sun/moon drawn at the tray's pixel size
  LightMeter.cs          MediaFoundationCamera (ICameraSession for any UVC webcam), CameraLightMeter; FixedLightMeter (tests)
  Displays.cs            Dxva2DdcChannel (IMonitorChannel over DDC/CI), DdcDisplays; FileDisplay (tests)
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

An override (the menu's Switch to Dark/Light, `--toggle`, or the above) lasts until `NextChangeUtc` — the next time the
*automatic* answer flips — after which automation simply resumes. Choosing the theme automation wants
anyway cancels any override.

In *Always Light/Dark* mode the chosen theme is enforced (also at startup, in case it was changed while
the app wasn't running). Switch to Dark/Light — or a change in Windows Settings — flips the mode instead.

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

## Hardware: channels and profiles

Hardware is split in two so another vendor is cheap to add:

- A **channel** is *how* the app talks to a kind of device — C# in the Windows project:
  `MediaFoundationCamera : ICameraSession` (any UVC webcam: exposure/gain/backlight, frames' luma, snapshot
  and restore of settings) and `Dxva2DdcChannel : IMonitorChannel` (read/write a VCP code). A device that
  speaks something else — a laptop panel (WMI brightness), a network TV — needs a new channel.
- A **profile** is *what's different* about one vendor's device within that channel — plain data in Core:
  `CameraProfile` (start/longest exposure, gain as a fraction of its range, black/white level, tone-curve
  gamma, luma window, frame region, settle frames) and `DisplayProfile` (VCP codes, gap between commands,
  verify and retry, contrast range). `Generic` holds the defaults; built-in profiles (Brio 500, Dell P2423D)
  are matched by device name. They're records with init properties so they can later come from JSON.
- The **algorithms** that use them are in Core and don't know the hardware: `LightMeasurement` (pin gain,
  exposure ladder, always restore) and `DisplayController` (percent ↔ monitor units, gap, verify, contrast
  mapping). `HardwareTests` run them against fakes that behave like awkward hardware — a camera with another
  range or no gain control, a monitor that drops commands sent too soon.

## Auto brightness

**Measuring.** A webcam in auto exposure makes every room look mid-grey, so `CameraLightMeter` opens the
camera through Media Foundation, takes `IAMCameraControl`/`IAMVideoProcAmp` from the same media source,
pins gain (64) and turns off backlight compensation, then steps a manual exposure from 2^-7 s until the
top half of the frame averages 60–200 luma (never longer than 2^-6 s: at 30 fps a longer exposure
stops adding light). `LightLevel.FromLuma` turns that into a log2 level: video-range black (16) is
subtracted, a gamma of 1.6 undoes the camera's tone curve, and the exposure is divided out. The level
is camera-specific; learning absorbs that. Exposure, gain and backlight are restored in a `finally`.
Exposure only sticks within one capture session, which is why this is all in-process rather than
`ffmpeg`/DirectShow. Windows records camera use per app in
`HKCU\…\CapabilityAccessManager\ConsentStore\webcam`; an entry with a start but no stop time means
another app has the camera, and the run is skipped. The first activation after the Frame Server service
has been idle can fail, so it is retried once after 2 s.

**Deciding.** `BrightnessCurve` is a line in (level, percent): with no learned points the default one
(level 3.2 → 50 %, 7.2 % per step); with one point the default slope through it; with points at least one
level apart a least-squares fit, slope kept within 2–30. `BrightnessAutomation.Tick` smooths the level
(half-way each run; a jump of 1.5+ levels — lights switched — is taken at once; a gap of three intervals
starts over), then:

1. If the monitor reads something other than `BrightnessLastSet` (±1), someone changed it: the value is
   learned as a point (replacing points within 0.5 level, eight at most) and nothing is written.
   Switching auto brightness on clears `BrightnessLastSet`, so the value at that moment is learned
   instead of jumping.
2. Otherwise the curve's value is snapped to the `BrightnessStep` grid (multiples of 5), and brightness
   moves only when that differs from the last decision (`BrightnessLastTarget`) *and* the unsnapped value
   is more than half a step + 1 % away from it — so a level hovering on a rounding boundary doesn't
   flip 60 ↔ 65 every run. Comparing with the last *target* rather than the monitor is
   what keeps a manual value — even one outside the range — until the room changes.

**Adjust now.** A left click on the tray icon (`--brightness-now`) calls `AdjustNow`: the raw level,
no smoothing, no hysteresis (still on the grid), and a value set by hand is overwritten rather than learned —
"match the room" is the point of clicking. It works whether auto brightness is on or off.

**Applying.** `DdcDisplays` writes VCP 0x10 (brightness) and 0x12 (contrast) to every monitor that
answers, 150 ms apart, reading each back and retrying: a Dell P2423D silently drops a command that
arrives too soon after the previous one. A fade writes every grid value on the way (50→80 is six writes, 80→70 two; from a hand-set 54 the
first write is 55). If a write isn't confirmed, the
monitor is read back and that value remembered, so the next run neither learns a half-finished fade as a
manual change nor gives up on the target. The tray and a one-shot `--brightness-once` keep separate copies
of the settings (as for the theme, the tray's state wins); a CLI write therefore looks like a manual change
to the tray, which learns the value the curve chose anyway. The screen's
own light barely moves the reading (≈0.17 levels between 20 % and 80 % in a dark room), so there's no
feedback loop to damp.

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
