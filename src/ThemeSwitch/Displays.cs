using System.Globalization;
using System.Runtime.InteropServices;
using ThemeSwitch.Core;

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
/// External monitors over DDC/CI (the monitor's own control channel on the video cable). Each monitor is
/// driven through <see cref="DisplayController"/> with its <see cref="DisplayProfile"/>; monitors that
/// don't answer (TVs, most laptop panels) are skipped.
/// </summary>
sealed class DdcDisplays(string nameFilter) : IBrightnessDisplay
{
    public IReadOnlyList<string> Names()
    {
        var names = new List<string>();
        Each(c => names.Add(c.Name));
        return names;
    }

    public int? Read()
    {
        int? value = null;
        Each(c => value ??= c.ReadBrightness());
        return value;
    }

    public bool Write(int percent, bool includeContrast)
    {
        bool ok = true;
        Each(c => ok &= c.Write(percent, includeContrast));
        return ok;
    }

    /// <summary>Runs <paramref name="action"/> for every matching monitor that answers, then releases the handles.</summary>
    void Each(Action<DisplayController> action)
    {
        foreach (var monitor in Dxva2DdcChannel.Monitors())
        {
            using (monitor)
            {
                foreach (var channel in monitor.Channels)
                {
                    if (nameFilter.Length > 0 && !channel.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    var controller = new DisplayController(channel, DisplayProfile.For(channel.Name), Thread.Sleep);
                    if (controller.ReadBrightness() is null) continue;
                    action(controller);
                }
            }
        }
    }
}

/// <summary>The DDC/CI channel: VCP codes through the Monitor Configuration API in dxva2.dll.</summary>
sealed class Dxva2DdcChannel(IntPtr handle, string name) : IMonitorChannel
{
    public string Name => name;

    public (int Current, int Max)? Read(byte code) =>
        GetVCPFeatureAndVCPFeatureReply(handle, code, IntPtr.Zero, out uint current, out uint max) ? ((int)current, (int)max) : null;

    public bool Write(byte code, int value) => SetVCPFeature(handle, code, (uint)Math.Max(0, value));

    /// <summary>The physical monitors behind one display; disposing releases their handles.</summary>
    public sealed class Group(PhysicalMonitor[] physical) : IDisposable
    {
        public IEnumerable<Dxva2DdcChannel> Channels => physical.Select(p => new Dxva2DdcChannel(p.Handle, p.Description));
        public void Dispose() => DestroyPhysicalMonitors((uint)physical.Length, physical);
    }

    public static IEnumerable<Group> Monitors()
    {
        var displays = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) => { displays.Add(h); return true; }, IntPtr.Zero);
        foreach (var display in displays)
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(display, out uint count) || count == 0) continue;
            var physical = new PhysicalMonitor[count];
            if (GetPhysicalMonitorsFromHMONITOR(display, count, physical)) yield return new Group(physical);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PhysicalMonitor
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
