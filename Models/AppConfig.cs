namespace OpenCodeTelegramBridge.Models;

/// <summary>Persisted application configuration (stored as data/config.json).</summary>
public class AppConfig
{
    /// <summary>Bot token from @BotFather.</summary>
    public string BotToken { get; set; } = "";

    /// <summary>Optional local SOCKS5 proxy (format: "socks5://host:port", e.g.
    /// "socks5://127.0.0.1:1080"). Use this when api.telegram.org is blocked directly on this
    /// network but a local V2ray/Xray client exposes a SOCKS5 listener that can reach it —
    /// .NET's SocketsHttpHandler routes the classic HTTP Bot API traffic through it natively.</summary>
    public string? Socks5ProxyUrl { get; set; }

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
