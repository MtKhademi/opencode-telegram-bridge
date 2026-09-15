using System.Diagnostics;
using System.Text.Json;

namespace OpenCodeTelegramBridge.Services;

/// <summary>
/// Spawns and manages a local xray-core process that tunnels a VLESS proxy link into a plain
/// local SOCKS5 listener, mirroring the process-management pattern used by
/// <see cref="OpenCodeManager"/> for `opencode serve` (fresh free port, redirect stdout/stderr
/// into <see cref="ActivityLog"/>, EnableRaisingEvents, track Exited).
///
/// Usage: call <see cref="StartAsync"/> with a parsed <see cref="VlessConfig"/> to (re)start the
/// tunnel; it returns a "socks5://127.0.0.1:&lt;port&gt;" URL once the tunnel is confirmed
/// listening. Call <see cref="StopAsync"/> before starting a new tunnel (e.g. when the user
/// changes the proxy link from the dashboard).
/// </summary>
public class ProxyTunnelManager : IDisposable
{
    private readonly ConfigStore _configStore;
    private readonly XrayBinaryProvider _binaryProvider;
    private readonly ActivityLog _log;

    private Process? _process;
    private string? _configPath;

    public int? LocalPort { get; private set; }
    public bool IsRunning => _process is { HasExited: false };
    public string? LocalSocks5Url => LocalPort is { } p ? $"socks5://127.0.0.1:{p}" : null;

    public ProxyTunnelManager(ConfigStore configStore, XrayBinaryProvider binaryProvider, ActivityLog log)
    {
        _configStore = configStore;
        _binaryProvider = binaryProvider;
        _log = log;
    }

    /// <summary>Stops any previous tunnel, then starts a new xray-core process configured to
    /// tunnel the given VLESS endpoint into a fresh local SOCKS5 port. Returns the resulting
    /// "socks5://127.0.0.1:&lt;port&gt;" URL once the tunnel is confirmed listening.</summary>
    public async Task<string> StartAsync(VlessConfig vless, CancellationToken ct = default)
    {
        await StopAsync();

        var binaryPath = await _binaryProvider.EnsureBinaryAsync(ct);

        var port = NetUtil.GetFreeTcpPort();
        var configJson = BuildXrayConfig(vless, port);
        var configPath = Path.Combine(_configStore.DataDir, $"xray-config-{port}.json");
        await File.WriteAllTextAsync(configPath, configJson, ct);

        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = $"run -c \"{configPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        _log.Info($"[xray] starting tunnel on 127.0.0.1:{port} → {vless.Host}:{vless.Port} ({vless.NetworkType}/{vless.Security})");

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            _log.Error($"[xray] failed to launch '{binaryPath}': {ex.Message}");
            throw new InvalidOperationException($"Could not launch xray-core: {ex.Message}", ex);
        }

        _process = process;
        _configPath = configPath;
        LocalPort = port;

        _ = PumpOutputAsync(process.StandardOutput, port, isError: false);
        _ = PumpOutputAsync(process.StandardError, port, isError: true);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            if (process.ExitCode != 0)
                _log.Error($"[xray:{port}] process exited with code {process.ExitCode}");
            else
                _log.Info($"[xray:{port}] process exited cleanly");
        };

        try
        {
            await NetUtil.WaitForPortAsync(port, TimeSpan.FromSeconds(10), ct);
        }
        catch (Exception ex)
        {
            _log.Error($"[xray:{port}] tunnel did not become ready: {ex.Message}");
            await StopAsync();
            throw;
        }

        _log.Info($"[xray:{port}] tunnel ready");
        return $"socks5://127.0.0.1:{port}";
    }

    private async Task PumpOutputAsync(StreamReader reader, int port, bool isError)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (line.Length == 0) continue;
                if (isError) _log.Warn($"[xray:{port}] {line}");
                else _log.Info($"[xray:{port}] {line}");
            }
        }
        catch { /* process ended, stream closed — ignore */ }
    }

    public async Task StopAsync()
    {
        var process = _process;
        _process = null;
        LocalPort = null;
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            catch { /* already gone */ }
        }
        if (_configPath != null)
        {
            try { File.Delete(_configPath); } catch { /* best effort */ }
            _configPath = null;
        }
    }

    private static string BuildXrayConfig(VlessConfig vless, int localPort)
    {
        var streamSettings = new Dictionary<string, object?>
        {
            ["network"] = vless.NetworkType,
            ["security"] = vless.Security,
        };
        if (vless.NetworkType == "ws")
        {
            streamSettings["wsSettings"] = new
            {
                path = vless.Path,
                headers = new { Host = vless.SniHost },
            };
        }
        if (vless.Security == "tls")
        {
            streamSettings["tlsSettings"] = new
            {
                serverName = vless.SniHost,
                allowInsecure = vless.AllowInsecure,
            };
        }

        var config = new
        {
            inbounds = new object[]
            {
                new
                {
                    listen = "127.0.0.1",
                    port = localPort,
                    protocol = "socks",
                    settings = new { udp = true },
                },
            },
            outbounds = new object[]
            {
                new
                {
                    protocol = "vless",
                    settings = new
                    {
                        vnext = new object[]
                        {
                            new
                            {
                                address = vless.Host,
                                port = vless.Port,
                                users = new object[]
                                {
                                    new { id = vless.Uuid, encryption = vless.Encryption },
                                },
                            },
                        },
                    },
                    streamSettings,
                },
            },
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
