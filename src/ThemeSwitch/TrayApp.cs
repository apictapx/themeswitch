using System.Diagnostics;
using Microsoft.Win32;
using ThemeSwitch.Core;

namespace ThemeSwitch;

/// <summary>
/// The notification-area app: left click toggles, right click opens the menu, and a 30-second
/// timer (plus wake-from-sleep and clock-change events) keeps the theme in line with the plan.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    readonly AppHost _host;
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _timer;
    readonly SynchronizationContext _ui;
    readonly Icon _sun = TrayIcons.Sun(), _moon = TrayIcons.Moon();
    readonly ToolStripMenuItem _status, _toggle, _auto, _alwaysLight, _alwaysDark, _autostart;
    long _lastClickTicks;
    bool _refreshing;

    public TrayApp(AppHost host)
    {
        _host = host;

        _status = new ToolStripMenuItem { Enabled = false };
        _toggle = new ToolStripMenuItem("Toggle theme", null, (_, _) => Safely(Toggle));
        _toggle.Font = new Font(_toggle.Font, FontStyle.Bold);
        _auto = new ToolStripMenuItem("Auto (sun + weather)", null, (_, _) => Safely(() => SetMode(ThemeMode.Auto)));
        _alwaysLight = new ToolStripMenuItem("Always Light", null, (_, _) => Safely(() => SetMode(ThemeMode.Light)));
        _alwaysDark = new ToolStripMenuItem("Always Dark", null, (_, _) => Safely(() => SetMode(ThemeMode.Dark)));
        _autostart = new ToolStripMenuItem("Start with Windows", null, (_, _) => Safely(() => Autostart.Set(!Autostart.IsEnabled)))
        {
            Enabled = !host.Options.NoAutostart,
        };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _status, _toggle, new ToolStripSeparator(),
            _auto, _alwaysLight, _alwaysDark, new ToolStripSeparator(),
            _autostart,
            new ToolStripMenuItem($"About ThemeSwitch {AppInfo.Version}", null, (_, _) => OpenRepository()),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        ]);
        menu.Opening += (_, _) => UpdateMenu();

        // Creating the menu installed the WinForms synchronization context; capture it for
        // SystemEvents, which fire on their own thread.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _tray = new NotifyIcon { ContextMenuStrip = menu, Icon = _moon, Text = "ThemeSwitch", Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Safely(OnLeftClick); };

        _timer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _timer.Tick += (_, _) => Evaluate();
        _timer.Start();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnTimeChanged;

        ConfigureAutostart();
        host.Log.Write($"tray started, version {AppInfo.Version}, key {host.Store.KeyPath}");
        Evaluate();
    }

    void ConfigureAutostart()
    {
        if (_host.Options.NoAutostart) return;
        if (!_host.Settings.AutostartConfigured)
        {
            Autostart.Set(true);
            _host.Settings.AutostartConfigured = true;
            _host.Save();
        }
        else
        {
            Autostart.RepairPath();
        }
    }

    // Decide with what we have right away, then refresh stale data and decide again, so a slow
    // network never delays the switch.
    async void Evaluate()
    {
        try
        {
            if (_host.RefreshTimeZone()) _host.Log.Write("re-checking location for the new time zone");
            Tick();
            if (_refreshing || !_host.NeedsRefresh(DateTime.UtcNow)) return;
            _refreshing = true;
            try
            {
                await _host.RefreshAsync(DateTime.UtcNow, force: false, CancellationToken.None);
            }
            finally
            {
                _refreshing = false;
            }
            Tick();
        }
        catch (Exception ex)
        {
            _host.Log.Write($"evaluate failed: {ex}");
        }
    }

    void Tick()
    {
        var overrideBefore = _host.Settings.OverrideUntilUtc;
        var status = _host.Automation.Tick(DateTime.UtcNow, _host.Location, _host.Weather);
        if (status.Applied) _host.Log.Write($"auto -> {status.Current} ({status.Decision?.Reason})");
        if (status.OverrideUntilUtc is { } until && until != overrideBefore)
            _host.Log.Write($"theme changed outside the app -> {status.Current}, holding until {until:u}");
        _host.Save();
        Show(status);
    }

    // Menu and click handlers run straight from the message loop; an exception there would show
    // the WinForms crash dialog, so log it instead (e.g. settings.json briefly locked by a CLI run).
    void Safely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _host.Log.Write($"action failed: {ex}");
        }
    }

    void OnLeftClick()
    {
        // A double-click arrives as two clicks; treat it as one toggle, not two that cancel out.
        long now = Environment.TickCount64;
        if (now - _lastClickTicks < SystemInformation.DoubleClickTime) return;
        _lastClickTicks = now;
        Toggle();
    }

    void Toggle()
    {
        var status = _host.Automation.Toggle(DateTime.UtcNow, _host.Location, _host.Weather);
        _host.Log.Write($"manual -> {status.Current}, override until {status.OverrideUntilUtc:u}");
        _host.Save();
        Show(status);
    }

    void SetMode(ThemeMode mode)
    {
        var status = _host.Automation.SetMode(mode, DateTime.UtcNow, _host.Location, _host.Weather);
        _host.Log.Write($"mode -> {mode}");
        _host.Save();
        Show(status);
    }

    void Show(AutomationStatus status)
    {
        _tray.Icon = status.Current == ThemeChoice.Light ? _sun : _moon;
        string line = StatusText.Line(status, _host.Zone, DateTime.UtcNow);
        _tray.Text = line.Length <= 127 ? line : line[..126] + "…";
        _status.Text = line;
        _toggle.Text = status.Current == ThemeChoice.Light ? "Switch to Dark" : "Switch to Light";
    }

    void UpdateMenu()
    {
        var mode = _host.Settings.Mode;
        _auto.Checked = mode == ThemeMode.Auto;
        _alwaysLight.Checked = mode == ThemeMode.Light;
        _alwaysDark.Checked = mode == ThemeMode.Dark;
        _autostart.Checked = Autostart.IsEnabled;
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _ui.Post(_ => Evaluate(), null);
    }

    void OnTimeChanged(object? sender, EventArgs e) => _ui.Post(_ => Evaluate(), null);

    static void OpenRepository() =>
        Process.Start(new ProcessStartInfo(AppInfo.RepositoryUrl) { UseShellExecute = true });

    protected override void ExitThreadCore()
    {
        _host.Log.Write("tray exiting");
        _timer.Stop();
        _tray.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // SystemEvents are static: unsubscribe or they keep this object alive.
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.TimeChanged -= OnTimeChanged;
            _timer.Dispose();
            _tray.Dispose();
            _sun.Dispose();
            _moon.Dispose();
        }
        base.Dispose(disposing);
    }
}
