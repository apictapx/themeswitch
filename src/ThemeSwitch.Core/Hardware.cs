namespace ThemeSwitch.Core;

// Hardware is split in two: a *channel* is how the app talks to a kind of device (Media Foundation for
// webcams, DDC/CI for monitors) and lives in the Windows project; a *profile* is plain data describing one
// vendor's quirks within that channel. A camera or monitor that talks the same way but with different
// numbers needs only a profile; a new way of talking needs a channel. The algorithms that use them are
// here, so they're tested against fakes that behave like awkward hardware.

// ---- cameras --------------------------------------------------------------------------------------

/// <summary>Which part of the frame is averaged.</summary>
public enum FrameRegion { Full, TopHalf }

/// <summary>
/// How to read room light from one kind of webcam. The defaults are a Logitech Brio 500 (UVC exposure in
/// log2 seconds, 30 fps, video-range output); <see cref="Generic"/> uses them for unknown cameras too.
/// </summary>
public sealed record CameraProfile
{
    public string Name { get; init; } = "generic";
    /// <summary>Applies to cameras whose name contains this; empty matches any (the generic profile).</summary>
    public string NameContains { get; init; } = "";

    /// <summary>First exposure tried, in the camera's units (UVC: the time is 2^value seconds).</summary>
    public int StartExposure { get; init; } = -7;
    /// <summary>Longer exposures run into the frame time (1/30 s at 30 fps) and stop adding light.</summary>
    public int LongestExposure { get; init; } = -6;
    /// <summary>Gain is pinned at this fraction of the camera's range while measuring (Brio: 0–255 → 64).</summary>
    public double GainFraction { get; init; } = 0.25;
    public bool BacklightCompensationOff { get; init; } = true;

    /// <summary>Luma of black and white in the camera's output; 16/235 is video range.</summary>
    public double Black { get; init; } = 16;
    public double White { get; init; } = 235;
    /// <summary>Undoes the camera's tone curve. Fitted on a Brio 500: adjacent exposures agree within ~0.3 levels.</summary>
    public double Gamma { get; init; } = 1.6;
    /// <summary>A frame averaging outside this range is re-taken at the next exposure.</summary>
    public double LowLuma { get; init; } = 60;
    public double HighLuma { get; init; } = 200;

    /// <summary>The top half is wall and ceiling for a camera on top of the monitor; the face below is screen-lit.</summary>
    public FrameRegion Region { get; init; } = FrameRegion.TopHalf;
    /// <summary>Frames read after each change before one is measured.</summary>
    public int SettleFrames { get; init; } = 6;
    public int MaxExposureSteps { get; init; } = 6;

    public static readonly CameraProfile Generic = new();
    /// <summary>Measured 2026-10-18; spelled out so a change to the generic defaults can't retune it.</summary>
    public static readonly CameraProfile Brio500 = new()
    {
        Name = "Logitech Brio 500", NameContains = "Brio 500",
        StartExposure = -7, LongestExposure = -6, GainFraction = 0.25, BacklightCompensationOff = true,
        Black = 16, White = 235, Gamma = 1.6, LowLuma = 60, HighLuma = 200,
        Region = FrameRegion.TopHalf, SettleFrames = 6, MaxExposureSteps = 6,
    };
    public static readonly IReadOnlyList<CameraProfile> BuiltIn = [Brio500];

    /// <summary>The first built-in profile whose name pattern matches, else <see cref="Generic"/>.</summary>
    public static CameraProfile For(string cameraName) =>
        BuiltIn.FirstOrDefault(p => p.NameContains.Length > 0 && cameraName.Contains(p.NameContains, StringComparison.OrdinalIgnoreCase))
        ?? Generic;
}

/// <summary>
/// A camera control's value and flags exactly as the channel reported them (DirectShow/UVC: 1 = auto,
/// 2 = manual), so a restore writes back precisely what was there.
/// </summary>
public readonly record struct ControlValue(int Value, int Flags)
{
    public const int AutoFlag = 1, ManualFlag = 2;
    public bool Auto => (Flags & AutoFlag) != 0;
    public static ControlValue Manual(int value) => new(value, ManualFlag);
}

