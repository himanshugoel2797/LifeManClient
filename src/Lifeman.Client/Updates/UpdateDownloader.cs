using System.Security.Cryptography;
using Lifeman.Client.Net;
using Microsoft.Extensions.Logging;

namespace Lifeman.Client.Updates;

/// Downloads an update artifact (Windows portable-zip, Android APK)
/// from the kernel-supplied `download_url`, verifies its SHA-256 against
/// the manifest, and parks the file in a per-version staging dir. Does
/// **not** install — that's platform-specific (see e.g.
/// `WindowsUpdateApplier`).
///
/// All HTTP traffic goes through `LifemanHttpClient` so the bearer
/// token is attached automatically; the kernel's update endpoint is
/// device-authenticated.
public sealed class UpdateDownloader
{
    private readonly LifemanHttpClient _http;
    private readonly string _stagingDir;
    private readonly ILogger _logger;

    public UpdateDownloader(LifemanHttpClient http, string stagingDir, ILogger? logger = null)
    {
        _http = http;
        _stagingDir = stagingDir;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public async Task<StagedUpdate?> DownloadAsync(UpdateInfo info, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(info.DownloadUrl))
        {
            _logger.LogDebug("update {Version}: no download_url, skipping", info.Version);
            return null;
        }
        if (string.IsNullOrEmpty(info.Sha256))
        {
            // We refuse to stage unsigned updates per CLIENT_DESIGN.md
            // §Risks: a malicious manifest could otherwise drop arbitrary
            // bytes into the user's app dir.
            _logger.LogWarning("update {Version}: refusing — manifest has no sha256", info.Version);
            return null;
        }

        var versionDir = Path.Combine(_stagingDir, info.Version);
        Directory.CreateDirectory(versionDir);

        // Pick a filename: trust the URL path if it exposes one, else
        // synthesize a name.
        var name = Path.GetFileName(new Uri(info.DownloadUrl).LocalPath);
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\'))
            name = $"lifeman-client-{info.Version}.bin";
        var finalPath = Path.Combine(versionDir, name);

        // Idempotent: if the file is already there and the hash matches,
        // skip the download. Lets the checker keep polling weekly without
        // re-downloading the same bytes.
        if (File.Exists(finalPath) && await VerifyAsync(finalPath, info.Sha256, ct).ConfigureAwait(false))
        {
            _logger.LogDebug("update {Version}: already staged at {Path}", info.Version, finalPath);
            return new StagedUpdate(info, finalPath);
        }

        var tmpPath = finalPath + ".part";
        try
        {
            using var resp = await _http.SendAsync(
                HttpMethod.Get, info.DownloadUrl,
                completion: HttpCompletionOption.ResponseHeadersRead, ct: ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("update {Version}: download HTTP {Status}", info.Version, resp.StatusCode);
                return null;
            }

            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmpPath))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            if (!await VerifyAsync(tmpPath, info.Sha256, ct).ConfigureAwait(false))
            {
                _logger.LogError("update {Version}: sha256 mismatch — discarding", info.Version);
                TryDelete(tmpPath);
                return null;
            }

            // Atomic rename so partial downloads can never be picked up
            // as "ready to apply".
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tmpPath, finalPath);
            _logger.LogInformation("update {Version}: staged at {Path}", info.Version, finalPath);
            return new StagedUpdate(info, finalPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "update {Version}: download failed", info.Version);
            TryDelete(tmpPath);
            return null;
        }
    }

    private static async Task<bool> VerifyAsync(string path, string expectedSha256, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        var hex = Convert.ToHexString(hash);
        return hex.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

/// A successfully-downloaded, hash-verified update. Heads use the path
/// to drive their platform-specific apply step.
public sealed record StagedUpdate(UpdateInfo Info, string LocalPath);
