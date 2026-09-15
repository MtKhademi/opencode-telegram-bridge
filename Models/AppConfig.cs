namespace OpenCodeTelegramBridge.Models;

/// <summary>Persisted application configuration (stored as data/config.json).</summary>
public class AppConfig
{
    /// <summary>api_id from https://my.telegram.org/apps — required by the MTProto protocol,
    /// even when logging in as a bot (this is different from the Bot API and is NOT the bot token).</summary>
    public int ApiId { get; set; }

    /// <summary>api_hash from https://my.telegram.org/apps.</summary>
    public string ApiHash { get; set; } = "";

    /// <summary>Bot token from @BotFather.</summary>
    public string BotToken { get; set; } = "";

    /// <summary>Optional MTProxy link (https://t.me/proxy?server=...&amp;port=...&amp;secret=...).
    /// Use this when api.telegram.org / the Telegram DCs are blocked on this network but an
    /// MTProxy is reachable — WTelegramClient connects over raw MTProto so it can tunnel
    /// through an MTProxy, unlike the classic HTTP Bot API.</summary>
    public string? MtProxyUrl { get; set; }

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
        ApiId > 0 &&
        !string.IsNullOrWhiteSpace(ApiHash) &&
        !string.IsNullOrWhiteSpace(BotToken) &&
        ProjectsBasePaths.Count > 0;
}
