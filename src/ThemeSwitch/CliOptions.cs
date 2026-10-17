using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ThemeSwitch;

enum Command { Tray, Report, ApplyOnce, Toggle, Help, Version }

sealed record CliOptions
{
    public Command Command { get; init; } = Command.Tray;
    public string? DataDir { get; init; }
    public string? ThemeKey { get; init; }
    public string? FixturesDir { get; init; }
    public DateTime? NowUtc { get; init; }
    public string? TimeZoneId { get; init; }
    public bool NoAutostart { get; init; }
    public bool ForceRefresh { get; init; }

    public const string Usage = """
        ThemeSwitch - switches Windows light/dark with the sun and the weather.

        Usage: ThemeSwitch.exe [command] [options]

        Commands:
          --tray          Run in the notification area (default).
          --report        Print location, sun times and today's hourly plan as JSON.
          --apply-once    Apply the automatic decision once, print the status, exit.
          --toggle        Flip the theme like a tray click, print the status, exit.
          --version       Print the version.
          --help          Show this help.

        Options (mostly for testing):
          --data-dir <dir>     Settings/cache folder (default %APPDATA%\ThemeSwitch).
          --theme-key <key>    HKCU registry key to treat as the theme (default: the real one).
          --fixtures <dir>     Read ipwhois.json / ipapi.json / forecast.json instead of the network.
          --now <iso-8601>     Pretend it's this instant, e.g. 2026-10-17T12:00:00Z.
          --tz <zone-id>       Use this time zone instead of the PC's, e.g. Europe/Lisbon.
          --refresh            Re-check location and weather even if the cache is fresh.
          --no-autostart       Never touch the "Start with Windows" entry.
        """;

    /// <summary>One instance per data folder, so a test instance can run beside the real one.</summary>
    public string MutexName
    {
        get
        {
            string dir = (DataDir ?? "default").ToUpperInvariant();
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dir)))[..16];
            return $@"Local\ThemeSwitch-{hash}";
        }
    }

    /// <exception cref="ArgumentException">Unknown or malformed argument.</exception>
    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var o = new CliOptions();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{arg} needs a value");
            o = arg switch
            {
                "--tray" => o with { Command = Command.Tray },
                "--report" => o with { Command = Command.Report },
                "--apply-once" => o with { Command = Command.ApplyOnce },
                "--toggle" => o with { Command = Command.Toggle },
                "--help" or "-h" or "/?" => o with { Command = Command.Help },
                "--version" => o with { Command = Command.Version },
                "--data-dir" => o with { DataDir = Path.GetFullPath(Value()) },
                "--theme-key" => o with { ThemeKey = Value() },
                "--fixtures" => o with { FixturesDir = Path.GetFullPath(Value()) },
                "--now" => o with { NowUtc = ParseInstant(Value()) },
                "--tz" => o with { TimeZoneId = Value() },
                "--refresh" => o with { ForceRefresh = true },
                "--no-autostart" => o with { NoAutostart = true },
                _ => throw new ArgumentException($"unknown argument: {arg}"),
            };
        }
        return o;
    }

    static DateTime ParseInstant(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.UtcDateTime
            : throw new ArgumentException($"--now: not a date/time: {text}");
}
