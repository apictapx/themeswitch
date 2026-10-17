using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace ThemeSwitch.E2E;

/// <summary>
/// One isolated world for the real exe: a temp data folder, a fixtures folder standing in for the
/// network, and a throwaway HKCU key standing in for the Windows theme. Nothing global is touched.
/// </summary>
sealed class AppSandbox : IDisposable
{
    const string KeyRoot = @"Software\ThemeSwitch.E2E";
    readonly string _id = Guid.NewGuid().ToString("N")[..12];

    public string Root { get; }
    public string DataDir => Path.Combine(Root, "data");
    public string FixturesDir => Path.Combine(Root, "fixtures");
    public string ThemeKey => $@"{KeyRoot}\{_id}\Personalize";

    public static string ExePath { get; } = Path.Combine(AppContext.BaseDirectory, "ThemeSwitch.exe");

    public AppSandbox()
    {
        Root = Directory.CreateTempSubdirectory("themeswitch-e2e-").FullName;
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(FixturesDir);
    }

    // ---- fixtures -------------------------------------------------------------------------------

    public void WriteLocation(double lat, double lon, string city, string zoneId) =>
        File.WriteAllText(Path.Combine(FixturesDir, "ipwhois.json"), JsonSerializer.Serialize(new
        {
            ip = "192.0.2.1",
            success = true,
            latitude = lat,
            longitude = lon,
            city,
            country = "Testland",
            timezone = new { id = zoneId },
        }));

    public void WriteLisbon() => WriteLocation(38.7223, -9.1393, "Lisbon", "Europe/Lisbon");

    /// <summary>72 clear hours from 2026-10-16T00:00Z (Open-Meteo shape), with chosen UTC hours of the 17th overridden.</summary>
    public void WriteForecast(params (int HourUtc, int Code, int Cloud, double Precipitation)[] overrides)
    {
        var start = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        var time = new JsonArray(); var code = new JsonArray(); var cloud = new JsonArray(); var rain = new JsonArray();
        for (int i = 0; i < 72; i++)
        {
            var t = start.AddHours(i);
            var o = overrides.FirstOrDefault(x => t.Day == 17 && t.Hour == x.HourUtc);
            time.Add(t.ToUnixTimeSeconds());
            code.Add(o == default ? 0 : o.Code);
            cloud.Add(o == default ? 0 : o.Cloud);
            rain.Add(o == default ? 0.0 : o.Precipitation);
        }
        var json = new JsonObject
        {
            ["latitude"] = 38.75,
            ["longitude"] = -9.15,
            ["hourly"] = new JsonObject { ["time"] = time, ["weather_code"] = code, ["cloud_cover"] = cloud, ["precipitation"] = rain },
        };
        File.WriteAllText(Path.Combine(FixturesDir, "forecast.json"), json.ToJsonString());
    }

    public void RemoveFixture(string name) => File.Delete(Path.Combine(FixturesDir, name));

    // ---- the fake theme -------------------------------------------------------------------------

    public string? Theme
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(ThemeKey);
            return key?.GetValue("AppsUseLightTheme") switch { 1 => "Light", 0 => "Dark", _ => null };
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(ThemeKey);
            key.SetValue("AppsUseLightTheme", value == "Light" ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    public int? SystemTheme
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(ThemeKey);
            return key?.GetValue("SystemUsesLightTheme") as int?;
        }
    }

    // ---- running the exe ------------------------------------------------------------------------

    public sealed record Result(int ExitCode, string Stdout, string Stderr)
    {
        public JsonNode Json => JsonNode.Parse(Stdout) ?? throw new InvalidOperationException("empty output");
    }

    /// <summary>Runs a one-shot command in the sandbox. <paramref name="nowUtc"/> pins the clock.</summary>
    public Result Run(string command, DateTime? nowUtc = null, params string[] extra)
    {
        var args = new List<string> { command };
        args.AddRange(SandboxArgs());
        if (nowUtc is { } now) args.AddRange(["--now", now.ToString("o", CultureInfo.InvariantCulture)]);
        args.AddRange(extra);
        return RunRaw(args);
    }

    public IEnumerable<string> SandboxArgs() =>
    [
        "--data-dir", DataDir,
        "--theme-key", ThemeKey,
        "--fixtures", FixturesDir,
        "--tz", "Europe/Lisbon",
        "--no-autostart",
    ];

    public static Result RunRaw(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill();
            throw new TimeoutException($"ThemeSwitch.exe {string.Join(' ', args)} did not exit");
        }
        return new Result(process.ExitCode, stdout.Result, stderr.Result);
    }

    public Process StartTray()
    {
        var psi = new ProcessStartInfo(ExePath) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--tray");
        foreach (var a in SandboxArgs()) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    public string Log => File.Exists(Path.Combine(DataDir, "themeswitch.log"))
        ? File.ReadAllText(Path.Combine(DataDir, "themeswitch.log"))
        : "";

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"{KeyRoot}\{_id}", throwOnMissingSubKey: false);
        using (var root = Registry.CurrentUser.OpenSubKey(KeyRoot))
            if (root is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(KeyRoot, throwOnMissingSubKey: false);
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { /* a killed tray may still hold the log briefly */ }
    }
}
