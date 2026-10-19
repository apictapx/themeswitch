using ThemeSwitch.Core;
using static ThemeSwitch.Core.Tests.TestData;

namespace ThemeSwitch.Core.Tests;

public class LightLevelTests
{
    static readonly CameraProfile Brio = CameraProfile.Brio500;

    [Fact]
    public void AdjacentExposuresOfTheSameSceneAgree()
    {
        // Brio 500, gain 64, one evening room: the same light seen at three exposures.
        double a = LightLevel.FromLuma(61, -8, Brio), b = LightLevel.FromLuma(93, -7, Brio), c = LightLevel.FromLuma(130, -6, Brio);

        Assert.InRange(Math.Max(a, Math.Max(b, c)) - Math.Min(a, Math.Min(b, c)), 0, 0.5);
    }

    [Fact]
    public void BrighterFrameOrShorterExposureMeansMoreLight()
    {
        Assert.True(LightLevel.FromLuma(120, -7, Brio) > LightLevel.FromLuma(80, -7, Brio));
        Assert.Equal(LightLevel.FromLuma(100, -7, Brio) + 1, LightLevel.FromLuma(100, -8, Brio), 6);
    }

    [Fact]
    public void BlackFramesDoNotProduceInfinity()
    {
        Assert.True(double.IsFinite(LightLevel.FromLuma(0, -6, Brio)));
        Assert.True(double.IsFinite(LightLevel.FromLuma(255, -11, Brio)));
    }

    [Theory]
    [InlineData(-7, 40, -6)]    // too dark: longer
    [InlineData(-6, 40, null)]  // already at the longest useful exposure
    [InlineData(-7, 230, -8)]   // too bright: shorter
    [InlineData(-11, 230, null)]
    [InlineData(-7, 120, null)] // in range
    public void ExposureLadder(int exposure, double luma, int? expected)
    {
        Assert.Equal(expected, LightLevel.NextExposure(exposure, luma, shortest: -11, longest: -2, Brio));
    }
}

public class BrightnessCurveTests
{
    static BrightnessPoint P(double level, int percent) => new(level, percent, Utc(12));

    [Fact]
    public void DefaultLineIsTheCalibratedOne()
    {
        var curve = BrightnessCurve.Fit([], BrightnessCurve.DefaultSlope);

        Assert.Equal(50, curve.At(3.2), 1);
        Assert.Equal(80, curve.At(7.4), 0);
    }

    [Fact]
    public void OnePointShiftsTheDefaultLineThroughIt()
    {
        var curve = BrightnessCurve.Fit([P(5, 70)], 7.2);

        Assert.Equal(70, curve.At(5), 6);
        Assert.Equal(7.2, curve.Slope);
    }

    [Fact]
    public void SpreadPointsSetTheSlope()
    {
        var curve = BrightnessCurve.Fit([P(3, 50), P(7, 90)], 7.2);

        Assert.Equal(10, curve.Slope, 6);
        Assert.Equal(70, curve.At(5), 6);
    }

    [Fact]
    public void PointsTooCloseTogetherKeepTheDefaultSlope()
    {
        var curve = BrightnessCurve.Fit([P(4.0, 50), P(4.5, 80)], 7.2);

        Assert.Equal(7.2, curve.Slope);
    }

    [Fact]
    public void AbsurdSlopesAreBounded()
    {
        Assert.Equal(30, BrightnessCurve.Fit([P(3, 0), P(4, 100)], 7.2).Slope);
        Assert.Equal(2, BrightnessCurve.Fit([P(3, 80), P(6, 50)], 7.2).Slope); // brighter room, dimmer screen
    }
}

public class BrightnessAutomationTests
{
    readonly AppSettings _settings = new() { AutoBrightness = true };

    BrightnessAutomation New() => new(_settings);

    [Fact]
    public void FirstTickLearnsWhatTheMonitorShowsInsteadOfJumping()
    {
        var status = New().Tick(Utc(12), 5.0, 66);

        Assert.Equal(BrightnessAction.Learned, status.Action);
        Assert.Null(status.Write);
        Assert.Equal(66, _settings.BrightnessLastSet);
        Assert.Single(_settings.BrightnessPoints);
        Assert.Equal(65, status.Target); // 66 on the 5 % grid
    }

