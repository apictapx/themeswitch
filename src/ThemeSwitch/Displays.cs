using System.Globalization;
using System.Runtime.InteropServices;

namespace ThemeSwitch;

/// <summary>Monitor brightness (and optionally contrast) in percent. The app uses DDC/CI; tests use a file.</summary>
interface IBrightnessDisplay
{
    /// <summary>Names of the monitors that would be controlled, for reports.</summary>
    IReadOnlyList<string> Names();
    /// <summary>Brightness of the first controlled monitor, or null if none answers (asleep, unplugged, no DDC/CI).</summary>
    int? Read();
    /// <returns>False if any monitor didn't confirm the value.</returns>
    bool Write(int percent, bool includeContrast);
}

/// <summary>
/// External monitors over DDC/CI (the monitor's own control channel on the video cable), through the
/// Monitor Configuration API in dxva2.dll. Monitors that don't answer (TVs, most laptop panels) are skipped.
/// </summary>
sealed class DdcDisplays(string nameFilter) : IBrightnessDisplay
{
    const byte Brightness = 0x10, Contrast = 0x12;
    /// <summary>DDC/CI wants a gap between commands; a Dell P2423D silently drops a write that comes too soon.</summary>
    const int CommandGapMs = 150;

    public IReadOnlyList<string> Names()
    {
        var names = new List<string>();
        Each((_, name) => names.Add(name));
        return names;
    }

    public int? Read()
    {
        int? value = null;
        Each((handle, _) => value ??= Get(handle, Brightness));
        return value;
    }

    public bool Write(int percent, bool includeContrast)
    {
        bool ok = true;
        Each((handle, _) =>
        {
            ok &= Set(handle, Brightness, percent);
            if (includeContrast) ok &= Set(handle, Contrast, percent);
        });
        return ok;
    }

    /// <summary>Runs <paramref name="action"/> for every matching monitor that answers DDC/CI, then releases the handles.</summary>
    void Each(Action<IntPtr, string> action)
    {
        var monitors = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) => { monitors.Add(h); return true; }, IntPtr.Zero);
        foreach (var monitor in monitors)
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint count) || count == 0) continue;
            var physical = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical)) continue;
            try
            {
                foreach (var p in physical)
                {
                    if (nameFilter.Length > 0 && !p.Description.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Get(p.Handle, Brightness) is null) continue;
                    action(p.Handle, p.Description);
                }
            }
            finally
            {
                DestroyPhysicalMonitors(count, physical);
            }
        }
    }

    /// <summary>The value as a percentage of the monitor's own maximum (usually 100 already).</summary>
    static int? Get(IntPtr handle, byte code) =>
        GetVCPFeatureAndVCPFeatureReply(handle, code, IntPtr.Zero, out uint current, out uint max) && max > 0
            ? (int)Math.Round(current * 100.0 / max)
            : null;

    static bool Set(IntPtr handle, byte code, int percent)
    {
        if (!GetVCPFeatureAndVCPFeatureReply(handle, code, IntPtr.Zero, out _, out uint max) || max == 0) return false;
        uint raw = (uint)Math.Round(Math.Clamp(percent, 0, 100) * max / 100.0);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Thread.Sleep(CommandGapMs); // also after the reads just before
            SetVCPFeature(handle, code, raw);
            Thread.Sleep(CommandGapMs);
            if (GetVCPFeatureAndVCPFeatureReply(handle, code, IntPtr.Zero, out uint now, out _) && now == raw) return true;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("dxva2.dll")]
    static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll")]
    static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")]
    static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")]
    static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, IntPtr type, out uint current, out uint max);
    [DllImport("dxva2.dll")]
    static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
}

/// <summary>A pretend monitor for tests (--monitor-file): the file holds "brightness contrast".</summary>
sealed class FileDisplay(string path) : IBrightnessDisplay
{
    public IReadOnlyList<string> Names() => ["Test monitor"];

    public int? Read() =>
        File.Exists(path) && int.TryParse(File.ReadAllText(path).Split(' ')[0], CultureInfo.InvariantCulture, out int v) ? v : null;

    public bool Write(int percent, bool includeContrast)
    {
        string contrast = includeContrast || !File.Exists(path) ? $"{percent}" : File.ReadAllText(path).Split(' ').ElementAtOrDefault(1) ?? "50";
        File.WriteAllText(path, $"{percent} {contrast}");
        return true;
    }
}
