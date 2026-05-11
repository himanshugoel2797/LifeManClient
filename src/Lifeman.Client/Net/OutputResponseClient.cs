using Lifeman.Client.Config;

namespace Lifeman.Client.Net;

/// POSTs user action responses back to the kernel.
public sealed class OutputResponseClient
{
    private readonly LifemanHttpClient _client;
    private readonly IConfigStore _config;

    public OutputResponseClient(LifemanHttpClient client, IConfigStore config)
    {
        _client = client;
        _config = config;
    }

    public async Task RespondAsync(string outputId, string actionLabel, string? rawInput = null, CancellationToken ct = default)
    {
        var deviceId = await _config.GetAsync(ConfigKeys.DeviceId, ct).ConfigureAwait(false);
        var channel = deviceId is null ? "" : $"&channel={Uri.EscapeDataString($"device:{deviceId}")}";
        var path = $"api/outputs/{Uri.EscapeDataString(outputId)}/respond?action_label={Uri.EscapeDataString(actionLabel)}{channel}";
        if (!string.IsNullOrEmpty(rawInput))
            path += $"&raw_input={Uri.EscapeDataString(rawInput)}";

        using var resp = await _client.SendAsync(HttpMethod.Post, path, ct: ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }
}
