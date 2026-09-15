namespace OpenCodeTelegramBridge.Models;

public enum MenuItemKind
{
    Project,
    Section,
    Command,
    Model,
    Provider,
}

public sealed class MenuItemToken
{
    public required string Token { get; init; }
    public required MenuItemKind Kind { get; init; }
    public required string Value { get; init; }
}

public sealed class MenuSnapshot
{
    public required string Id { get; init; }
    public required long ChatId { get; init; }
    public required long UserId { get; init; }
    public required int MessageId { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, MenuItemToken> Tokens { get; } = new(StringComparer.Ordinal);
}

public sealed class PromptExecution
{
    public required CancellationTokenSource Cancellation { get; init; }
    public string? ProjectPath { get; set; }
    public string? OpenCodeSessionId { get; set; }
}
