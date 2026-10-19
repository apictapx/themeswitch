# Changelog

All notable changes to ThemeSwitch are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). See [docs/releasing.md](docs/releasing.md).

## [Unreleased]

## [0.2.0] - 2026-10-19

### Added

- Auto brightness (opt-in, tray menu): every 5 minutes the webcam measures room light and external
  monitors' brightness and contrast follow it over DDC/CI, fading in 5 % steps, within 50–80 %.
- Learns from manual adjustments on the monitor: each becomes a point on the light → brightness curve,
  and is kept until the room light changes.
- Skips measuring while another app uses the camera, while the PC is locked, and for monitors without
  DDC/CI; camera exposure, gain and backlight compensation are restored after every measurement.
- Command line: `--light-report`, `--brightness-once`, `--brightness-now`, and `--light` /
  `--monitor-file` for testing.

### Changed

- Left-clicking the tray icon now sets monitor brightness for the room right now (also with auto
  brightness off, and over a value set by hand). The theme toggle is **Switch to Dark/Light** in the menu.

## [0.1.0] - 2026-10-17

First release.

### Added

- Tray app that switches Windows 11 light/dark mode (apps, taskbar and Start) automatically:
  light from 30 min after sunrise until 30 min before sunset.
- Stays dark during daytime while the current hour is overcast, ≥ 85 % cloudy, rainy, snowy or
  foggy (Open-Meteo hourly forecast), and for one hour afterwards to avoid flicker.
- Sunrise/sunset calculated locally, so switching works offline.
- Location from IP geolocation (ipwho.is, falling back to ipapi.co); lookups that disagree with the
  PC's time zone (e.g. VPN) are ignored in favour of the last good location.
- Left-click the tray icon to toggle; the manual choice holds until the next scheduled change.
  Changes made in Windows Settings are respected the same way.
- Tray menu: Auto / Always Light / Always Dark, Start with Windows (enabled on first run).
- Command line: `--report`, `--apply-once`, `--toggle`, `--version`, plus sandbox options for testing.
- Re-checks immediately after waking from sleep or a clock change, and notices time zone changes
  (e.g. after travel) without a restart.

[Unreleased]: https://github.com/apictapx/themeswitch/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/apictapx/themeswitch/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/apictapx/themeswitch/releases/tag/v0.1.0
