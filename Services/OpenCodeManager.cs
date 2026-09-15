using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenCodeTelegramBridge.Models;

namespace OpenCodeTelegramBridge.Services;

/// <summary>One parsed Server-Sent Event coming from opencode's `/event` stream.</summary>
public record SseEvent(string Type, JsonElement Properties);

/// <summary>
/// Spawns and talks to `opencode serve` processes (one per project folder, started on demand),
/// mirroring the approach used by github.com/weisser-dev/opencode-remote-telegram:
///   - a fresh random port + Basic Auth credentials per instance
///   - OPENCODE_CONFIG_PATH forced to a global config so per-project configs can't break credentials
///   - idle instances are stopped automatically
///   - one prompt = POST /session/{id}/prompt_async, response text arrives via GET /event (SSE)
/// </summary>
public class OpenCodeManager : IDisposable
{
    private readonly ConfigStore _configStore;
    private readonly ActivityLog _log;
    private readonly ConcurrentDictionary<string, ProjectSession> _sessions = new();
    private readonly Timer _idleTimer;

    public OpenCodeManager(ConfigStore configStore, ActivityLog log)
    {
        _configStore = configStore;
        _log = log;
        _idleTimer = new Timer(_ => SweepIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public IReadOnlyCollection<ProjectSession> ActiveSessions => _sessions.Values.ToList();

    /// <summary>Every immediate subdirectory of the configured project base paths.</summary>
    public List<(string Name, string Path)> DiscoverProjects()
    {
        var result = new List<(string, string)>();
        foreach (var basePath in _configStore.Current.ProjectsBasePaths)
        {
            var expanded = ExpandHome(basePath);
            if (!Directory.Exists(expanded)) continue;
            foreach (var dir in Directory.EnumerateDirectories(expanded).OrderBy(d => d))
            {
                var name = Path.GetFileName(dir.TrimEnd('/'));
                if (!string.IsNullOrEmpty(name))
                    result.Add((name, dir));
            }
        }
        return result;
    }

    public static string ExpandHome(string path)
    {
        if (path.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home + path[1..];
        }
        return path;
    }

    /// <summary>Runs `opencode models` and returns one model id ("providerID/modelID") per line.</summary>
    public async Task<List<string>> GetModelsAsync(CancellationToken ct = default)
    {
        var config = _configStore.Current;
        var psi = new ProcessStartInfo
        {
            FileName = config.OpenCodeCommand,
            Arguments = "models",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        ApplyEnv(psi, config);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start 'opencode models'");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return stdout.Split('\n')
            .Select(l => l.Trim().TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();
    }

    private void ApplyEnv(ProcessStartInfo psi, AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.OpenCodeConfigPath))
            psi.EnvironmentVariables["OPENCODE_CONFIG_PATH"] = ExpandHome(config.OpenCodeConfigPath);
    }

    public async Task<ProjectSession> GetOrStartAsync(string projectPath, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(projectPath, out var existing) && !existing.Exited)
        {
            existing.LastActivity = DateTime.UtcNow;
            return existing;
        }
        if (existing is { Exited: true })
            _sessions.TryRemove(projectPath, out _);

        var config = _configStore.Current;
        var port = NetUtil.GetFreeTcpPort();
        var username = "opencode-bridge";
        var password = Convert.ToHexString(RandomNumberGeneratorBytes(24)).ToLowerInvariant();

        var psi = new ProcessStartInfo
        {
            FileName = config.OpenCodeCommand,
            Arguments = $"serve --port {port} --hostname 127.0.0.1",
            WorkingDirectory = projectPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        ApplyEnv(psi, config);
        psi.EnvironmentVariables["OPENCODE_SERVER_USERNAME"] = username;
        psi.EnvironmentVariables["OPENCODE_SERVER_PASSWORD"] = password;

        _log.Info($"[serve] starting opencode on port {port} for {projectPath}");

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            _log.Error($"[serve] failed to launch '{config.OpenCodeCommand}': {ex.Message}");
            throw new InvalidOperationException(
                $"Could not launch OpenCode ('{config.OpenCodeCommand}'). Is it installed and on PATH? ({ex.Message})", ex);
        }

        var session = new ProjectSession
        {
            ProjectPath = projectPath,
            Port = port,
            Username = username,
            Password = password,
            Process = process,
        };
        _sessions[projectPath] = session;

        _ = PumpOutputAsync(process.StandardOutput, port, isError: false);
        _ = PumpOutputAsync(process.StandardError, port, isError: true);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            session.Exited = true;
            if (process.ExitCode != 0)
            {
                session.ExitError = $"opencode serve exited with code {process.ExitCode}";
                _log.Error($"[serve:{port}] {session.ExitError}");
            }
            else
            {
                _log.Info($"[serve:{port}] process exited cleanly");
            }
        };

        await WaitForReadyAsync(session, ct);
        return session;
    }

