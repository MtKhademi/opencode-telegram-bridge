using System.Net;
using System.Net.Sockets;

namespace OpenCodeTelegramBridge.Services;

/// <summary>Small shared networking helpers used by both <see cref="OpenCodeManager"/> and
/// <see cref="ProxyTunnelManager"/>.</summary>
public static class NetUtil
{
    /// <summary>Binds a TCP listener to port 0 (OS-assigned free port), reads back the port,
    /// then releases it. There's a small race window before the real listener binds, but this
    /// is the same best-effort approach used by most local dev tooling.</summary>
    public static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Polls until a TCP connection to 127.0.0.1:<paramref name="port"/> succeeds, or
    /// throws <see cref="TimeoutException"/> after <paramref name="timeout"/>.</summary>
    public static async Task WaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(IPAddress.Loopback, port, ct).AsTask();
                await connectTask.WaitAsync(TimeSpan.FromSeconds(1), ct);
                if (client.Connected) return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException($"Nothing is listening on 127.0.0.1:{port} after {timeout.TotalSeconds:0}s" +
                                    (lastError != null ? $" (last error: {lastError.Message})" : ""));
    }
}
