using ThemeSwitch.Core;

namespace ThemeSwitch.Core.Tests;

/// <summary>
/// A webcam looking at a scene of a given light level. Frames follow the profile's own model (video-range
/// luma through the tone curve), so a correct measurement recovers the scene level.
/// </summary>
sealed class FakeCamera(double sceneLevel, CameraProfile model) : ICameraSession
{
    public string Name { get; init; } = "Fake Cam";
    public (int Min, int Max) ExposureRange { get; init; } = (-11, -2);
    public (int Min, int Max)? GainRange { get; init; } = (0, 255);
    public CameraSettings Initial { get; init; } = new(new(-6, 1), new(147, 1), new(1, 2));
    public Exception? FailOnGrab { get; init; }

    public List<string> Calls { get; } = [];
    public CameraSettings? Restored { get; private set; }
    int _exposure = -6;

    public CameraSettings Snapshot() => Initial;
    public void Restore(CameraSettings settings) { Restored = settings; Calls.Add("restore"); }
    public void SetExposure(int value) { _exposure = value; Calls.Add($"exposure {value}"); }
    public (int Min, int Max) Exposures => (Calls.Where(c => c.StartsWith("exposure")).Min(Num), Calls.Where(c => c.StartsWith("exposure")).Max(Num));
    static int Num(string call) => int.Parse(call.Split(' ')[1]);
    public void SetGain(int value) => Calls.Add($"gain {value}");
    public void SetBacklightCompensation(bool on) => Calls.Add($"backlight {on}");

    public double GrabLuma(int frames, FrameRegion region)
    {
        if (FailOnGrab is { } e) throw e;
        double signal = Math.Pow(2, (sceneLevel + _exposure) / model.Gamma);
        return Math.Clamp(model.Black + (model.White - model.Black) * signal, 0, 255);
    }

    public void Dispose() { }
}

/// <summary>
/// A monitor's control channel with a clock: like a Dell P2423D it ignores a command that arrives sooner
/// than <see cref="DropWithinMs"/> after the previous one.
/// </summary>
sealed class FakeMonitorChannel : IMonitorChannel
{
    public string Name { get; init; } = "Fake Monitor";
    public int DropWithinMs { get; init; }
    public int Max { get; init; } = 100;
    public bool Answers { get; init; } = true;
    public Dictionary<byte, int> Values { get; } = new() { [0x10] = 50, [0x12] = 50 };
    public int Writes { get; private set; }
    /// <summary>The channel itself reports the write as failed (e.g. SetVCPFeature returned FALSE).</summary>
    public bool RejectWrites { get; init; }

    long _now, _lastCommand = long.MinValue / 2;

    public void Pause(int ms) => _now += ms;

    public (int Current, int Max)? Read(byte code)
    {
        _lastCommand = _now;
        return Answers && Values.TryGetValue(code, out int v) ? (v, Max) : null;
    }

    public bool Write(byte code, int value)
    {
        Writes++;
        if (RejectWrites) return false;
        bool tooSoon = _now - _lastCommand < DropWithinMs;
        _lastCommand = _now;
        if (!tooSoon) Values[code] = value;
        return true;
    }
}

public class LightMeasurementTests
{
    static readonly CameraProfile Profile = CameraProfile.Generic;

    [Theory]
    [InlineData(4.75)] // evening lamp: measured at the start exposure
    [InlineData(7.4)]  // daylight: steps to shorter exposures
    [InlineData(3.0)]  // dim: steps to a longer one
    public void RecoversTheSceneLevel(double scene)
    {
        var sample = LightMeasurement.Measure(new FakeCamera(scene, Profile), Profile);

        Assert.Equal(scene, sample.Level, 1);
        Assert.InRange(sample.Luma, Profile.LowLuma, Profile.HighLuma);
    }

    [Fact]
    public void VeryDarkRoomStopsAtTheLongestUsefulExposure()
    {
        var camera = new FakeCamera(-2, Profile);

        var sample = LightMeasurement.Measure(camera, Profile);

        Assert.Equal(Profile.LongestExposure, sample.Exposure);
        Assert.DoesNotContain("exposure -5", camera.Calls);
    }

