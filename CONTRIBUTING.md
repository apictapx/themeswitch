# Contributing

Thanks for helping! Bug reports, ideas and pull requests are all welcome.

## Reporting a bug

Open an [issue](https://github.com/apictapx/themeswitch/issues/new/choose) using the bug template.
The most useful things to attach:

- the output of `ThemeSwitch.exe --report` (it contains your approximate location — trim it if you like),
- the last lines of `%APPDATA%\ThemeSwitch\themeswitch.log`,
- what you expected the theme to be, and when.

## Making a change

1. Fork, then branch from `main`.
2. Build and test — see [docs/building.md](docs/building.md). `dotnet test ThemeSwitch.slnx` must pass;
   the end-to-end tests never touch your real theme.
3. Keep rules in `ThemeSwitch.Core` (pure, testable) and Windows plumbing in `ThemeSwitch`.
   [docs/architecture.md](docs/architecture.md) explains the split.
4. Add or update tests for behaviour changes. Decision changes belong in `DecisionEngineTests` or
   `ThemeAutomationTests`; anything a user would see end-to-end also gets an `EndToEndTests` case.
5. Add a line under `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md).
6. Open a pull request. CI runs the full test suite on Windows.

## Style

- Follow `.editorconfig` (`dotnet format` applies it). Warnings are errors.
- Comments explain *why*, not *what*.
- No new network services or telemetry without discussing it in an issue first — privacy is a feature.

## Releases

Maintainers cut releases by tagging; see [docs/releasing.md](docs/releasing.md).
