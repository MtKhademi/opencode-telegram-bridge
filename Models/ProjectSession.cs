using System.Diagnostics;

namespace OpenCodeTelegramBridge.Models;

/// <summary>Tracks one running `opencode serve` process for a single project folder.</summary>
public class ProjectSession
{
    public required string ProjectPath { get; init; }
    public required int Port { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required Process Process { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public bool Exited { get; set; }
    public string? ExitError { get; set; }

    /// <summary>OpenCode session id currently bound to this project (one at a time, v1 scope).</summary>
    public string? OpenCodeSessionId { get; set; }
}
