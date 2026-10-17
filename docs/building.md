# Building from source

## Prerequisites

- Windows 10 1903+ or Windows 11 (the app uses the Windows registry and tray)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0): `winget install Microsoft.DotNet.SDK.10`
- Git

## On Windows

```powershell
git clone https://github.com/apictapx/themeswitch
cd themeswitch
./build.ps1                     # run all tests, then publish + zip into dist/
./build.ps1 -SkipTests -Install # quick build, copy to %LOCALAPPDATA%\ThemeSwitch and start it
```

`build.ps1` parameters:

| Parameter | Default | |
|---|---|---|
| `-Version` | `0.1.0-dev` | Stamped into the exe and the zip name |
| `-Configuration` | `Release` | |
| `-SkipTests` | off | Skip `dotnet test` |
| `-Install` | off | Stop the installed copy, replace it, start it again |

Output in `dist/` (git-ignored):

```text
dist/ThemeSwitch-v0.1.0-dev-win-x64/        ThemeSwitch.exe + README, LICENSE, CHANGELOG
dist/ThemeSwitch-v0.1.0-dev-win-x64.zip
dist/ThemeSwitch-v0.1.0-dev-win-x64.zip.sha256
```

## From WSL

The Windows SDK works on the WSL file system, so you can keep the clone in Linux:

```bash
./build.sh                       # same parameters as build.ps1
./build.sh -SkipTests -Install
"/mnt/c/Program Files/dotnet/dotnet.exe" test ThemeSwitch.slnx
```

To run a freshly built exe from a WSL shell, give it the execute bit first
(`chmod +x src/ThemeSwitch/bin/Debug/net10.0-windows/ThemeSwitch.exe`); Windows paths don't need it.

## Tests

```powershell
dotnet test ThemeSwitch.slnx
```

| Project | What it covers |
|---|---|
| `tests/ThemeSwitch.Core.Tests` | Sun math against reference tables, gloom rules, every decision boundary, override handling, location guard, parsers, settings file. Pure .NET: also runs on Linux/macOS. |
| `tests/ThemeSwitch.E2E` | Starts the real `ThemeSwitch.exe` as a process: report, apply, toggle, VPN guard, outages, tray startup and single-instance. Uses canned API responses, a temp data folder and a throwaway `HKCU\Software\ThemeSwitch.E2E\…` key, so **your real theme is never touched**. Windows only. |

One extra test talks to the real services. It's skipped unless you opt in:

```powershell
$env:THEMESWITCH_LIVE = "1"; dotnet test ThemeSwitch.slnx
```

## Trying changes safely

The testing switches work for manual experiments too:

```powershell
$exe = "src/ThemeSwitch/bin/Debug/net10.0-windows/ThemeSwitch.exe"
& $exe --report --data-dir $env:TEMP\ts --theme-key "Software\ThemeSwitch.Try\Personalize" --now 2026-12-21T12:00:00Z
```

`--theme-key` points the app at a scratch registry key instead of the real theme, `--data-dir` at a
scratch settings folder, `--now` and `--tz` pin the clock, and `--fixtures <dir>` replaces the network
with `ipwhois.json`, `ipapi.json` and `forecast.json` files from that folder.

## The icon

`src/ThemeSwitch/app.ico` is generated (no image editor needed):

```bash
python3 tools/make_icon.py
```

The tray's sun and moon are drawn at runtime in `TrayIcons.cs`.
