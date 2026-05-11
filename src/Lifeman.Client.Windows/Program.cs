using System.Diagnostics;
using System.Runtime.Versioning;
using Lifeman.Client.Config;
using Lifeman.Client.Contracts;
using Lifeman.Client.Net;
using Lifeman.Client.Windows;
using Lifeman.Client.Windows.Config;

[assembly: SupportedOSPlatform("windows10.0.19041.0")]

// Project builds as `WinExe` so autostart doesn't flash a console.
// CLI subcommands attach to the parent terminal explicitly.
var stateDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "lifeman", "client");
Directory.CreateDirectory(stateDir);
var logDir = Path.Combine(stateDir, "logs");

// No-arg, `run`, or a `lifeman://` URL → tray mode (or forward-to-tray).
if (args.Length == 0 || args[0] == "run")
{
    return TrayApp.Run(stateDir, logDir);
}

if (args[0].StartsWith("lifeman://", StringComparison.OrdinalIgnoreCase))
{
    // URL handler invocation. Forward to the running tray; if none is
    // running, start one and let it pair on first-run.
    if (TrayIpc.TrySend("pair " + args[0], TimeSpan.FromSeconds(3)))
        return 0;
    // No tray yet — launch ourselves with `run` and seed the same URL
    // into the new instance's pipe via a small handoff loop. Simplest:
    // just call PerformPair inline using a temp HttpClient, then start
    // the tray.
    return await CliPairAndRunAsync(args[0]);
}

// CLI subcommands → attach to parent console for stdout/stderr.
ConsoleAttach.AttachToParent();
var config = new DpapiConfigStore(Path.Combine(stateDir, "config.json"));
using var authHandler = new DeviceTokenHandler(config);
using var http = new HttpClient(authHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
var ctSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; ctSource.Cancel(); };

return args[0] switch
{
    "pair" => await PairAsync(args.Skip(1).ToArray()),
    "status" => await StatusAsync(),
    "install-autostart" => InstallAutostart(),
    "uninstall-autostart" => UninstallAutostart(),
    "register-url-handler" => RegisterUrlHandler(),
    "unregister-url-handler" => UnregisterUrlHandler(),
    _ => Misuse(),
};

async Task<int> PairAsync(string[] rest)
{
    if (rest.Length == 0) { Console.Error.WriteLine("usage: pair <lifeman://pair?...|--host <url> --code <code>>"); return 1; }
    string serverUrl, code;
    if (rest[0].StartsWith("lifeman://", StringComparison.OrdinalIgnoreCase))
    {
        (serverUrl, code) = PairingClient.ParsePairUrl(rest[0]);
    }
    else
    {
        string? host = null, c = null;
        for (var i = 0; i < rest.Length - 1; i++)
        {
            if (rest[i] == "--host") host = rest[i + 1];
            if (rest[i] == "--code") c = rest[i + 1];
        }
        if (host is null || c is null) { Console.Error.WriteLine("missing --host or --code"); return 1; }
        serverUrl = host; code = c;
    }

    var pairing = new PairingClient(http, config);
    var capabilities = new DeviceCapabilities(
        RichContent: true, Images: false, Actions: true, Persistence: true,
        InterruptionLevel: "foreground", TypicalLatencyMs: 1000);
    var name = $"win-{Environment.MachineName}";
    var resp = await pairing.PairAsync(serverUrl, code, name, "windows", capabilities, ctSource.Token);
    Console.WriteLine($"paired: device_id={resp.DeviceId} name={resp.Name}");
    return 0;
}

async Task<int> StatusAsync()
{
    Console.WriteLine($"server:      {await config.GetAsync(ConfigKeys.ServerBaseUrl) ?? "(unpaired)"}");
    Console.WriteLine($"device_id:   {await config.GetAsync(ConfigKeys.DeviceId) ?? "-"}");
    Console.WriteLine($"name:        {await config.GetAsync(ConfigKeys.DeviceName) ?? "-"}");
    Console.WriteLine($"autostart:   {Autostart.CurrentCommand() ?? "(disabled)"}");
    Console.WriteLine($"url handler: {UrlProtocol.CurrentCommand() ?? "(not registered)"}");
    Console.WriteLine($"logs:        {logDir}");
    Console.WriteLine();
    Console.WriteLine("(open the tray's Status… for outbox depth, last upload, per-collector health)");
    return 0;
}

int InstallAutostart()
{
    var exe = ResolveExe();
    Autostart.Install(exe);
    Console.WriteLine($"autostart installed: {Autostart.CurrentCommand()}");
    return 0;
}

int UninstallAutostart()
{
    Autostart.Uninstall();
    Console.WriteLine("autostart removed.");
    return 0;
}

int RegisterUrlHandler()
{
    UrlProtocol.Register(ResolveExe());
    Console.WriteLine($"lifeman:// handler registered: {UrlProtocol.CurrentCommand()}");
    return 0;
}

int UnregisterUrlHandler()
{
    UrlProtocol.Unregister();
    Console.WriteLine("lifeman:// handler removed.");
    return 0;
}

string ResolveExe() =>
    Environment.ProcessPath
    ?? Process.GetCurrentProcess().MainModule?.FileName
    ?? throw new InvalidOperationException("Could not resolve own exe path.");

async Task<int> CliPairAndRunAsync(string url)
{
    // Fallback when invoked from a URL click while no tray is running:
    // do a one-shot pair (best-effort, GUI), then start the tray.
    try
    {
        var (serverUrl, code) = PairingClient.ParsePairUrl(url);
        var c = new DpapiConfigStore(Path.Combine(stateDir, "config.json"));
        using var ah = new DeviceTokenHandler(c);
        using var h = new HttpClient(ah, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        var pairing = new PairingClient(h, c);
        var capabilities = new DeviceCapabilities(
            RichContent: true, Images: false, Actions: true, Persistence: true,
            InterruptionLevel: "foreground", TypicalLatencyMs: 1000);
        await pairing.PairAsync(serverUrl, code, $"win-{Environment.MachineName}", "windows", capabilities, default);
    }
    catch
    {
        // Tray will prompt the user to re-pair via the menu if this
        // failed — falling through to start the tray either way.
    }
    return TrayApp.Run(stateDir, logDir);
}

int Misuse() { PrintUsage(); return 1; }

void PrintUsage() => Console.WriteLine("""
    lifeman-client (Windows)

    Run with no args — or `run` — to launch the tray agent.
    A `lifeman://pair?...` URL launches the tray and pairs.

    CLI subcommands:
      lifeman-client pair <lifeman://pair?host=…&code=…>
      lifeman-client pair --host <server-url> --code <code>
      lifeman-client status
      lifeman-client install-autostart
      lifeman-client uninstall-autostart
      lifeman-client register-url-handler
      lifeman-client unregister-url-handler
    """);
