using System.Runtime.InteropServices;
using Microsoft.Win32;
using ThemeSwitch.Core;

namespace ThemeSwitch;

/// <summary>
/// Reads and writes the same registry values the Settings app toggles. Any other key path (used by
/// tests) is written the same way but never broadcast, so the real desktop is left alone.
/// </summary>
sealed class RegistryThemeStore(string keyPath, AppSettings settings, bool broadcastInBackground) : IThemeStore
{
    public const string WindowsKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public string KeyPath => keyPath;

    public ThemeChoice? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue("AppsUseLightTheme") is int value
            ? value != 0 ? ThemeChoice.Light : ThemeChoice.Dark
            : null;
    }

    public void Write(ThemeChoice theme)
    {
        int value = theme == ThemeChoice.Light ? 1 : 0;
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue("AppsUseLightTheme", value, RegistryValueKind.DWord);
            if (settings.SwitchSystemTheme) key.SetValue("SystemUsesLightTheme", value, RegistryValueKind.DWord);
        }

        if (!string.Equals(keyPath, WindowsKey, StringComparison.OrdinalIgnoreCase)) return;
        // Explorer watches the key itself; the broadcast makes classic Win32 apps repaint too.
        // It takes ~0.6 s (every top-level window answers), so the tray never waits for it.
        if (broadcastInBackground) _ = Task.Run(Broadcast);
        else Broadcast();
    }

    static void Broadcast() =>
        SendMessageTimeout(HwndBroadcast, WmSettingChange, UIntPtr.Zero, "ImmersiveColorSet", SmtoAbortIfHung, 3000, out _);

    static readonly IntPtr HwndBroadcast = new(0xFFFF);
    const uint WmSettingChange = 0x001A;
    const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
        uint flags, uint timeout, out UIntPtr result);
}