/// <summary>The controls a measurement changes, as they were before; null = the camera doesn't have it.</summary>
public sealed record CameraSettings(ControlValue? Exposure, ControlValue? Gain, ControlValue? BacklightCompensation);

/// <summary>
/// An open webcam, as far as measuring light needs it. Implementations: Media Foundation (any UVC webcam)
/// in the app, fakes in tests.
/// </summary>
public interface ICameraSession : IDisposable
{
    string Name { get; }
    (int Min, int Max) ExposureRange { get; }
    (int Min, int Max)? GainRange { get; }

    /// <summary>The settings as they were when the session was opened, before anything (even the format) changed.</summary>
    CameraSettings Snapshot();
    /// <summary>Puts every control back, value and auto/manual flag. Must not throw.</summary>
    void Restore(CameraSettings settings);

    void SetExposure(int value);
    void SetGain(int value);
    void SetBacklightCompensation(bool on);

    /// <summary>Reads <paramref name="frames"/> frames and returns the average luma (0–255) of the last one's region.</summary>
    double GrabLuma(int frames, FrameRegion region);
}

/// <summary>One measurement: average luma at a known exposure, and the light level it means.</summary>
public sealed record LightSample(double Level, double Luma, int Exposure);

/// <summary>Converts luma at a known exposure into a room light level on a log2 scale: +1 = twice the light.</summary>
public static class LightLevel
{
    /// <param name="exposure">UVC exposure: the time is 2^exposure seconds.</param>
    public static double FromLuma(double meanLuma, int exposure, CameraProfile profile)
    {
        double signal = Math.Clamp((meanLuma - profile.Black) / (profile.White - profile.Black), 0.002, 1);
        return Math.Log2(Math.Pow(signal, profile.Gamma)) - exposure;
    }

    /// <summary>The exposure to try next, or null when this frame is good enough (or the camera can't go further).</summary>
    public static int? NextExposure(int exposure, double meanLuma, int shortest, int longest, CameraProfile profile)
    {
        longest = Math.Min(longest, profile.LongestExposure);
        if (meanLuma < profile.LowLuma && exposure < longest) return exposure + 1;
        if (meanLuma > profile.HighLuma && exposure > shortest) return exposure - 1;
        return null;
    }
}

/// <summary>
/// The measurement: pin gain, turn off backlight compensation, step a manual exposure until the frame is
/// well exposed, and always put the camera back as it was.
/// </summary>
public static class LightMeasurement
{
    public static LightSample Measure(ICameraSession camera, CameraProfile profile)
    {
        var (shortest, longest) = camera.ExposureRange;
        if (shortest > longest)
            throw new NotSupportedException($"{camera.Name} reports an invalid exposure range ({shortest}..{longest})");
        // With auto gain the camera normalises every frame and the level means nothing.
        if (camera.Snapshot().Gain is not null && camera.GainRange is null)
            throw new NotSupportedException($"{camera.Name} has gain but doesn't report its range, so it can't be fixed");

        var before = camera.Snapshot();
        try
        {
            if (camera.GainRange is var (gainMin, gainMax))
                camera.SetGain((int)Math.Round(gainMin + (gainMax - gainMin) * profile.GainFraction));
            if (profile.BacklightCompensationOff && before.BacklightCompensation is not null)
                camera.SetBacklightCompensation(false);

            // Never start past the longest useful exposure (frame time), unless the camera can't go shorter.
            int usableLongest = Math.Max(shortest, Math.Min(longest, profile.LongestExposure));
            int exposure = Math.Clamp(profile.StartExposure, shortest, usableLongest), measuredAt = exposure;
            double luma = 0;
            camera.GrabLuma(profile.SettleFrames, profile.Region); // let the stream start
            for (int step = 0; step < profile.MaxExposureSteps; step++)
            {
                camera.SetExposure(exposure);
                luma = camera.GrabLuma(profile.SettleFrames, profile.Region);
                measuredAt = exposure;
                if (LightLevel.NextExposure(exposure, luma, shortest, longest, profile) is not { } next) break;
                exposure = next;
            }
            return new(LightLevel.FromLuma(luma, measuredAt, profile), luma, measuredAt);
        }
        finally
        {
            camera.Restore(before);
        }
    }
}

