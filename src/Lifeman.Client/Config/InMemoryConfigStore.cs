using System.Collections.Concurrent;

namespace Lifeman.Client.Config;

public sealed class InMemoryConfigStore : IConfigStore
{
    private readonly ConcurrentDictionary<string, string> _store = new();

    public ValueTask<string?> GetAsync(string key, CancellationToken ct = default)
        => new(_store.TryGetValue(key, out var v) ? v : null);

    public ValueTask SetAsync(string key, string value, CancellationToken ct = default)
    {
        _store[key] = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        _store.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }
}
