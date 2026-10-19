using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ThemeSwitch.Core;

namespace ThemeSwitch;

/// <summary>One room-light measurement. <see cref="Level"/> is log2: +1 = twice the light.</summary>
sealed record LightReading(double Level, string Source, double? Luma = null, int? Exposure = null);

interface ILightMeter
{
    /// <exception cref="LightMeterException">No camera, camera busy, or capture failed.</exception>
    LightReading Measure();
}

sealed class LightMeterException(string message) : Exception(message);

/// <summary>A fixed level for tests (--light).</summary>
sealed class FixedLightMeter(double level) : ILightMeter
{
    public LightReading Measure() => new(level, "fixed");
}

/// <summary>
/// Measures room light with a webcam: opens it through Media Foundation, picks the camera's profile and
/// runs <see cref="LightMeasurement"/>, which locks exposure for a second and then puts everything back.
/// Frames never leave memory.
/// </summary>
sealed class CameraLightMeter(string nameFilter) : ILightMeter
{
    public LightReading Measure()
    {
        if (CameraInUse() is { } user) throw new LightMeterException($"camera in use by {user}");
        using var camera = MediaFoundationCamera.Open(nameFilter);
        LightSample sample;
        try
        {
            sample = LightMeasurement.Measure(camera, CameraProfile.For(camera.Name));
        }
        catch (NotSupportedException ex)
        {
            throw new LightMeterException(ex.Message);
        }
        return new(sample.Level, camera.Name, sample.Luma, sample.Exposure);
    }

    /// <summary>
    /// Another app holding the camera (a video call) would see its picture jump while exposure is locked,
    /// so skip. Windows records each app's camera use; a start time without a stop time means "now".
    /// </summary>
    static string? CameraInUse()
    {
        const string root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
        foreach (string path in new[] { root, root + @"\NonPackaged" })
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            if (key is null) continue;
            foreach (string app in key.GetSubKeyNames())
            {
                using var entry = key.OpenSubKey(app);
                if (entry?.GetValue("LastUsedTimeStart") is long start && start > 0 && entry.GetValue("LastUsedTimeStop") is 0L)
                    return Path.GetFileName(app.Replace('#', '\\'));
            }
        }
        return null;
    }
}

/// <summary>
/// The Media Foundation channel: any UVC webcam. Exposure, gain and backlight compensation come from
/// IAMCameraControl / IAMVideoProcAmp on the same media source the frames are read from — exposure set
/// from another process or session doesn't stick.
/// </summary>
sealed unsafe class MediaFoundationCamera : ICameraSession
{
    readonly IntPtr _source, _reader, _camera, _amp;
    readonly int _width, _height;
    readonly bool _yuy2;
    readonly CameraSettings _initial;

    public string Name { get; }
    public (int Min, int Max) ExposureRange { get; }
    public (int Min, int Max)? GainRange { get; }

    MediaFoundationCamera(IntPtr source, string name)
    {
        _source = source;
        Name = name;
        var cameraIid = IidCameraControl;
        var ampIid = IidVideoProcAmp;
        try
        {
            Check(Marshal.QueryInterface(source, in cameraIid, out _camera), "exposure control");
            if (Marshal.QueryInterface(source, in ampIid, out _amp) < 0) _amp = IntPtr.Zero;
            ExposureRange = GetRange(_camera, CameraExposure) ?? throw new LightMeterException("camera has no exposure range");
            GainRange = _amp != IntPtr.Zero ? GetRange(_amp, AmpGain) : null;
            // Before the reader and format: some drivers retune exposure and gain when the format changes.
            _initial = new(
                Get(_camera, CameraExposure),
                _amp != IntPtr.Zero ? Get(_amp, AmpGain) : null,
                _amp != IntPtr.Zero ? Get(_amp, AmpBacklight) : null);
            Check(MFCreateSourceReaderFromMediaSource(source, IntPtr.Zero, out _reader), "source reader");
            (_width, _height, _yuy2) = ChooseSmallFormat(_reader);
        }
        catch
        {
            // Open() shuts the source down; release what this constructor took.
            foreach (var p in new[] { _reader, _amp, _camera }) if (p != IntPtr.Zero) Marshal.Release(p);
            throw;
        }
    }