// ---- monitors -------------------------------------------------------------------------------------

/// <summary>
/// How to drive one kind of monitor over its control channel. The defaults are MCCS (DDC/CI) codes and the
/// timing a Dell P2423D needs: it silently drops a command that arrives too soon after the previous one.
/// </summary>
public sealed record DisplayProfile
{
    public string Name { get; init; } = "generic";
    /// <summary>Applies to monitors whose name contains this; empty matches any (the generic profile).</summary>
    public string NameContains { get; init; } = "";

    public byte BrightnessCode { get; init; } = 0x10;
    public byte ContrastCode { get; init; } = 0x12;
    /// <summary>Pause before and after every write.</summary>
    public int CommandGapMs { get; init; } = 150;
    /// <summary>Read each write back and repeat it if the monitor didn't take it.</summary>
    public bool VerifyWrites { get; init; } = true;
    public int WriteAttempts { get; init; } = 3;
    /// <summary>When contrast follows brightness, 0–100 % brightness maps onto this contrast range.</summary>
    public int ContrastMin { get; init; }
    public int ContrastMax { get; init; } = 100;

    public static readonly DisplayProfile Generic = new();
    /// <summary>Drops a command sent within ~50 ms of the previous one; spelled out like <see cref="CameraProfile.Brio500"/>.</summary>
    public static readonly DisplayProfile DellP2423D = new()
    {
        Name = "Dell P2423D", NameContains = "P2423D",
        BrightnessCode = 0x10, ContrastCode = 0x12, CommandGapMs = 150, VerifyWrites = true, WriteAttempts = 3,
        ContrastMin = 0, ContrastMax = 100,
    };
    public static readonly IReadOnlyList<DisplayProfile> BuiltIn = [DellP2423D];

    public static DisplayProfile For(string monitorName) =>
        BuiltIn.FirstOrDefault(p => p.NameContains.Length > 0 && monitorName.Contains(p.NameContains, StringComparison.OrdinalIgnoreCase))
        ?? Generic;

    public int ContrastFor(int brightnessPercent) =>
        (int)Math.Round(ContrastMin + (ContrastMax - ContrastMin) * Math.Clamp(brightnessPercent, 0, 100) / 100.0);
}

/// <summary>
/// One monitor's control channel, in the monitor's own units. Implementations: DDC/CI through dxva2 in the
/// app, fakes in tests; other protocols (laptop panels over WMI, network TVs) would be further ones.
/// </summary>
public interface IMonitorChannel
{
    string Name { get; }
    /// <summary>Current and maximum value, or null if the monitor doesn't answer for this code.</summary>
    (int Current, int Max)? Read(byte code);
    /// <returns>False if the channel knows the write failed (it may still be dropped silently).</returns>
    bool Write(byte code, int value);
}

/// <summary>Brightness and contrast in percent over a channel, following the monitor's profile.</summary>
public sealed class DisplayController(IMonitorChannel channel, DisplayProfile profile, Action<int> pause)
{
    public string Name => channel.Name;
    public DisplayProfile Profile => profile;

    public int? ReadBrightness() =>
        channel.Read(profile.BrightnessCode) is var (current, max) && max > 0 ? (int)Math.Round(current * 100.0 / max) : null;

    /// <returns>False if the monitor didn't confirm a value.</returns>
    public bool Write(int brightnessPercent, bool includeContrast)
    {
        bool ok = Set(profile.BrightnessCode, brightnessPercent);
        if (includeContrast) ok &= Set(profile.ContrastCode, profile.ContrastFor(brightnessPercent));
        return ok;
    }

    bool Set(byte code, int percent)
    {
        if (channel.Read(code) is not (_, var max) || max <= 0) return false;
        int raw = (int)Math.Round(Math.Clamp(percent, 0, 100) * max / 100.0);
        for (int attempt = 0; attempt < Math.Max(1, profile.WriteAttempts); attempt++)
        {
            pause(profile.CommandGapMs); // also after the reads just before
            bool sent = channel.Write(code, raw);
            pause(profile.CommandGapMs);
            if (!sent) continue;
            if (!profile.VerifyWrites) return true;
            if (channel.Read(code) is (var now, _) && now == raw) return true;
        }
        return false;
    }
}
