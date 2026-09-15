namespace OpenCodeTelegramBridge.Models;

public enum LogLevel2 { Info, Warn, Error }

public record LogEntry(DateTimeOffset Time, LogLevel2 Level, string Message);