    [Fact]
    public void BrighterRoomRaisesBrightness()
    {
        var a = New();
        a.Tick(Utc(12), 3.2, 50);

        var status = a.Tick(Utc(12, 5), 7.4, 50);

        Assert.Equal(BrightnessAction.Apply, status.Action);
        Assert.Equal(80, status.Write);
        Assert.Equal(80, _settings.BrightnessLastSet);
    }

    [Fact]
    public void SmallLightChangesAreIgnored()
    {
        var a = New();
        a.Tick(Utc(12), 4.0, 56);

        var status = a.Tick(Utc(12, 5), 4.3, 56); // +2 % on the curve

        Assert.Equal(BrightnessAction.None, status.Action);
    }

    [Fact]
    public void TargetStaysInsideTheRange()
    {
        var a = New();
        a.Tick(Utc(12), 3.2, 50);

        Assert.Equal(80, a.Tick(Utc(12, 5), 12, 50).Write);
        Assert.Equal(50, a.Tick(Utc(12, 10), -3, 80).Target);
    }

    [Fact]
    public void ManualChangeIsLearnedAndHeldUntilTheLightChanges()
    {
        var a = New();
        a.Tick(Utc(12), 5.0, 63);

        var learned = a.Tick(Utc(12, 5), 5.0, 75); // user turned it up

        Assert.Equal(BrightnessAction.Learned, learned.Action);
        Assert.Equal(75, _settings.BrightnessPoints.Single().Percent); // replaced the nearby point
        Assert.Equal(BrightnessAction.None, a.Tick(Utc(12, 10), 5.1, 75).Action);

        var darker = a.Tick(Utc(12, 15), 3.0, 75);
        Assert.Equal(BrightnessAction.Apply, darker.Action);
        Assert.True(darker.Write < 75);
    }

    [Fact]
    public void ManualValueOutsideTheRangeIsRespectedUntilTheLightChanges()
    {
        var a = New();
        a.Tick(Utc(12), 7.0, 80);

        a.Tick(Utc(12, 5), 7.0, 95);

        Assert.Equal(80, _settings.BrightnessPoints.Single().Percent);
        Assert.Equal(BrightnessAction.None, a.Tick(Utc(12, 10), 7.0, 95).Action);
    }

    [Fact]
    public void TwoAdjustmentsInDifferentLightTeachTheSlope()
    {
        _settings.BrightnessMax = 100;
        var a = New();
        a.Tick(Utc(12), 3.0, 50);
        a.Tick(Utc(18), 7.0, 50);
        a.Tick(Utc(18, 5), 7.0, 90); // user: much brighter in daylight

        Assert.Equal(10, _settings.ToBrightnessCurve().Slope, 6);
    }

    [Fact]
    public void OldPointsAreDroppedBeyondEight()
    {
        var a = New();
        int last = 50;
        for (int i = 0; i < 12; i++)
        {
            a.Restart();
            a.Tick(Utc(12, i), i, last = 50 + i);
        }

        Assert.Equal(8, _settings.BrightnessPoints.Count);
        Assert.DoesNotContain(_settings.BrightnessPoints, p => p.Level < 4);
    }

    [Fact]
    public void AdjustNowOverridesAManualValueWithoutLearningIt()
    {
        var a = New();
        a.Tick(Utc(12), 5.0, 63);

        var status = a.AdjustNow(Utc(12, 1), 5.0, 75); // user had turned it up; click = match the room

        Assert.Equal(BrightnessAction.Apply, status.Action);
        Assert.Equal(65, status.Write); // the curve says 63; brightness lives on the 5 % grid
        Assert.Equal(63, _settings.BrightnessPoints.Single().Percent);
        Assert.Equal(BrightnessAction.None, a.Tick(Utc(12, 5), 5.0, 65).Action); // not seen as a manual change
    }

    [Fact]
    public void AdjustNowIgnoresHysteresisAndSmoothing()
    {
        var a = New();
        a.Tick(Utc(12), 4.0, 56); // learned: 56 at 4.0, grid target 55

        Assert.Equal(BrightnessAction.None, a.Tick(Utc(12, 5), 4.4, 56).Action); // curve 58.9: inside the hysteresis
        var status = a.AdjustNow(Utc(12, 6), 4.4, 56);

        Assert.Equal(4.4, status.Level);
        Assert.Equal(60, status.Write);
    }

