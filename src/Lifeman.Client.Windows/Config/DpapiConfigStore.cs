using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lifeman.Client.Config;

namespace Lifeman.Client.Windows.Config;

/// File-backed config store that wraps sensitive values with Windows
/// DPAPI (CurrentUser scope) before writing them to disk. Non-sensitive
/// values are stored plain so they're inspectable for debugging.
///
/// On-disk shape:
///   { "device.token": { "p": "&lt;base64 dpapi blob&gt;" }, "server.base_url": "http://…" }
///
/// A protected value is `{ "p": "<base64>" }`; a plain value is just the
/// raw string. New keys default to plain unless ConfigKeys.IsSensitive
/// reports them as sensitive.
[SupportedOSPlatform("windows")]
public sealed class DpapiConfigStore : IConfigStore
{
    private readonly string _path;
    private readonly byte[] _entropy;
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;

    public DpapiConfigStore(string path, string entropyTag = "lifeman.client.v1")
    {
        _path = path;
        _entropy = Encoding.UTF8.GetBytes(entropyTag);
    }

    public static bool IsSensitive(string key) => key switch
    {
        ConfigKeys.DeviceToken => true,
        _ => false,
    };

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
                using var doc = JsonDocument.Parse(json);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    string? plain = null;
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        plain = p.Value.GetString();
                    }
                    else if (p.Value.ValueKind == JsonValueKind.Object
                             && p.Value.TryGetProperty("p", out var ciphertextEl)
                             && ciphertextEl.ValueKind == JsonValueKind.String)
                    {
                        var cipher = Convert.FromBase64String(ciphertextEl.GetString()!);
                        var bytes = ProtectedData.Unprotect(cipher, _entropy, DataProtectionScope.CurrentUser);
                        plain = Encoding.UTF8.GetString(bytes);
                    }
                    if (plain is not null) _cache[p.Name] = plain;
                }
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

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var kv in _cache)
            {
                if (IsSensitive(kv.Key))
                {
                    var cipher = ProtectedData.Protect(
                        Encoding.UTF8.GetBytes(kv.Value), _entropy, DataProtectionScope.CurrentUser);
                    writer.WriteStartObject(kv.Key);
                    writer.WriteString("p", Convert.ToBase64String(cipher));
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteString(kv.Key, kv.Value);
                }
            }
            writer.WriteEndObject();
        }

        var tmp = _path + ".tmp";
        await File.WriteAllBytesAsync(tmp, ms.ToArray(), ct).ConfigureAwait(false);
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
