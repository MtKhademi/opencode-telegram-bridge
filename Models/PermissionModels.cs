using System.Text.Json.Serialization;

namespace OpenCodeTelegramBridge.Models;

/// <summary>
/// Payload of the "properties" object inside a live <c>permission.asked</c> SSE event, as
/// actually emitted by the installed OpenCode server (verified against a real running
/// `opencode serve` — see README "Permission approvals" section). Field names below match the
/// live JSON exactly; note this is the *legacy* permission event/endpoint namespace, not the
/// "v2" one — the v2 endpoints (/api/session/{id}/permission/...) exist in the OpenAPI spec but
/// the session-scoped v2 list stayed empty while `permission.asked` kept firing correctly, so
/// the legacy namespace is the one actually wired up in this OpenCode version.
/// </summary>
public record PermissionAskedEvent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sessionID")] string SessionID,
    [property: JsonPropertyName("permission")] string Permission,
    [property: JsonPropertyName("patterns")] List<string> Patterns,
    [property: JsonPropertyName("always")] List<string>? Always,
    [property: JsonPropertyName("metadata")] Dictionary<string, object>? Metadata,
    [property: JsonPropertyName("tool")] PermissionAskedTool? Tool
);

public record PermissionAskedTool(
    [property: JsonPropertyName("messageID")] string MessageID,
    [property: JsonPropertyName("callID")] string CallID
);

/// <summary>Payload of the "properties" object inside a live <c>permission.replied</c> SSE event.</summary>
public record PermissionRepliedEvent(
    [property: JsonPropertyName("sessionID")] string SessionID,
    [property: JsonPropertyName("requestID")] string RequestID,
    [property: JsonPropertyName("reply")] string Reply
);

/// <summary>
/// A permission request currently awaiting the user's decision in Telegram. Keyed by
/// <see cref="RequestId"/> alone in <c>TelegramBridgeService</c>'s in-memory dictionary —
/// OpenCode's request ids ("per_...") are already globally unique per server instance, so no
/// composite (chatId, requestId) key is needed.
/// </summary>
public class PendingPermission
{
    public required string RequestId { get; init; }
    public required string SessionId { get; init; }
    public required long ChatId { get; init; }
    public required string ProjectPath { get; init; }
    public required string ProjectName { get; init; }
    public required string Action { get; init; }
    public required List<string> Resources { get; init; }
    public long? MessageId { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