    private async Task PumpOutputAsync(StreamReader reader, int port, bool isError)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (line.Length == 0) continue;
                if (isError) _log.Warn($"[serve:{port}] {line}");
                else _log.Info($"[serve:{port}] {line}");
            }
        }
        catch { /* process ended, stream closed — ignore */ }
    }

    private async Task WaitForReadyAsync(ProjectSession session, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        while (DateTime.UtcNow < deadline)
        {
            if (session.Exited)
                throw new InvalidOperationException(session.ExitError ?? "opencode serve exited before becoming ready");
            try
            {
                var resp = await http.GetAsync($"http://127.0.0.1:{session.Port}/session", ct);
                if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.Unauthorized)
                    return;
            }
            catch { /* not up yet */ }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException($"opencode serve on port {session.Port} did not become ready in time");
    }

    private HttpClient MakeAuthedClient(ProjectSession session)
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{session.Port}") };
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{session.Username}:{session.Password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
        return client;
    }

    public async Task<string> CreateOpenCodeSessionAsync(ProjectSession session, CancellationToken ct = default)
    {
        using var http = MakeAuthedClient(session);
        var resp = await http.PostAsync("/session", new StringContent("{}", Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct));
        var id = doc.RootElement.GetProperty("id").GetString()
                 ?? throw new InvalidOperationException("opencode /session response had no id");
        session.OpenCodeSessionId = id;
        return id;
    }

    public static (string providerId, string modelId)? ParseModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var idx = model.IndexOf('/');
        if (idx <= 0 || idx == model.Length - 1) return null;
        return (model[..idx], model[(idx + 1)..]);
    }

    public async Task SendPromptAsync(ProjectSession session, string openCodeSessionId, string text, string? model, CancellationToken ct = default)
    {
        session.LastActivity = DateTime.UtcNow;
        using var http = MakeAuthedClient(session);
        var parsed = ParseModel(model);
        object body = parsed is { } m
            ? new { parts = new[] { new { type = "text", text } }, model = new { providerID = m.providerId, modelID = m.modelId } }
            : new { parts = new[] { new { type = "text", text } } };
        var json = JsonSerializer.Serialize(body);
        var resp = await http.PostAsync($"/session/{openCodeSessionId}/prompt_async",
            new StringContent(json, Encoding.UTF8, "application/json"), ct);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Failed to send prompt: {(int)resp.StatusCode} {resp.ReasonPhrase} — {detail}");
        }
    }

    public async Task AbortAsync(ProjectSession session, string openCodeSessionId, CancellationToken ct = default)
    {
        try
        {
            using var http = MakeAuthedClient(session);
            await http.PostAsync($"/session/{openCodeSessionId}/abort", null, ct);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Replies to a pending permission request (the legacy `/permission/{requestID}/reply`
    /// endpoint — confirmed as the one actually wired to live `permission.asked` events on the
    /// installed OpenCode version; the requestID alone identifies the request, no sessionID
    /// needed in the path). <paramref name="reply"/> must be "once", "always", or "reject".
    /// Throws with response detail on non-2xx (e.g. already-answered or unknown request).
    /// </summary>
    public async Task ReplyPermissionAsync(ProjectSession session, string requestId, string reply, CancellationToken ct = default)
    {
        using var http = MakeAuthedClient(session);
        var json = JsonSerializer.Serialize(new { reply });
        var resp = await http.PostAsync($"/permission/{requestId}/reply",
            new StringContent(json, Encoding.UTF8, "application/json"), ct);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Failed to reply to permission: {(int)resp.StatusCode} {resp.ReasonPhrase} — {detail}");
        }
    }

    /// <summary>Streams parsed events from opencode's /event SSE endpoint until cancelled.</summary>
    public async IAsyncEnumerable<SseEvent> ListenEventsAsync(ProjectSession session,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var http = MakeAuthedClient(session);
        http.Timeout = Timeout.InfiniteTimeSpan;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/event");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var dataBuilder = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) yield break; // stream closed
            if (line.Length == 0)
            {
                if (dataBuilder.Length == 0) continue;
                var payload = dataBuilder.ToString();
                dataBuilder.Clear();
                SseEvent? evt = null;
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    var root = doc.RootElement.Clone();
                    if (root.TryGetProperty("type", out var t) && root.TryGetProperty("properties", out var p))
                        evt = new SseEvent(t.GetString() ?? "", p.Clone());
                }
                catch { /* ignore malformed frame */ }
                if (evt != null) yield return evt;
                continue;
            }
            if (line.StartsWith("data:"))
            {
                var chunk = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..];
                if (dataBuilder.Length > 0) dataBuilder.Append('\n');
                dataBuilder.Append(chunk);
            }
            // other SSE fields (event:, id:, :comment) are ignored — opencode only uses "data:"
        }
    }

    public async Task StopAsync(string projectPath)
    {
        if (!_sessions.TryRemove(projectPath, out var session)) return;
        await KillAsync(session);
    }

    private async Task KillAsync(ProjectSession session)
    {
        try
        {
            if (!session.Process.HasExited)
            {
                session.Process.Kill(entireProcessTree: true);
                await session.Process.WaitForExitAsync();
            }
        }
        catch { /* already gone */ }
        session.Exited = true;
    }

    private void SweepIdle()
    {
        var timeout = TimeSpan.FromMinutes(Math.Max(1, _configStore.Current.IdleTimeoutMinutes));
        foreach (var (path, session) in _sessions.ToList())
        {
            if (session.Exited)
            {
                _sessions.TryRemove(path, out _);
                continue;
            }
            if (DateTime.UtcNow - session.LastActivity > timeout)
            {
                _log.Info($"[serve:{session.Port}] idle for {timeout.TotalMinutes:0}m — stopping");
                _ = StopAsync(path);
            }
        }
    }

    private static byte[] RandomNumberGeneratorBytes(int count)
    {
        var bytes = new byte[count];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        foreach (var session in _sessions.Values)
            KillAsync(session).GetAwaiter().GetResult();
    }
}
