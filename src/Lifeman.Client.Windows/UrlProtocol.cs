using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Lifeman.Client.Windows;

/// Registers `lifeman://` as a Windows URL scheme handler pointing at
/// the local client exe. Per-user (HKCU\Software\Classes) so no admin
/// is needed; clicking a `lifeman://pair?host=…&code=…` link in the
/// server's loopback web UI then dispatches to our running tray.
[SupportedOSPlatform("windows")]
public static class UrlProtocol
{
    public const string Scheme = "lifeman";
    private const string BaseKey = @"Software\Classes\" + Scheme;

    public static void Register(string exePath)
    {
        // Top-level: scheme key + URL Protocol marker value.
        using (var root = Registry.CurrentUser.CreateSubKey(BaseKey, writable: true)
            ?? throw new InvalidOperationException("Could not create scheme key."))
        {
            root.SetValue("", "URL:Lifeman pairing handler");
            root.SetValue("URL Protocol", "");
        }

        // shell\open\command — the actual launcher line. %1 is the URL.
        using var cmd = Registry.CurrentUser.CreateSubKey(BaseKey + @"\shell\open\command", writable: true)
            ?? throw new InvalidOperationException("Could not create command key.");
        cmd.SetValue("", $"\"{exePath}\" \"%1\"");
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(BaseKey, throwOnMissingSubKey: false);
    }

    public static string? CurrentCommand()
    {
        using var cmd = Registry.CurrentUser.OpenSubKey(BaseKey + @"\shell\open\command", writable: false);
        return cmd?.GetValue("") as string;
    }
}