    [Fact]
    public void TargetsAreMultiplesOfTheStep()
    {
        var a = New();
        for (double level = 2; level <= 9; level += 0.1)
            Assert.Equal(0, a.Target(level) % 5);
        _settings.BrightnessStep = 10;
        Assert.Equal(0, a.Target(5.3) % 10);
    }

    [Fact]
    public void LevelHoveringOnARoundingBoundaryDoesNotFlipFlop()
    {
        var a = New();
        a.Tick(Utc(12), 4.0, 60); // curve through (4.0, 60): 62.5 is the 60/65 boundary at ~4.35
        Assert.Equal(60, _settings.BrightnessLastTarget);

        foreach (var (minute, level) in new[] { (5, 4.33), (10, 4.37), (15, 4.32), (20, 4.38) })
            Assert.Equal(BrightnessAction.None, a.Tick(Utc(12, minute), level, 60).Action);

        var clear = a.Tick(Utc(12, 25), 4.9, 60); // smoothed ~4.64, curve ~64.6: well past the boundary
        Assert.Equal(65, clear.Write);
    }

    [Fact]
    public void AdjustNowWithoutHistoryUsesTheDefaultCurve()
    {
        var status = New().AdjustNow(Utc(12), 7.4, 50);

        Assert.Equal(80, status.Write);
        Assert.Empty(_settings.BrightnessPoints);
    }

    [Fact]
    public void UnresponsiveMonitorChangesNothing()
    {
        var a = New();
        a.Tick(Utc(12), 4.0, 56);

        var status = a.Tick(Utc(12, 5), 7.0, null);

        Assert.Equal(BrightnessAction.MonitorUnavailable, status.Action);
        Assert.Equal(56, _settings.BrightnessLastSet);
    }

    [Fact]
    public void FailedWriteIsNotMistakenForAManualChange()
    {
        var a = New();
        a.Tick(Utc(12), 3.2, 50);
        int? target = _settings.BrightnessLastTarget;

        a.Tick(Utc(12, 5), 7.4, 50);
        a.WriteFailed(50, target); // nothing reached the monitor
        var retry = a.Tick(Utc(12, 10), 7.4, 50);

        Assert.Equal(BrightnessAction.Apply, retry.Action);
        Assert.Single(_settings.BrightnessPoints);
    }

    [Fact]
    public void FadeThatStoppedHalfWayIsNotLearned()
    {
        var a = New();
        a.Tick(Utc(12), 3.2, 50);
        int? target = _settings.BrightnessLastTarget;

        a.Tick(Utc(12, 5), 7.4, 50);
        a.WriteFailed(57, target); // the monitor took the first fade step only
        var retry = a.Tick(Utc(12, 10), 7.4, 57);

        Assert.Equal(BrightnessAction.Apply, retry.Action);
        Assert.Equal(80, retry.Write);
        Assert.Single(_settings.BrightnessPoints);
    }

    [Fact]
    public void SmoothingDampsNoiseButFollowsTheLightsBeingSwitched()
    {
        var a = New();
        a.Tick(Utc(12), 4.0, 56);

        Assert.Equal(4.5, a.Tick(Utc(12, 5), 5.0, 56).Level, 6);   // half-way
        Assert.Equal(7.5, a.Tick(Utc(12, 10), 7.5, 56).Level, 6);  // jump: at once
        Assert.Equal(3.0, a.Tick(Utc(14), 3.0, 56).Level, 6);      // long gap: start over
    }

    [Theory]
    [InlineData(50, 80, new[] { 55, 60, 65, 70, 75, 80 })]
    [InlineData(80, 70, new[] { 75, 70 })]
    [InlineData(54, 80, new[] { 55, 60, 65, 70, 75, 80 })] // hand-set value: first step joins the grid
    [InlineData(68, 50, new[] { 65, 60, 55, 50 })]
    [InlineData(55, 60, new[] { 60 })]
    [InlineData(60, 60, new int[0])]
    public void FadeEndsOnTheTarget(int from, int to, int[] expected)
    {
        Assert.Equal(expected, BrightnessAutomation.FadeSteps(from, to, 5));
    }
}
