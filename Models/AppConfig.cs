namespace OpenCodeTelegramBridge.Models;

/// <summary>Persisted application configuration (stored as data/config.json).</summary>
public class AppConfig
{
    /// <summary>Bot token from @BotFather.</summary>
    public string BotToken { get; set; } = "";

    /// <summary>Optional raw VLESS proxy link (e.g. "vless://uuid@host:port?...#name"), pasted
    /// directly from a V2ray/Xray subscription. When set, the app parses it, downloads and
    /// spawns a local xray-core tunnel automatically (see <see cref="ProxyTunnelManager"/>),
    /// and routes Telegram Bot API traffic through the resulting local SOCKS5 port — no manual
    /// xray-core setup or SOCKS5 configuration needed. Empty/null means connect directly.</summary>
    public string? ProxyLink { get; set; }

    /// <summary>Telegram numeric user IDs allowed to use the bot. Empty = allow anyone (not recommended).</summary>
    public List<long> AllowedUserIds { get; set; } = new();

    /// <summary>Folders that contain your projects; every immediate subdirectory is offered as a project.</summary>
    public List<string> ProjectsBasePaths { get; set; } = new();

    /// <summary>Optional path to a global opencode.json/opencode.jsonc with provider credentials,
    /// passed to spawned `opencode serve` processes via OPENCODE_CONFIG_PATH so that per-project
    /// configs without credentials don't break the connection.</summary>
    public string? OpenCodeConfigPath { get; set; }

    /// <summary>Command used to launch OpenCode. Usually just "opencode" (resolved via PATH).</summary>
    public string OpenCodeCommand { get; set; } = "opencode";

    /// <summary>Minutes of inactivity before an idle `opencode serve` instance is stopped.</summary>
    public int IdleTimeoutMinutes { get; set; } = 10;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(BotToken) &&
        ProjectsBasePaths.Count > 0;
}
