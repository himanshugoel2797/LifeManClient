using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Lifeman.Client.Windows;

/// Registers the lifeman client exe to launch at user logon via the
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run key. Per-user, no
/// admin required, no service install. Matches CLIENT_DESIGN's "v1:
/// auto-start in user session" choice.
[SupportedOSPlatform("windows")]
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LifemanClient";

    public static void Install(string exePath, string arguments = "run")
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Could not open Run key.");
        var cmd = $"\"{exePath}\" {arguments}".Trim();
        key.SetValue(ValueName, cmd, RegistryValueKind.String);
    }

    public static void Uninstall()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static string? CurrentCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) as string;
    }
}
