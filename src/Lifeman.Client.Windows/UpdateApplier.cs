using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;

namespace Lifeman.Client.Windows;

/// Applies a portable-zip update that `UpdateDownloader` parked under
/// `stateDir/updates/<version>/`. Windows lets a running exe be renamed
/// (just not deleted), which is the trick that makes in-place updates
/// work without a separate installer: rename current → `.old`, extract
/// the new zip on top, relaunch, exit, next-launch deletes `.old`.
///
/// The zip layout is expected to be flat — a `lifeman-client.exe` plus
/// its sibling DLLs / runtime files. Anything not in the zip is left
/// untouched; the user is responsible for distributing complete bundles.
[SupportedOSPlatform("windows")]
public static class UpdateApplier
{
    /// Pick the newest staged update (by version comparison if parseable,
    /// else by directory mtime). Returns null if `stagingDir` has no
    /// usable artifacts.
    public static string? FindLatestArtifact(string stagingDir)
    {
        if (!Directory.Exists(stagingDir)) return null;
        var candidates = Directory.GetDirectories(stagingDir)
            .Select(d => new
            {
                Dir = d,
                Version = Path.GetFileName(d),
                Mtime = Directory.GetLastWriteTimeUtc(d),
                Artifact = Directory.GetFiles(d).FirstOrDefault(f =>
                    f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)),
            })
            .Where(c => c.Artifact is not null)
            .ToList();
        if (candidates.Count == 0) return null;

        // Prefer the newest mtime; users normally only have one staged
        // version, and parsing semver out of arbitrary tags risks
        // promoting a stale "v9999"-style folder.
        return candidates.OrderByDescending(c => c.Mtime).First().Artifact;
    }

    /// Apply a staged zip. Renames the current exe + companion files
    /// next to it, extracts the zip in place, and spawns a relaunch
    /// after the running process exits. Returns true on success.
    public static bool Apply(string zipPath, string? installDir = null)
    {
        var exeDir = installDir ?? Path.GetDirectoryName(Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule!.FileName!)
            ?? throw new InvalidOperationException("Could not resolve install dir.");
        var exePath = Path.Combine(exeDir, "lifeman-client.exe");
        if (!File.Exists(exePath)) throw new FileNotFoundException("install exe not found", exePath);

        // 1) Open the zip, read entries, plan the swap. Validate up
        //    front so a malformed zip doesn't leave us in a half-state.
        using var zip = ZipFile.OpenRead(zipPath);
        var staged = new List<(string Relative, string Final, ZipArchiveEntry Entry)>();
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
            var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (rel.Contains("..")) throw new InvalidDataException($"zip entry escapes root: {rel}");
            var final = Path.Combine(exeDir, rel);
            staged.Add((rel, final, entry));
        }
        if (staged.Count == 0) throw new InvalidDataException("update zip is empty");

        // 2) Rename anything we're about to overwrite to `.old` so the
        //    running process can keep executing from the renamed file.
        var renamed = new List<(string New, string Old)>();
        try
        {
            foreach (var (_, final, _) in staged)
            {
                if (File.Exists(final))
                {
                    var oldPath = final + ".old";
                    if (File.Exists(oldPath))
                    {
                        // Stale rollback file from a prior apply — drop it.
                        try { File.Delete(oldPath); } catch { /* still in use; let Move fail explicitly */ }
                    }
                    File.Move(final, oldPath);
                    renamed.Add((final, oldPath));
                }
            }

            // 3) Extract.
            foreach (var (_, final, entry) in staged)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                entry.ExtractToFile(final, overwrite: true);
            }
        }
        catch
        {
            // Roll back: put renamed files back in place.
            foreach (var (newPath, oldPath) in renamed)
            {
                try
                {
                    if (File.Exists(newPath)) File.Delete(newPath);
                    if (File.Exists(oldPath)) File.Move(oldPath, newPath);
                }
                catch { /* best-effort rollback */ }
            }
            throw;
        }

        // 4) Spawn a relaunch helper that waits for our PID then starts
        //    the freshly-extracted exe. We exit immediately so the OS
        //    releases its handle on the (renamed) exe and the helper
        //    can clean up.
        SpawnRelaunch(exePath, Environment.ProcessId);
        return true;
    }

    /// Delete any `*.old` rollback files left over from a previous
    /// apply. Called at startup of the new exe — the old one's file
    /// handle is closed by then.
    public static void SweepOldFiles(string installDir)
    {
        try
        {
            foreach (var f in Directory.GetFiles(installDir, "*.old", SearchOption.AllDirectories))
            {
                try { File.Delete(f); } catch { /* still in use, try next run */ }
            }
        }
        catch { /* installDir may be inaccessible */ }
    }

    private static void SpawnRelaunch(string exePath, int waitForPid)
    {
        // Use cmd.exe with timeout so we don't pull in a PowerShell
        // execution-policy dependency. The /B on start is important so
        // the spawned cmd window doesn't linger.
        var cmd = $"/c (for /L %i in (1,1,30) do (tasklist /FI \"PID eq {waitForPid}\" | findstr /B /C:\"lifeman-client\" >nul && timeout /T 1 /NOBREAK >nul)) & start \"\" \"{exePath}\"";
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = cmd,
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }
}
