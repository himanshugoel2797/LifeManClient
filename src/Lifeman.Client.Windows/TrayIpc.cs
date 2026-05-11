using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace Lifeman.Client.Windows;

/// Tiny named-pipe channel from a second invocation (e.g. `lifeman://`
/// URL click) to the running tray instance. Wire format is one UTF-8
/// line per message: `pair <lifeman://…>`.
///
/// Per-user pipe name so multi-user boxes get isolated channels;
/// matches `SingleInstance`'s scoping.
[SupportedOSPlatform("windows")]
public static class TrayIpc
{
    private static string PipeName
    {
        get
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            return $"lifeman-tray-{sid}";
        }
    }

    /// Start a background listener. `onMessage` runs on a threadpool
    /// thread; the caller is responsible for marshalling to the UI
    /// thread if needed. Stops when `ct` cancels.
    public static Task StartListenerAsync(Func<string, Task> onMessage, CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(line))
                        await onMessage(line).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch
                {
                    // Pipe instance got into a bad state — recreate.
                    try { await Task.Delay(500, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }, ct);
    }

    /// Send a single line to the running tray. Returns true if the
    /// message reached the server within `timeout`; false if no tray
    /// is listening.
    public static bool TrySend(string message, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.Out, PipeOptions.None);
            client.Connect((int)timeout.TotalMilliseconds);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(message);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