    [Theory]
    [InlineData(0, 255, 64)] // Brio 500
    [InlineData(0, 100, 25)]
    [InlineData(-10, 10, -5)]
    public void PinsGainAtAFractionOfTheCamerasRange(int min, int max, int expected)
    {
        var camera = new FakeCamera(4.75, Profile) { GainRange = (min, max) };

        LightMeasurement.Measure(camera, Profile);

        Assert.Contains($"gain {expected}", camera.Calls);
    }

    [Fact]
    public void PutsEverySettingBackWithItsAutoFlag()
    {
        var camera = new FakeCamera(4.75, Profile);

        LightMeasurement.Measure(camera, Profile);

        Assert.Equal(camera.Initial, camera.Restored);
        Assert.Equal("restore", camera.Calls[^1]);
        Assert.Contains("backlight False", camera.Calls);
    }

    [Fact]
    public void PutsSettingsBackEvenWhenTheStreamFails()
    {
        var camera = new FakeCamera(4.75, Profile) { FailOnGrab = new IOException("unplugged") };

        Assert.Throws<IOException>(() => LightMeasurement.Measure(camera, Profile));
        Assert.Equal(camera.Initial, camera.Restored);
    }

    [Fact]
    public void CameraWithoutGainOrBacklightControlsIsOnlyExposed()
    {
        var camera = new FakeCamera(4.75, Profile) { GainRange = null, Initial = new(new(-6, 1), null, null) };

        LightMeasurement.Measure(camera, Profile);

        Assert.DoesNotContain(camera.Calls, c => c.StartsWith("gain") || c.StartsWith("backlight"));
    }

    [Fact]
    public void StartExposureOutsideTheCamerasRangeIsClamped()
    {
        var camera = new FakeCamera(9, Profile) { ExposureRange = (-5, 0) };

        LightMeasurement.Measure(camera, Profile);

        Assert.Equal("exposure -5", camera.Calls.First(c => c.StartsWith("exposure")));
    }

    [Fact]
    public void NeverStartsPastTheLongestUsefulExposure()
    {
        var slowStart = Profile with { StartExposure = -3 };
        var camera = new FakeCamera(3.5, slowStart);

        LightMeasurement.Measure(camera, slowStart);

        Assert.True(camera.Exposures.Max <= Profile.LongestExposure);
    }

    [Fact]
    public void InvalidExposureRangeIsRefusedBeforeTouchingTheCamera()
    {
        var camera = new FakeCamera(5, Profile) { ExposureRange = (0, -11) };

        Assert.Throws<NotSupportedException>(() => LightMeasurement.Measure(camera, Profile));
        Assert.Empty(camera.Calls);
    }

    [Fact]
    public void GainThatCantBeFixedIsRefused()
    {
        var camera = new FakeCamera(5, Profile) { GainRange = null }; // has gain (Initial), no range

        Assert.Throws<NotSupportedException>(() => LightMeasurement.Measure(camera, Profile));
        Assert.Empty(camera.Calls);
    }

    [Fact]
    public void BuiltInProfilesDoNotDependOnTheGenericDefaults()
    {
        Assert.Equal(1.6, CameraProfile.Brio500.Gamma);
        Assert.Equal(150, DisplayProfile.DellP2423D.CommandGapMs);
    }

    [Fact]
    public void ProfileDecidesTheModel()
    {
        // A camera with full-range output and a different tone curve: its own profile reads it right.
        var fullRange = Profile with { Name = "full range", Black = 0, White = 255, Gamma = 2.2 };

        var sample = LightMeasurement.Measure(new FakeCamera(5, fullRange), fullRange);

        Assert.Equal(5, sample.Level, 1);
    }

    [Theory]
    [InlineData("Brio 500", "Logitech Brio 500")]
    [InlineData("Logitech BRIO 500 webcam", "Logitech Brio 500")]
    [InlineData("Integrated Camera", "generic")]
    public void BuiltInProfileIsChosenByName(string camera, string profile)
    {
        Assert.Equal(profile, CameraProfile.For(camera).Name);
    }
}

