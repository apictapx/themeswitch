namespace ThemeSwitch.Core;

/// <summary>
/// Turns a webcam frame's average luma at a fixed gain and a known exposure into a room light level on a
/// log2 scale: +1 means twice the light. The camera's own auto exposure would pull every scene towards
/// mid-grey, so the meter locks exposure and walks it until the frame is well inside the sensor's range.
/// </summary>
public static class LightLevel
{
    /// <summary>Video-range black: Y=16 is black, Y=235 is white.</summary>
    const double Black = 16, White = 235;
    /// <summary>Fitted on a Logitech Brio 500: makes adjacent exposure steps agree within ~0.3 levels.</summary>
    const double Gamma = 1.6;

    public const int StartExposure = -7;
    /// <summary>Longer than 1/64 s runs into the 30 fps frame time and stops adding light.</summary>
    public const int LongestExposure = -6;
    public const double LowLuma = 60, HighLuma = 200;

    /// <param name="exposureLog2">UVC exposure: the time is 2^exposureLog2 seconds.</param>
    public static double FromLuma(double meanLuma, int exposureLog2)
    {
        double signal = Math.Clamp((meanLuma - Black) / (White - Black), 0.002, 1);
        return Math.Log2(Math.Pow(signal, Gamma)) - exposureLog2;
    }

    /// <summary>The exposure to try next, or null when this frame is good enough (or the camera can't go further).</summary>
    public static int? NextExposure(int exposureLog2, double meanLuma, int shortest, int longest)
    {
        longest = Math.Min(longest, LongestExposure);
        if (meanLuma < LowLuma && exposureLog2 < longest) return exposureLog2 + 1;
        if (meanLuma > HighLuma && exposureLog2 > shortest) return exposureLog2 - 1;
        return null;
    }
}

/// <summary>A manual adjustment: at this light level the user wanted this much.</summary>
public sealed record BrightnessPoint(double Level, int Percent, DateTime AtUtc);

/// <summary>percent = Intercept + Slope × level, before clamping to the configured range.</summary>
public sealed record BrightnessCurve(double Slope, double Intercept)
{
    /// <summary>Calibrated in one room: level 3.2 (dark) → 50 %, level 7.4 (bright daylight) → 80 %.</summary>
    public const double DefaultSlope = 7.2;
    const double DefaultLevel = 3.2, DefaultPercent = 50;
    const double MinSlope = 2, MaxSlope = 30;
    /// <summary>Points closer than this in light level can't tell a slope apart from noise.</summary>
    const double MinSpread = 1;

    public double At(double level) => Intercept + Slope * level;

    /// <summary>
    /// No points: the default line. One point, or points too close together: the default slope through
    /// their average. Otherwise a least-squares line, its slope kept within sane bounds.
    /// </summary>
    public static BrightnessCurve Fit(IReadOnlyList<BrightnessPoint> points, double defaultSlope)
    {
        if (points.Count == 0) return new(defaultSlope, DefaultPercent - defaultSlope * DefaultLevel);
        double meanLevel = points.Average(p => p.Level), meanPercent = points.Average(p => p.Percent);
        double slope = defaultSlope;
        if (points.Max(p => p.Level) - points.Min(p => p.Level) >= MinSpread)
        {
            double sxx = points.Sum(p => (p.Level - meanLevel) * (p.Level - meanLevel));
            double sxy = points.Sum(p => (p.Level - meanLevel) * (p.Percent - meanPercent));
            slope = Math.Clamp(sxy / sxx, MinSlope, MaxSlope);
        }
        return new(slope, meanPercent - slope * meanLevel);
    }
}

public enum BrightnessAction { None, Apply, Learned, MonitorUnavailable }

public sealed record BrightnessStatus(
    BrightnessAction Action,
    double RawLevel,
    double Level,
    int? Current,
    int? Target,
    BrightnessCurve Curve)
{
    /// <summary>The value to write, when <see cref="Action"/> is Apply.</summary>
    public int? Write => Action == BrightnessAction.Apply ? Target : null;
}

/// <summary>
/// Decides monitor brightness from room light. Values sit on a grid (multiples of BrightnessStep) and move
/// only when the light does (the curve's answer moves to another grid value, with some hysteresis), so a
/// manual adjustment holds until the room changes. A manual adjustment
/// is noticed as the monitor reading something other than what was last set, and is learned as a
/// point on the curve.
/// </summary>
public sealed class BrightnessAutomation(AppSettings settings)
{
    const int MaxPoints = 8;
    /// <summary>A new point replaces older ones this close in light level.</summary>
    const double SameLevel = 0.5;
    /// <summary>The DDC readback of a value just written can be off by one on some monitors.</summary>
    const int ReadbackTolerance = 1;
    /// <summary>A jump this big is the lights being switched, not noise: follow it at once.</summary>
    const double Jump = 1.5;
    const double Smoothing = 0.5;
    /// <summary>Beyond half a step: a level hovering on a rounding boundary must not flip 60 ↔ 65 every run.</summary>
    const double Hysteresis = 1;

    double? _smoothed;
    DateTime _smoothedAtUtc;

    public AppSettings Settings => settings;

