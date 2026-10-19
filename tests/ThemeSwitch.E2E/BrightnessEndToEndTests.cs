namespace ThemeSwitch.E2E;

/// <summary>
/// Auto brightness through the real exe, with a fixed light level (--light) instead of the webcam and a
/// file (--monitor-file, "brightness contrast") instead of a DDC/CI monitor.
/// </summary>
public class BrightnessEndToEndTests : IDisposable
{
    readonly AppSandbox _box = new();
    string MonitorFile => Path.Combine(_box.Root, "monitor.txt");

    string Monitor
    {
        get => File.ReadAllText(MonitorFile);
        set => File.WriteAllText(MonitorFile, value);
    }

    AppSandbox.Result Run(string command, double light) =>
        _box.Run(command, null, "--light", light.ToString(System.Globalization.CultureInfo.InvariantCulture), "--monitor-file", MonitorFile);

    [Fact]
    public void LightReportShowsTheTargetAndChangesNothing()
    {
        Monitor = "50 50";

        var r = Run("--light-report", 7.4);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(7.4, (double?)r.Json["lightLevel"]);
        Assert.Equal(50, (int?)r.Json["brightness"]);
        Assert.Equal(80, (int?)r.Json["target"]);
        Assert.Equal("Test monitor", (string?)r.Json["monitors"]![0]);
        Assert.Equal("50 50", Monitor);
        Assert.False(File.Exists(Path.Combine(_box.DataDir, "settings.json")));
    }

    [Fact]
    public void FirstRunLearnsThenBrighterRoomFadesBrightnessAndContrastUp()
    {
        Monitor = "50 50";

        var first = Run("--brightness-once", 3.2);
        Assert.Equal("Learned", (string?)first.Json["action"]);
        Assert.Equal("50 50", Monitor);

        var second = Run("--brightness-once", 7.4);
        Assert.Equal("Apply", (string?)second.Json["action"]);
        Assert.Equal(80, (int?)second.Json["to"]);
        Assert.Equal("80 80", Monitor);
        Assert.Contains("brightness: light 7.40 · 50% -> 80%", _box.Log);
    }

    [Fact]
    public void ManualChangeIsLearnedAndKept()
    {
        Monitor = "60 60";
        Run("--brightness-once", 5.0);

        Monitor = "70 60"; // user turned brightness up on the monitor
        var learned = Run("--brightness-once", 5.0);
        var again = Run("--brightness-once", 5.1);

        Assert.Equal("Learned", (string?)learned.Json["action"]);
        Assert.Equal("None", (string?)again.Json["action"]);
        Assert.StartsWith("70", Monitor);
        Assert.Contains("\"percent\": 70", File.ReadAllText(Path.Combine(_box.DataDir, "settings.json")));
    }

    [Fact]
    public void BrightnessNowResetsAManualValueToTheRoom()
    {
        Monitor = "50 50";
        Run("--brightness-once", 3.2);
        Monitor = "75 75"; // set by hand

        var r = Run("--brightness-now", 3.2);

        Assert.Equal("Apply", (string?)r.Json["action"]);
        Assert.Equal("50 50", Monitor);
        Assert.Contains("brightness (now): light 3.20", _box.Log);
        Assert.DoesNotContain("\"percent\": 75", File.ReadAllText(Path.Combine(_box.DataDir, "settings.json")));
    }

    [Fact]
    public void ContrastCanBeLeftAlone()
    {
        Monitor = "50 40";
        File.WriteAllText(Path.Combine(_box.DataDir, "settings.json"), """{ "brightnessIncludesContrast": false }""");
        Run("--brightness-once", 3.2);

        Run("--brightness-once", 7.4);

        Assert.Equal("80 40", Monitor);
    }

    [Fact]
    public void MissingMonitorIsReportedNotFatal()
    {
        var r = Run("--brightness-once", 5.0);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("MonitorUnavailable", (string?)r.Json["action"]);
        Assert.Contains("no monitor answers DDC/CI", _box.Log);
    }

    public void Dispose() => _box.Dispose();
}