public class DisplayControllerTests
{
    static DisplayController Controller(FakeMonitorChannel channel, DisplayProfile? profile = null) =>
        new(channel, profile ?? DisplayProfile.Generic, channel.Pause);

    [Fact]
    public void MonitorThatDropsFastCommandsGetsEveryWriteWithTheGap()
    {
        var dellLike = new FakeMonitorChannel { DropWithinMs = 100 };

        bool ok = Controller(dellLike, DisplayProfile.DellP2423D).Write(70, includeContrast: true);

        Assert.True(ok);
        Assert.Equal(70, dellLike.Values[0x10]);
        Assert.Equal(70, dellLike.Values[0x12]);
    }

    [Fact]
    public void WithoutTheGapSuchAMonitorLosesWritesAndItIsReported()
    {
        var dellLike = new FakeMonitorChannel { DropWithinMs = 100 };
        var tooFast = DisplayProfile.Generic with { CommandGapMs = 0 };

        bool ok = Controller(dellLike, tooFast).Write(70, includeContrast: false);

        Assert.False(ok);
        Assert.Equal(50, dellLike.Values[0x10]);
        Assert.Equal(3, dellLike.Writes); // tried WriteAttempts times
    }

    [Fact]
    public void PercentIsScaledToTheMonitorsOwnMaximum()
    {
        var channel = new FakeMonitorChannel { Max = 50 };
        channel.Values[0x10] = 25;
        var controller = Controller(channel);

        Assert.Equal(50, controller.ReadBrightness());
        controller.Write(60, includeContrast: false);
        Assert.Equal(30, channel.Values[0x10]);
    }

    [Fact]
    public void ContrastCanFollowBrightnessOnItsOwnRange()
    {
        var channel = new FakeMonitorChannel();
        var profile = DisplayProfile.Generic with { ContrastMin = 50, ContrastMax = 100 };

        Controller(channel, profile).Write(60, includeContrast: true);

        Assert.Equal(60, channel.Values[0x10]);
        Assert.Equal(80, channel.Values[0x12]);
    }

    [Fact]
    public void VendorCodesComeFromTheProfile()
    {
        var channel = new FakeMonitorChannel();
        channel.Values[0xE0] = 10;
        var profile = DisplayProfile.Generic with { BrightnessCode = 0xE0 };

        Controller(channel, profile).Write(40, includeContrast: false);

        Assert.Equal(40, channel.Values[0xE0]);
        Assert.Equal(50, channel.Values[0x10]);
    }

    [Fact]
    public void WithoutVerificationEachValueIsWrittenOnce()
    {
        var channel = new FakeMonitorChannel { DropWithinMs = 100 };
        var blind = DisplayProfile.Generic with { CommandGapMs = 0, VerifyWrites = false };

        Assert.True(Controller(channel, blind).Write(70, includeContrast: false));
        Assert.Equal(1, channel.Writes);
    }

    [Fact]
    public void WriteTheChannelRejectsIsRetriedAndReportedEvenWithoutVerification()
    {
        var channel = new FakeMonitorChannel { RejectWrites = true };
        var blind = DisplayProfile.Generic with { VerifyWrites = false };

        Assert.False(Controller(channel, blind).Write(70, includeContrast: false));
        Assert.Equal(3, channel.Writes);
    }

    [Fact]
    public void SilentMonitorReadsNullAndRefusesWrites()
    {
        var silent = new FakeMonitorChannel { Answers = false };
        var controller = Controller(silent);

        Assert.Null(controller.ReadBrightness());
        Assert.False(controller.Write(60, includeContrast: true));
        Assert.Equal(0, silent.Writes);
    }

    [Theory]
    [InlineData("Dell P2423D(DisplayPort)", "Dell P2423D")]
    [InlineData("Generic PnP Monitor", "generic")]
    public void BuiltInProfileIsChosenByName(string monitor, string profile)
    {
        Assert.Equal(profile, DisplayProfile.For(monitor).Name);
    }
}
