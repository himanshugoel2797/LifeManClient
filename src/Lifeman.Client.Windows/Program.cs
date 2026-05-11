using System.Diagnostics;
using System.Runtime.Versioning;
using Lifeman.Client.Collectors;
using Lifeman.Client.Config;
using Lifeman.Client.Contracts;
using Lifeman.Client.Health;
using Lifeman.Client.Hosting;
using Lifeman.Client.Net;
using Lifeman.Client.Outbox;
using Lifeman.Client.Updates;
using Lifeman.Client.Windows;
using Lifeman.Client.Windows.Collectors;
using Lifeman.Client.Windows.Config;
using Lifeman.Client.Windows.Logging;
using Lifeman.Client.Windows.Renderers;
using Microsoft.Extensions.Logging;

[assembly: SupportedOSPlatform("windows10.0.19041.0")]

if (args.Length == 0) { PrintUsage(); return 0; }

var stateDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "lifeman", "client");
Directory.CreateDirectory(stateDir);
var logDir = Path.Combine(stateDir, "logs");

var config = new DpapiConfigStore(Path.Combine(stateDir, "config.json"));

using var fileLogProvider = new RollingFileLoggerProvider(logDir);
using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .AddProvider(fileLogProvider)
    .SetMinimumLevel(LogLevel.Information));

using var authHandler = new DeviceTokenHandler(config);
using var http = new HttpClient(authHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
var ctSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; ctSource.Cancel(); };

return args[0] switch
{
    "pair" => await PairAsync(args.Skip(1).ToArray()),
    "run" => await RunAsync(),
    "status" => await StatusAsync(),
    "install-autostart" => InstallAutostart(),
    "uninstall-autostart" => UninstallAutostart(),
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
    Console.WriteLine($"server:    {await config.GetAsync(ConfigKeys.ServerBaseUrl) ?? "(unpaired)"}");
    Console.WriteLine($"device_id: {await config.GetAsync(ConfigKeys.DeviceId) ?? "-"}");
    Console.WriteLine($"name:      {await config.GetAsync(ConfigKeys.DeviceName) ?? "-"}");
    Console.WriteLine($"autostart: {Autostart.CurrentCommand() ?? "(disabled)"}");
    Console.WriteLine($"logs:      {logDir}");
    return 0;
}

async Task<int> RunAsync()
{
    using var single = SingleInstance.TryAcquire();
    if (!single.Acquired)
    {
        Console.Error.WriteLine("another lifeman-client is already running for this user.");
        return 2;
    }

    if (await config.GetAsync(ConfigKeys.DeviceToken) is null)
    {
        Console.Error.WriteLine("no device token — run `pair` first.");
        return 1;
    }
    await using var bundle = ClientHostFactory.Build(
        http, config, loggerFactory,
        rendererFactory: responses => new WindowsToastRenderer(responses),
        // Network collector retunes the uploader's batch profile when
        // connectivity / metering changes — that's why it gets a reference.
        collectorsFactory: uploader => new ICollector[]
        {
            new DesktopPowerCollector(),
            new DesktopActiveWindowCollector(),
            new DesktopIdleCollector(),
            new DesktopNetworkCollector(uploader),
            new DesktopSessionCollector(),
            new DesktopProcessListCollector(),
            new DesktopNotificationCollector(),
            new DesktopScreenCaptureCollector(),
        },
        options: new ClientHostOptions
        {
            OutboxPath = Path.Combine(stateDir, "outbox.db"),
            Platform = "windows",
            CurrentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            UploaderIdlePoll = TimeSpan.FromSeconds(2),
            MeteredByDefault = false,
        });
    RuntimeState.CurrentOutbox = bundle.Outbox;

    var log = loggerFactory.CreateLogger("lifeman-client");
    log.LogInformation("running (collectors: {Count}, logs: {LogDir})", bundle.CollectorCount, logDir);
    Console.Error.WriteLine("[lifeman-client] running. Ctrl+C to stop.");
    try { await bundle.Host.RunAsync(ctSource.Token); }
    catch (OperationCanceledException) when (ctSource.IsCancellationRequested) { }
    log.LogInformation("stopped");
    return 0;
}

int InstallAutostart()
{
    var exe = Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("Could not resolve own exe path.");
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

int Misuse() { PrintUsage(); return 1; }

void PrintUsage() => Console.WriteLine("""
    lifeman-client (Windows)

    usage:
      lifeman-client pair <lifeman://pair?host=…&code=…>
      lifeman-client pair --host <server-url> --code <code>
      lifeman-client run
      lifeman-client status
      lifeman-client install-autostart
      lifeman-client uninstall-autostart
    """);
