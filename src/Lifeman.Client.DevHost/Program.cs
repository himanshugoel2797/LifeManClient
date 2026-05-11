using Lifeman.Client.Collectors;
using Lifeman.Client.Config;
using Lifeman.Client.Contracts;
using Lifeman.Client.DevHost;
using Lifeman.Client.Hosting;
using Lifeman.Client.Net;
using Lifeman.Client.Outbox;
using Microsoft.Extensions.Logging;

if (args.Length == 0) { PrintUsage(); return 0; }

var stateDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "lifeman", "devhost");
Directory.CreateDirectory(stateDir);

var config = new FileConfigStore(Path.Combine(stateDir, "config.json"));
Console.Error.WriteLine($"[devhost] state dir: {stateDir}");
Console.Error.WriteLine("[devhost] WARNING: this dev host stores the device token unencrypted. Use only on trusted machines.");

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    })
    .SetMinimumLevel(LogLevel.Information));

using var http = new HttpClient(new DeviceTokenHandler(config)) { Timeout = TimeSpan.FromSeconds(30) };
var ctSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; ctSource.Cancel(); };

switch (args[0])
{
    case "pair":
        return await PairAsync(args.Skip(1).ToArray());
    case "run":
        return await RunAsync();
    case "status":
        return await StatusAsync();
    default:
        PrintUsage();
        return 1;
}

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
        serverUrl = host;
        code = c;
    }

    var pairing = new PairingClient(http, config);
    var capabilities = new DeviceCapabilities(
        RichContent: true,
        Images: false,
        Actions: true,
        Persistence: true,
        InterruptionLevel: "foreground",
        TypicalLatencyMs: 1000);
    var name = $"devhost-{Environment.MachineName}";
    var resp = await pairing.PairAsync(serverUrl, code, name, "windows", capabilities, ctSource.Token);
    Console.WriteLine($"paired: device_id={resp.DeviceId} name={resp.Name}");
    return 0;
}

async Task<int> StatusAsync()
{
    var url = await config.GetAsync(ConfigKeys.ServerBaseUrl);
    var id = await config.GetAsync(ConfigKeys.DeviceId);
    var name = await config.GetAsync(ConfigKeys.DeviceName);
    Console.WriteLine($"server:    {url ?? "(unpaired)"}");
    Console.WriteLine($"device_id: {id ?? "-"}");
    Console.WriteLine($"name:      {name ?? "-"}");
    return 0;
}

async Task<int> RunAsync()
{
    if (await config.GetAsync(ConfigKeys.DeviceToken) is null)
    {
        Console.Error.WriteLine("no device token — run `pair` first.");
        return 1;
    }

    await using var bundle = ClientHostFactory.Build(
        http, config, loggerFactory,
        rendererFactory: _ => new ConsoleRenderer(),
        collectorsFactory: _ => Array.Empty<ICollector>(),
        options: new ClientHostOptions
        {
            OutboxPath = Path.Combine(stateDir, "outbox.db"),
            Platform = "devhost",
            CurrentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            UploaderIdlePoll = TimeSpan.FromSeconds(2),
            HeartbeatInterval = TimeSpan.FromSeconds(30),
            MeteredByDefault = false,
            EnableUpdateChecker = false,
        });

    Console.Error.WriteLine("[devhost] running. Ctrl+C to stop.");
    try
    {
        await bundle.Host.RunAsync(ctSource.Token);
    }
    catch (OperationCanceledException) when (ctSource.IsCancellationRequested)
    {
        // graceful shutdown
    }
    return 0;
}

void PrintUsage()
{
    Console.WriteLine("""
        lifeman-devhost — console smoke-test for the lifeman client core

        usage:
          lifeman-devhost pair <lifeman://pair?host=…&code=…>
          lifeman-devhost pair --host <server-url> --code <code>
          lifeman-devhost run
          lifeman-devhost status
        """);
}
