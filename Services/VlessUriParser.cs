using System.Web;

namespace OpenCodeTelegramBridge.Services;

/// <summary>Parsed fields from a "vless://" proxy link, e.g.:
/// vless://&lt;uuid&gt;@&lt;host&gt;:&lt;port&gt;?path=&lt;path&gt;&amp;security=tls&amp;encryption=none&amp;host=&lt;host&gt;&amp;type=ws&amp;allowInsecure=0#name
///
/// NOTE: only VLESS is supported for now. VMess links use a completely different format
/// (base64-encoded JSON payload, no query-string params) and are NOT handled here —
/// TODO: add VMess support as a separate parser if/when needed.</summary>
public record VlessConfig(
    string Uuid,
    string Host,
    int Port,
    string Path,
    string Security,
    string Encryption,
    string SniHost,
    string NetworkType,
    bool AllowInsecure,
    string Name);

public static class VlessUriParser
{
    /// <summary>Parses a raw "vless://..." link. Throws <see cref="FormatException"/> with a
    /// human-readable message on anything malformed, so callers (e.g. the dashboard) can show
    /// a useful error instead of a stack trace.</summary>
    public static VlessConfig Parse(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
            throw new FormatException("Proxy link is empty.");

        link = link.Trim();

        Uri uri;
        try
        {
            uri = new Uri(link);
        }
        catch (Exception ex)
        {
            throw new FormatException($"Not a valid URI: {ex.Message}");
        }

        if (!string.Equals(uri.Scheme, "vless", StringComparison.OrdinalIgnoreCase))
            throw new FormatException(
                $"Unsupported scheme '{uri.Scheme}://' — only vless:// links are supported for now " +
                "(vmess://, trojan://, ss:// are not implemented yet).");

        var uuid = Uri.UnescapeDataString(uri.UserInfo);
        if (string.IsNullOrWhiteSpace(uuid))
            throw new FormatException("Missing UUID before '@' in the vless:// link.");

        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host))
            throw new FormatException("Missing host in the vless:// link.");

        if (uri.Port <= 0)
            throw new FormatException("Missing or invalid port in the vless:// link.");
        var port = uri.Port;

        var query = HttpUtility.ParseQueryString(uri.Query);

        var path = query["path"] ?? "/";
        var security = query["security"] ?? "none";
        var encryption = query["encryption"] ?? "none";
        if (!string.Equals(encryption, "none", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Unexpected encryption='{encryption}' — VLESS requires encryption=none.");

        var sniHost = query["host"];
        if (string.IsNullOrWhiteSpace(sniHost)) sniHost = host;

        var networkType = query["type"] ?? "tcp";
        if (networkType is not ("ws" or "tcp"))
            throw new FormatException(
                $"Unsupported type='{networkType}' — only 'ws' and 'tcp' are supported for now (grpc etc. not implemented).");

        var allowInsecureRaw = query["allowInsecure"] ?? query["insecure"] ?? "0";
        var allowInsecure = allowInsecureRaw is "1" or "true";

        var name = string.IsNullOrEmpty(uri.Fragment) ? "" : Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));

        return new VlessConfig(uuid, host, port, path, security, encryption, sniHost, networkType, allowInsecure, name);
    }
}
