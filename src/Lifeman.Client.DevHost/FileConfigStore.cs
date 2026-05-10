using System.Collections.Concurrent;
using System.Text.Json;
using Lifeman.Client.Config;

namespace Lifeman.Client.DevHost;

/// File-backed config store for the dev console host. Stores values in a
/// plain JSON file under the user's app-data dir. **Not** encrypted — that's
/// what the MAUI platform heads do via DPAPI / Android Keystore. The dev
/// host is for local kernel-loop testing only; never ship this on a phone.
public sealed class FileConfigStore : IConfigStore
{
    private readonly string _path;
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;

    public FileConfigStore(string path) => _path = path;

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            if (File.Exists(_path))
            {
                var json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                foreach (var kv in dict) _cache[kv.Key] = kv.Value;
            }
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(_cache.ToDictionary(k => k.Key, v => v.Value), new JsonSerializerOptions { WriteIndented = true });
        var tmp = _path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
        File.Move(tmp, _path, overwrite: true);
    }

    public async ValueTask<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        return _cache.TryGetValue(key, out var v) ? v : null;
    }

    public async ValueTask SetAsync(string key, string value, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        _cache[key] = value;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await SaveAsync(ct).ConfigureAwait(false); } finally { _gate.Release(); }
    }

    public async ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        _cache.TryRemove(key, out _);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await SaveAsync(ct).ConfigureAwait(false); } finally { _gate.Release(); }
    }
}