    public BrightnessStatus Tick(DateTime nowUtc, double rawLevel, int? current)
    {
        double level = Smooth(nowUtc, rawLevel);
        if (current is not { } now)
            return new(BrightnessAction.MonitorUnavailable, rawLevel, level, null, null, settings.ToBrightnessCurve());

        if (settings.BrightnessLastSet is not { } last || Math.Abs(now - last) > ReadbackTolerance)
        {
            // First run after enabling, or someone changed it by hand: that's the preference at this light.
            Learn(level, now, nowUtc);
            settings.BrightnessLastSet = now;
            settings.BrightnessLastTarget = Target(level);
            return new(BrightnessAction.Learned, rawLevel, level, now, settings.BrightnessLastTarget, settings.ToBrightnessCurve());
        }

        double exact = Exact(level);
        int target = Snap(exact);
        var curve = settings.ToBrightnessCurve();
        if (settings.BrightnessLastTarget is { } lastTarget
            && (target == lastTarget || Math.Abs(exact - lastTarget) < Step / 2.0 + Hysteresis))
            return new(BrightnessAction.None, rawLevel, level, now, lastTarget, curve);

        settings.BrightnessLastTarget = target;
        if (target == now) return new(BrightnessAction.None, rawLevel, level, now, target, curve);
        settings.BrightnessLastSet = target;
        return new(BrightnessAction.Apply, rawLevel, level, now, target, curve);
    }

    /// <summary>
    /// "Match the room now" (the tray click): the curve's value for the light as measured this moment, written
    /// even for a small difference and over a manual setting, which is not learned.
    /// </summary>
    public BrightnessStatus AdjustNow(DateTime nowUtc, double rawLevel, int? current)
    {
        _smoothed = rawLevel;
        _smoothedAtUtc = nowUtc;
        var curve = settings.ToBrightnessCurve();
        if (current is not { } now)
            return new(BrightnessAction.MonitorUnavailable, rawLevel, rawLevel, null, null, curve);

        int target = Target(rawLevel);
        settings.BrightnessLastTarget = target;
        settings.BrightnessLastSet = target;
        var action = target == now ? BrightnessAction.None : BrightnessAction.Apply;
        return new(action, rawLevel, rawLevel, now, target, curve);
    }

    /// <summary>
    /// Writing failed, perhaps half-way through a fade: remember what the monitor actually shows (so the
    /// next tick doesn't learn it as a manual change) and forget the target (so the next tick retries).
    /// </summary>
    public void WriteFailed(int? monitorNow, int? previousLastTarget)
    {
        if (monitorNow is { } now) settings.BrightnessLastSet = now;
        settings.BrightnessLastTarget = previousLastTarget;
    }

    /// <summary>Called when automation is switched on: whatever the monitor shows then is taken as the preference.</summary>
    public void Restart()
    {
        settings.BrightnessLastSet = null;
        settings.BrightnessLastTarget = null;
        _smoothed = null;
    }

    /// <summary>The brightness for this light: the curve's value, on the step grid, within the range.</summary>
    public int Target(double level) => Snap(Exact(level));

    int Step => Math.Clamp(settings.BrightnessStep, 1, 50);

    (int Min, int Max) Range()
    {
        int min = Math.Clamp(settings.BrightnessMin, 0, 100);
        return (min, Math.Clamp(settings.BrightnessMax, min, 100));
    }

    double Exact(double level)
    {
        var (min, max) = Range();
        return Math.Clamp(settings.ToBrightnessCurve().At(level), min, max);
    }

    int Snap(double exact)
    {
        var (min, max) = Range();
        return Math.Clamp((int)Math.Round(exact / Step, MidpointRounding.AwayFromZero) * Step, min, max);
    }

    /// <summary>
    /// The values to write on the way: every multiple of <paramref name="step"/> strictly between the two,
    /// then <paramref name="to"/>. 50→80 is 55, 60 … 80; from a hand-set 54 the first step is 55.
    /// </summary>
    public static IReadOnlyList<int> FadeSteps(int from, int to, int step)
    {
        var values = new List<int>();
        if (to > from)
            for (int v = (from / step + 1) * step; v < to; v += step) values.Add(v);
        else if (to < from)
            for (int v = (from - 1) / step * step; v > to; v -= step) values.Add(v);
        if (to != from) values.Add(to);
        return values;
    }

    double Smooth(DateTime nowUtc, double raw)
    {
        // After a long gap (sleep, camera busy) the old value says nothing about now.
        var maxGap = TimeSpan.FromMinutes(3 * Math.Max(1, settings.BrightnessIntervalMinutes));
        if (_smoothed is not { } s || nowUtc - _smoothedAtUtc > maxGap || nowUtc < _smoothedAtUtc || Math.Abs(raw - s) >= Jump)
            _smoothed = raw;
        else
            _smoothed = s + Smoothing * (raw - s);
        _smoothedAtUtc = nowUtc;
        return _smoothed.Value;
    }

    void Learn(double level, int percent, DateTime nowUtc)
    {
        // Out-of-range values are respected on the monitor but learned at the edge of the range.
        int min = Math.Clamp(settings.BrightnessMin, 0, 100), max = Math.Clamp(settings.BrightnessMax, min, 100);
        var point = new BrightnessPoint(Math.Round(level, 2), Math.Clamp(percent, min, max), nowUtc);
        settings.BrightnessPoints = [.. settings.BrightnessPoints
            .Where(p => Math.Abs(p.Level - level) >= SameLevel)
            .OrderByDescending(p => p.AtUtc)
            .Take(MaxPoints - 1)
            .Append(point)
            .OrderBy(p => p.Level)];
    }
}
