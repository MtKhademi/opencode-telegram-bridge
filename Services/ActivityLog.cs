using System.Collections.Concurrent;
using OpenCodeTelegramBridge.Models;

namespace OpenCodeTelegramBridge.Services;

/// <summary>Small in-memory ring buffer of recent activity, shown live on the dashboard.</summary>
public class ActivityLog
{
    private const int MaxEntries = 500;
    private readonly object _lock = new();
    private readonly LinkedList<LogEntry> _entries = new();

    /// <summary>Raised whenever a new entry is added — the dashboard's SSE endpoint uses this
    /// to push updates to connected browsers without polling.</summary>
    public event Action<LogEntry>? OnEntry;

    public void Info(string message) => Add(LogLevel2.Info, message);
    public void Warn(string message) => Add(LogLevel2.Warn, message);
    public void Error(string message) => Add(LogLevel2.Error, message);

    private void Add(LogLevel2 level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);
        lock (_lock)
        {
            _entries.AddLast(entry);
            while (_entries.Count > MaxEntries)
                _entries.RemoveFirst();
        }
        OnEntry?.Invoke(entry);
    }

    public List<LogEntry> Snapshot()
    {
        lock (_lock)
            return _entries.ToList();
    }
}
