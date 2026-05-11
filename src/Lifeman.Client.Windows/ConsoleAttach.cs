using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lifeman.Client.Windows;

/// Project is built as `WinExe` so autostart doesn't flash a console
/// window. CLI subcommands (pair, status, …) still want stdout/stderr,
/// so when one is invoked we attach to the parent process's console
/// (the terminal that ran `lifeman-client …`) and reopen the managed
/// Console streams onto it. Best-effort — if no parent console exists
/// (e.g. launched from explorer / URL handler), output is dropped.
[SupportedOSPlatform("windows")]
public static class ConsoleAttach
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    public static void AttachToParent()
    {
        if (!AttachConsole(ATTACH_PARENT_PROCESS)) return;
        try
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetError(stderr);
        }
        catch
        {
            // Reopening can fail if the parent already closed; tolerate it.
        }
    }

    /// Press-enter prompt + newline after CLI subcommands so the
    /// parent shell's prompt doesn't overlap our last line. Only
    /// useful when AttachToParent succeeded.
    public static void Detach() => FreeConsole();
}