    /// <exception cref="LightMeterException">No matching camera, or it can't be opened.</exception>
    public static MediaFoundationCamera Open(string nameFilter)
    {
        Check(MFStartup(MfVersion, 0), "MFStartup");
        IntPtr source = IntPtr.Zero;
        try
        {
            (source, string name) = Activate(nameFilter);
            return new MediaFoundationCamera(source, name);
        }
        catch
        {
            if (source != IntPtr.Zero)
            {
                Call(source, Slot.MediaSourceShutdown);
                Marshal.Release(source);
            }
            MFShutdown();
            throw;
        }
    }

    public CameraSettings Snapshot() => _initial;

    // A setting that couldn't be read is left to the camera (auto), the safe default for other apps.
    public void Restore(CameraSettings settings)
    {
        SetProp(_camera, CameraExposure, settings.Exposure);
        if (_amp == IntPtr.Zero) return;
        SetProp(_amp, AmpGain, settings.Gain);
        SetProp(_amp, AmpBacklight, settings.BacklightCompensation);
    }

    public void SetExposure(int value) => SetProp(_camera, CameraExposure, ControlValue.Manual(value));

    public void SetGain(int value)
    {
        if (_amp != IntPtr.Zero) SetProp(_amp, AmpGain, ControlValue.Manual(value));
    }

    public void SetBacklightCompensation(bool on)
    {
        if (_amp != IntPtr.Zero) SetProp(_amp, AmpBacklight, ControlValue.Manual(on ? 1 : 0));
    }

    public double GrabLuma(int frames, FrameRegion region)
    {
        var read = (delegate* unmanaged<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Fn(_reader, Slot.ReaderReadSample);
        for (int got = 0, empty = 0; ;)
        {
            uint stream, flags;
            long time;
            IntPtr sample;
            Check(read(_reader, FirstVideoStream, 0, &stream, &flags, &time, &sample), "read frame");
            if ((flags & (ReaderError | ReaderEndOfStream)) != 0)
            {
                if (sample != IntPtr.Zero) Marshal.Release(sample);
                throw new LightMeterException("camera stream ended (unplugged?)");
            }
            if (sample == IntPtr.Zero) // a stream event, not a frame; a camera that only sends these is broken
            {
                if (++empty > 50) throw new LightMeterException("camera sends no frames");
                continue;
            }
            try
            {
                if (++got < frames) continue;
                return Luma(sample, region == FrameRegion.TopHalf ? _height / 2 : _height);
            }
            finally
            {
                Marshal.Release(sample);
            }
        }
    }

    public void Dispose()
    {
        if (_amp != IntPtr.Zero) Marshal.Release(_amp);
        Marshal.Release(_camera);
        Marshal.Release(_reader);
        Call(_source, Slot.MediaSourceShutdown);
        Marshal.Release(_source);
        MFShutdown();
    }

    static (IntPtr Source, string Name) Activate(string nameFilter)
    {
        Check(MFCreateAttributes(out IntPtr attributes, 1), "attributes");
        IntPtr* devices = null;
        uint count = 0;
        try
        {
            var key = SourceTypeKey;
            var value = SourceTypeVideoCapture;
            Check(((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Fn(attributes, Slot.AttributesSetGuid))(attributes, &key, &value), "SetGUID");
            Check(MFEnumDeviceSources(attributes, out devices, out count), "enumerate cameras");

            for (int i = 0; i < count; i++)
            {
                string name = FriendlyName(devices[i]);
                if (nameFilter.Length > 0 && !name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)) continue;
                var iid = IidMediaSource;
                IntPtr source = IntPtr.Zero;
                // The first activation after Frame Server has been idle can fail while the service starts.
                for (int attempt = 0; ; attempt++)
                {
                    int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(devices[i], Slot.ActivateObject))(devices[i], &iid, &source);
                    if (hr >= 0) return (source, name);
                    if (attempt == 1)
                        throw new LightMeterException(string.Create(CultureInfo.InvariantCulture,
                            $"{name} unavailable, maybe in use by another app (0x{hr:X8})"));
                    Thread.Sleep(2000);
                }
            }
            throw new LightMeterException(count == 0 ? "no camera" : $"no camera matching \"{nameFilter}\"");
        }
        finally
        {
            for (int i = 0; i < count; i++) Marshal.Release(devices[i]);
            if (devices != null) Marshal.FreeCoTaskMem((IntPtr)devices);
            Marshal.Release(attributes);
        }
    }

