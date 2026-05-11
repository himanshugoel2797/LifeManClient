using Lifeman.Client.Collectors;
using Lifeman.Client.Config;
using Lifeman.Client.Health;
using Lifeman.Client.Net;
using Lifeman.Client.Outbox;
using Lifeman.Client.Renderers;
using Lifeman.Client.Updates;
using Microsoft.Extensions.Logging;

namespace Lifeman.Client.Hosting;

/// Per-host knobs that vary by platform; everything else is shared.
public sealed class ClientHostOptions
{
    public required string OutboxPath { get; init; }
    public required string Platform { get; init; }    // "windows" / "android" / "devhost"
    public required string CurrentVersion { get; init; }
    public bool MeteredByDefault { get; init; }
    public TimeSpan UploaderIdlePoll { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMinutes(5);
    /// Enable polling /api/system/client-updates/<platform>. DevHost
    /// turns this off — it's developer-built so update checks are noise.
    public bool EnableUpdateChecker { get; init; } = true;
}

/// Wires the shared core pieces together. Each head supplies its
/// IConfigStore, HttpClient (already wrapped in DeviceTokenHandler),
/// IRenderer factory, and ICollector factory; the factory owns
/// the SqliteOutbox / SqliteHealthStore / LifemanHttpClient / Uploader
/// / SseReceiver / OutputResponseClient / UpdateChecker wiring that was
/// duplicated across the three program entry points.
public sealed class ClientHostFactory
{
    /// Build a fully-wired host. The caller owns `http`, `config`,
    /// and `loggerFactory` lifetimes — the factory does not dispose
    /// them. The returned bundle owns the outbox and host.
    ///
    /// The outbox is constructed but not initialised — `host.RunAsync`
    /// will call `outbox.InitAsync` on its first pass, matching the
    /// pre-refactor behaviour.
    public static ClientHostBundle Build(
        HttpClient http,
        IConfigStore config,
        ILoggerFactory loggerFactory,
        Func<OutputResponseClient, IRenderer> rendererFactory,
        Func<Uploader, IReadOnlyList<ICollector>> collectorsFactory,
        ClientHostOptions options)
    {
        var outbox = new SqliteOutbox(options.OutboxPath);
        var health = new SqliteHealthStore(options.OutboxPath);
        var lifemanHttp = new LifemanHttpClient(http, config);
        var uploader = new Uploader(outbox, lifemanHttp, config,
            options: new UploaderOptions { IdlePollInterval = options.UploaderIdlePoll },
            logger: loggerFactory.CreateLogger<Uploader>());
        uploader.SetNetworkProfile(isMetered: options.MeteredByDefault);

        var sse = new SseReceiver(lifemanHttp, config,
            logger: loggerFactory.CreateLogger<SseReceiver>());
        var responses = new OutputResponseClient(lifemanHttp, config);
        var renderer = rendererFactory(responses);

        var platformCollectors = collectorsFactory(uploader);
        var collectors = new List<ICollector>(platformCollectors.Count + 1)
        {
            new HeartbeatCollector(options.HeartbeatInterval),
        };
        collectors.AddRange(platformCollectors);

        UpdateChecker? updates = null;
        if (options.EnableUpdateChecker)
        {
            updates = new UpdateChecker(lifemanHttp, renderer, options.Platform, options.CurrentVersion,
                logger: loggerFactory.CreateLogger<UpdateChecker>());
        }

        var host = new LifemanClientHost(outbox, uploader, sse, renderer, collectors,
            loggerFactory.CreateLogger<LifemanClientHost>(),
            health: health,
            updates: updates);

        return new ClientHostBundle(host, outbox, collectors.Count);
    }
}

/// Owned-resource bundle returned from the factory. Disposing this
/// tears down the host first, then the outbox — order matters because
/// the host still holds the outbox reference.
public sealed class ClientHostBundle : IAsyncDisposable
{
    public LifemanClientHost Host { get; }
    private readonly SqliteOutbox _outbox;

    /// Total collector count including the heartbeat. Useful for the
    /// head's startup log line.
    public int CollectorCount { get; }

    internal ClientHostBundle(LifemanClientHost host, SqliteOutbox outbox, int collectorCount)
    {
        Host = host;
        _outbox = outbox;
        CollectorCount = collectorCount;
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync().ConfigureAwait(false);
        await _outbox.DisposeAsync().ConfigureAwait(false);
    }

    public IOutbox Outbox => _outbox;
}
