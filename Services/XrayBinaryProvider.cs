using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace OpenCodeTelegramBridge.Services;

/// <summary>Resolves a local xray-core binary, downloading it from XTLS/Xray-core's GitHub
/// releases on first use and caching it under data/bin/. Only linux-x64 (the environment this
/// app is designed to run in — WSL/Linux) is implemented; other OSes throw NotSupportedException
/// for now.</summary>
public class XrayBinaryProvider
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/XTLS/Xray-core/releases/latest";

    private readonly ConfigStore _configStore;
    private readonly ActivityLog _log;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    public XrayBinaryProvider(ConfigStore configStore, ActivityLog log)
    {
        _configStore = configStore;
        _log = log;
    }

    private string BinDir => Path.Combine(_configStore.DataDir, "bin");
    private string BinaryPath => Path.Combine(BinDir, "xray");

    /// <summary>Returns the local path to a working xray-core binary, downloading it first if
    /// necessary. Safe to call concurrently — only one download happens at a time.</summary>
    public async Task<string> EnsureBinaryAsync(CancellationToken ct = default)
    {
        if (File.Exists(BinaryPath))
            return BinaryPath;

        await _downloadLock.WaitAsync(ct);
        try
        {
            // Re-check: another caller may have finished the download while we waited.
            if (File.Exists(BinaryPath))
                return BinaryPath;

            await DownloadAndExtractAsync(ct);
            return BinaryPath;
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private async Task DownloadAndExtractAsync(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // This app is designed to run under WSL/Linux (same reasoning as OpenCode itself —
            // native Windows has known process-execution issues). Windows/macOS support is a
            // possible future addition; TODO: pick the right release asset name per-OS/arch.
            throw new NotSupportedException(
                "Automatic xray-core download is only implemented for Linux/WSL right now. " +
                "Install xray-core manually and place the binary at " + BinaryPath);
        }
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new NotSupportedException(
                $"Automatic xray-core download only supports linux-x64 right now (detected {RuntimeInformation.ProcessArchitecture}). " +
                "Install xray-core manually and place the binary at " + BinaryPath);
        }

        Directory.CreateDirectory(BinDir);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenCodeTelegramBridge");

        _log.Info("[xray] checking latest Xray-core release…");
        var releaseJson = await http.GetStringAsync(LatestReleaseUrl, ct);
        using var doc = JsonDocument.Parse(releaseJson);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : "unknown";

        string? assetUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (string.Equals(name, "Xray-linux-64.zip", StringComparison.OrdinalIgnoreCase))
                {
                    assetUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        }
        if (assetUrl == null)
            throw new InvalidOperationException("Could not find 'Xray-linux-64.zip' in the latest Xray-core release assets.");

        _log.Info($"[xray] downloading Xray-core {tag} (linux-64)…");
        var zipBytes = await http.GetByteArrayAsync(assetUrl, ct);

        var tmpZip = Path.Combine(BinDir, $"xray-download-{Guid.NewGuid():N}.zip");
        await File.WriteAllBytesAsync(tmpZip, zipBytes, ct);
        try
        {
            using (var archive = ZipFile.OpenRead(tmpZip))
            {
                var entry = archive.GetEntry("xray")
                    ?? throw new InvalidOperationException("Downloaded Xray-core zip did not contain an 'xray' binary.");
                entry.ExtractToFile(BinaryPath, overwrite: true);
            }

            // chmod +x — required on Linux, no-op concept on Windows.
            var chmod = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{BinaryPath}\"",
                UseShellExecute = false,
            });
            if (chmod != null) await chmod.WaitForExitAsync(ct);

            _log.Info($"[xray] Xray-core {tag} ready at {BinaryPath}");
        }
        catch (Exception ex)
        {
            _log.Error($"[xray] failed to extract Xray-core: {ex.Message}");
            throw;
        }
        finally
        {
            try { File.Delete(tmpZip); } catch { /* best effort */ }
        }
    }
}