    static string FriendlyName(IntPtr activate)
    {
        var key = FriendlyNameKey;
        char* text;
        uint length;
        if (((delegate* unmanaged<IntPtr, Guid*, char**, uint*, int>)Fn(activate, Slot.AttributesGetAllocatedString))(activate, &key, &text, &length) < 0)
            return "camera";
        string name = new(text, 0, (int)length);
        Marshal.FreeCoTaskMem((IntPtr)text);
        return name;
    }

    /// <summary>The smallest uncompressed format at least 320 wide: plenty for an average, cheap to read.</summary>
    static (int Width, int Height, bool Yuy2) ChooseSmallFormat(IntPtr reader)
    {
        IntPtr best = IntPtr.Zero;
        int bestWidth = 0, bestHeight = 0;
        bool bestYuy2 = false;
        var subtypeKey = SubtypeKey;
        var sizeKey = FrameSizeKey;
        for (uint i = 0; ; i++)
        {
            IntPtr type;
            if (((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Fn(reader, Slot.ReaderGetNativeMediaType))(reader, FirstVideoStream, i, &type) < 0) break;
            Guid subtype;
            ulong size;
            ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Fn(type, Slot.AttributesGetGuid))(type, &subtypeKey, &subtype);
            ((delegate* unmanaged<IntPtr, Guid*, ulong*, int>)Fn(type, Slot.AttributesGetUInt64))(type, &sizeKey, &size);
            int width = (int)(size >> 32), height = (int)(uint)size;
            bool usable = (subtype == Yuy2 || subtype == Nv12) && width >= 320;
            if (usable && (best == IntPtr.Zero || width * height < bestWidth * bestHeight))
            {
                if (best != IntPtr.Zero) Marshal.Release(best);
                (best, bestWidth, bestHeight, bestYuy2) = (type, width, height, subtype == Yuy2);
            }
            else
            {
                Marshal.Release(type);
            }
        }
        if (best == IntPtr.Zero) throw new LightMeterException("camera offers no YUY2/NV12 format");
        try
        {
            Check(((delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, int>)Fn(reader, Slot.ReaderSetCurrentMediaType))(reader, FirstVideoStream, IntPtr.Zero, best), "set format");
        }
        finally
        {
            Marshal.Release(best);
        }
        return (bestWidth, bestHeight, bestYuy2);
    }

