namespace OpenCodeTelegramBridge.Models;

/// <summary>Per Telegram chat (one allowed user, in private chat with the bot) selection state.</summary>
public class ChatState
{
    public string? ProjectPath { get; set; }
    public string? Model { get; set; }
}
