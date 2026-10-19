# ThemeSwitch

Switches Windows 11 between light and dark mode on its own, following the sun **and the weather** —
and, optionally, sets your monitor's brightness to match the room, using the webcam as a light meter.

macOS has had "Auto" appearance for years; on Windows it's three clicks deep in Settings and never
automatic. ThemeSwitch fixes that, and goes one step further: on a grey, rainy day it stays dark.

- ☀️ **Light** from 30 min after sunrise until 30 min before sunset…
- 🌧️ …unless the current hour is overcast, ≥ 85 % cloudy, rainy, snowy or foggy.
- 🌙 **Dark** the rest of the time.
- 🔁 **Switch to Dark/Light** in the tray menu flips the theme. Your choice holds until the next
  scheduled change, then automation takes over again — the same as macOS.
- 🔆 **Left-click** the tray icon to set monitor brightness for the room right now.
- 🔌 Works offline (sun times are calculated locally); weather is a bonus when the network is up.

## Install

1. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64) if
   you don't have it — or `winget install Microsoft.DotNet.DesktopRuntime.10`.
2. Download `ThemeSwitch-vX.Y.Z-win-x64.zip` from the [latest release](https://github.com/apictapx/themeswitch/releases/latest).
3. Unzip `ThemeSwitch.exe` somewhere permanent, e.g. `%LOCALAPPDATA%\ThemeSwitch\`, and run it.

On first run it adds itself to **Start with Windows** (untick it in the tray menu if you'd rather not).

### "Windows protected your PC"

The exe isn't code-signed (signing certificates cost money every year), so SmartScreen warns about
an unknown publisher. Click **More info → Run anyway**. To check you got the genuine file, compare its
checksum with the `.sha256` file published next to the zip:

```powershell
Get-FileHash .\ThemeSwitch-vX.Y.Z-win-x64.zip -Algorithm SHA256
```

Or build it yourself from source — see [docs/building.md](docs/building.md).

## Using it

| Action | What happens |
|---|---|
| Left-click the icon | Measure the room and set monitor brightness (and contrast) now — overrides a value you set by hand. Works with auto brightness off too. |
| Hover the icon | Status, e.g. `Dark (overcast) · Light at ~11:00`, plus `Brightness 63% · light 4.8` once measured |
| Right-click → **Switch to Dark / Light** | Flip the theme. Held until the next automatic change. |
| Right-click → **Auto** | Follow sun + weather (default). |
| Right-click → **Always Light / Always Dark** | Stop automating and keep that theme. **Switch to …** (or a change in Windows Settings) flips between the two. |
| Right-click → **Adjust brightness now** | Same as a left-click. |
| Right-click → **Auto brightness (webcam)** | Match monitor brightness and contrast to the room light (off by default — see below). |
| Right-click → **Start with Windows** | Autostart on/off. |

Changing the theme in Windows Settings is treated exactly like **Switch to …** in the menu.

### Auto brightness

Every 5 minutes ThemeSwitch takes one look through the webcam (the camera light blinks for about a
second), works out how bright the room is, and sets external monitors' brightness — and contrast, by
default — over DDC/CI, the control channel most desktop monitors have on the video cable. It's a
fade in steps of 5 % (values are always 50, 55, 60 …), and only when the room really changed.

- **It learns from you.** Change brightness on the monitor's own buttons and that becomes "at this
  light, I want this"; a couple of adjustments in different light (day, evening) set the whole curve.
  Your setting is kept until the room light changes.
- It skips a run while another app is using the camera (a video call), while the PC is locked, and for
  monitors that don't answer DDC/CI (TVs, most laptop panels).
- Measure without changing anything: `ThemeSwitch.exe --light-report`.

### Command line

The exe also has one-shot commands, handy for scripts and shortcuts:

```text
ThemeSwitch.exe --report       # JSON: location, sunrise/sunset, light window, today's hourly plan
ThemeSwitch.exe --apply-once   # apply the automatic decision once and exit
ThemeSwitch.exe --toggle       # flip the theme (e.g. bind to a keyboard shortcut)
ThemeSwitch.exe --light-report     # JSON: room light level, monitors, the brightness it would set
ThemeSwitch.exe --brightness-once  # one auto-brightness step: measure, then adjust (or learn your setting)
ThemeSwitch.exe --brightness-now   # like a left-click: brightness for the room right now
ThemeSwitch.exe --help         # everything, including testing options
```

### Settings

`%APPDATA%\ThemeSwitch\settings.json` — edit while the app is closed:

| Setting | Default | Meaning |
|---|---|---|
| `sunriseOffsetMinutes` | `30` | Light starts this long after sunrise (negative = before) |
| `sunsetOffsetMinutes` | `-30` | Light ends this long after sunset (negative = before) |
| `cloudCoverThreshold` | `85` | Cloud % that counts as gloomy |
| `gloomHoldMinutes` | `60` | Stay dark this long after a gloomy hour, to avoid flicker |
| `switchSystemTheme` | `true` | Also switch taskbar and Start, not only apps |
| `autoBrightness` | `false` | Webcam-driven monitor brightness (the tray menu toggles it) |
| `brightnessIntervalMinutes` | `5` | How often to measure |
| `brightnessMin` / `brightnessMax` | `50` / `80` | The range automation stays within (your own manual changes may go outside it) |
| `brightnessStep` | `5` | Brightness is always a multiple of this and fades one step at a time |
| `brightnessIncludesContrast` | `true` | Set contrast to the same value as brightness |
| `brightnessSlope` | `7.2` | Percent per doubling of room light, until your adjustments say otherwise |
| `brightnessMonitor` / `brightnessCamera` | `""` | Only monitors / the camera whose name contains this (empty = all / first) |
| `brightnessPoints` | `[]` | What it learned from your adjustments; delete to start over |

A log of every decision is written to `%APPDATA%\ThemeSwitch\themeswitch.log`.

## How it decides

Every 30 seconds, and immediately after waking from sleep or a clock/time-zone change:

1. **Where am I?** IP geolocation (ipwho.is, falling back to ipapi.co), refreshed every 3 hours.
   If the answer's time zone doesn't match your PC's clock (typical with a VPN), it is ignored and
   the last good location is kept.
2. **When is the sun up?** Calculated locally with the sunrise equation — within about a minute of
   official tables.
3. **Is it gloomy right now?** Hourly forecast from [Open-Meteo](https://open-meteo.com), refreshed
   hourly. After a gloomy hour, dark is held for another hour so a single bright hour between showers
   doesn't flash the screen.

Details, edge cases (polar night, DST, outages) and the code layout are in
[docs/architecture.md](docs/architecture.md).

## Privacy

No account, no telemetry, no analytics. The only network requests are:

| Service | Sees | Why |
|---|---|---|
| ipwho.is, ipapi.co | your public IP | rough location (city level) |
| api.open-meteo.com | your rounded coordinates | hourly weather |

Everything else stays in `%APPDATA%\ThemeSwitch\`.

With auto brightness on, webcam frames are read into memory, averaged to a single number and
discarded — nothing is saved, shown or sent. Windows lists ThemeSwitch under *Settings → Privacy &
security → Camera → Recent activity*.

## Contributing

Bug reports and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).
Releases and versioning are described in [docs/releasing.md](docs/releasing.md).

## License

[MIT](LICENSE) © 2026 apictapx. Weather data by [Open-Meteo.com](https://open-meteo.com) (CC BY 4.0).
