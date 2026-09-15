namespace OpenCodeTelegramBridge.Models;

/// <summary>Per Telegram chat (one allowed user, in private chat with the bot) selection state.</summary>
public class ChatState
{
    public string? ProjectPath { get; set; }
    public string? SectionId { get; set; }
    public string? SectionWorkingDirectory { get; set; }
    public PendingCommandInput? PendingCommand { get; set; }
    public string? Model { get; set; }
}

public sealed class PendingCommandInput
{
    public required string ProjectPath { get; init; }
    public required string SectionId { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string Command { get; init; }
    public required string Title { get; init; }
}