    /// <summary>Average luma of the first <paramref name="rows"/> rows.</summary>
    double Luma(IntPtr sample, int rows)
    {
        IntPtr buffer;
        Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(sample, Slot.SampleConvertToContiguousBuffer))(sample, &buffer), "frame buffer");
        try
        {
            byte* data;
            uint maxLength, length;
            Check(((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)Fn(buffer, Slot.BufferLock))(buffer, &data, &maxLength, &length), "lock frame");
            try
            {
                // YUY2 interleaves Y U Y V; NV12 starts with the whole Y plane.
                int pixels = _width * rows, stride = _yuy2 ? 2 : 1;
                if ((long)pixels * stride > length) throw new LightMeterException("short frame");
                long sum = 0;
                for (int i = 0; i < pixels; i++) sum += data[i * stride];
                return (double)sum / pixels;
            }
            finally
            {
                ((delegate* unmanaged<IntPtr, int>)Fn(buffer, Slot.BufferUnlock))(buffer);
            }
        }
        finally
        {
            Marshal.Release(buffer);
        }
    }

    // ---- COM plumbing: Media Foundation interfaces called through their vtables -----------------

    /// <summary>Vtable indexes (IUnknown is 0-2, IMFAttributes 3-32).</summary>
    static class Slot
    {
        public const int AttributesGetUInt64 = 8, AttributesGetGuid = 10, AttributesGetAllocatedString = 13, AttributesSetGuid = 24;
        public const int ActivateObject = 33;                       // IMFActivate
        public const int MediaSourceShutdown = 12;                  // IMFMediaSource
        public const int ReaderGetNativeMediaType = 5, ReaderSetCurrentMediaType = 7, ReaderReadSample = 9; // IMFSourceReader
        public const int SampleConvertToContiguousBuffer = 41;      // IMFSample
        public const int BufferLock = 3, BufferUnlock = 4;          // IMFMediaBuffer
        public const int PropGetRange = 3, PropSet = 4, PropGet = 5; // IAMCameraControl, IAMVideoProcAmp
    }

    const uint MfVersion = 0x00020070;
    const uint FirstVideoStream = 0xFFFFFFFC;
    const uint ReaderError = 0x1, ReaderEndOfStream = 0x2;
    const int CameraExposure = 4, AmpBacklight = 8, AmpGain = 9;

    static readonly Guid IidMediaSource = new("279a808d-aec7-40c8-9c6b-a6b492c78a66");
    static readonly Guid IidCameraControl = new("c6e13370-30ac-11d0-a18c-00a0c9118956");
    static readonly Guid IidVideoProcAmp = new("c6e13360-30ac-11d0-a18c-00a0c9118956");
    static readonly Guid SourceTypeKey = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    static readonly Guid SourceTypeVideoCapture = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    static readonly Guid FriendlyNameKey = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    static readonly Guid SubtypeKey = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid FrameSizeKey = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid Yuy2 = new("32595559-0000-0010-8000-00aa00389b71");
    static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");

    static IntPtr Fn(IntPtr obj, int slot) => (*(IntPtr**)obj)[slot];

    static void Call(IntPtr obj, int slot) => ((delegate* unmanaged<IntPtr, int>)Fn(obj, slot))(obj);

    static (int Min, int Max)? GetRange(IntPtr control, int property)
    {
        int lo, hi, step, def, caps;
        return ((delegate* unmanaged<IntPtr, int, int*, int*, int*, int*, int*, int>)Fn(control, Slot.PropGetRange))(control, property, &lo, &hi, &step, &def, &caps) >= 0
            ? (lo, hi)
            : null;
    }

    static ControlValue? Get(IntPtr control, int property)
    {
        int value, flags;
        return ((delegate* unmanaged<IntPtr, int, int*, int*, int>)Fn(control, Slot.PropGet))(control, property, &value, &flags) >= 0
            ? new ControlValue(value, flags)
            : null;
    }

    static void SetProp(IntPtr control, int property, ControlValue? value)
    {
        var (v, flags) = value is { } c ? (c.Value, c.Flags) : (0, ControlValue.AutoFlag);
        ((delegate* unmanaged<IntPtr, int, int, int, int>)Fn(control, Slot.PropSet))(control, property, v, flags);
    }

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new LightMeterException(string.Create(CultureInfo.InvariantCulture, $"{what} failed (0x{hr:X8})"));
    }

    [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] static extern int MFShutdown();
    [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IntPtr attributes, uint size);
    [DllImport("mf.dll")] static extern int MFEnumDeviceSources(IntPtr attributes, out IntPtr* devices, out uint count);
    [DllImport("mfreadwrite.dll")] static extern int MFCreateSourceReaderFromMediaSource(IntPtr source, IntPtr attributes, out IntPtr reader);
}
