using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace OpenCodeTelegramBridge.Services;

/// <summary>Runs native commands without overriding their configured agent or permissions.</summary>
public sealed class OpenCodeCommandRunner(HttpClient http)
{
    public async Task<string> RunAsync(string sessionId, string command, string arguments,
        string? model, Func<JsonElement, Task> onPermission, CancellationToken ct = default)
    {
        command = command.Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Command name is required.", nameof(command));

        // Subscribe before dispatch. /command waits for completion and may need a permission
        // reply in the meantime, so awaiting the POST before consuming SSE would deadlock.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/event");
        using var events = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        events.EnsureSuccessStatusCode();
        await using var stream = await events.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // The documented first frame confirms the bus subscription is ready.
        using (var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            readyTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                var frame = await ReadFrameAsync(reader, readyTimeout.Token);
                if (frame == null) throw new IOException("OpenCode event stream closed before command dispatch.");
                if (frame.Value.TryGetProperty("type", out var type) && type.GetString() == "server.connected") break;
            }
        }

        // Command API model is a provider/model string, unlike prompt_async's model object.
        var body = new Dictionary<string, object> { ["command"] = command, ["arguments"] = arguments };
        if (!string.IsNullOrWhiteSpace(model)) body["model"] = model;
        var completion = ExecuteAsync(sessionId, body, lifetime.Token);
        var permissions = ForwardPermissionsAsync(reader, sessionId, onPermission, lifetime.Token);
        try
        {
            var first = await Task.WhenAny(completion, permissions);
            if (first == permissions && !completion.IsCompleted)
            {
                await permissions; // Surface stream/handler errors instead of hanging on approval.
                throw new IOException("OpenCode event stream closed while the command was running.");
            }
            return await completion;
        }
        finally
        {
            lifetime.Cancel();
            // Observe both tasks, including cancellation after a stream failure.
            try { await completion; } catch { }
            try { await permissions; } catch { }
        }
    }

    private async Task<string> ExecuteAsync(string sessionId, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"/session/{Uri.EscapeDataString(sessionId)}/command", body, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenCode command failed (HTTP {(int)response.StatusCode}). Check that the named command exists in this section; no prompt fallback was used.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("info", out var info) && info.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException("OpenCode reported a command execution error. Check the OpenCode server log.");
        if (!root.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("OpenCode returned an unsupported command response (missing parts).");
        return string.Join("\n", parts.EnumerateArray()
            .Where(p => p.TryGetProperty("type", out var type) && type.GetString() == "text")
            .Select(p => p.TryGetProperty("text", out var text) ? text.GetString() : null)
            .Where(text => !string.IsNullOrEmpty(text)));
    }

    private static async Task ForwardPermissionsAsync(StreamReader reader, string sessionId,
        Func<JsonElement, Task> onPermission, CancellationToken ct)
    {
        while (await ReadFrameAsync(reader, ct) is { } frame)
        {
            if (frame.TryGetProperty("type", out var type) && type.GetString() == "permission.asked" &&
                frame.TryGetProperty("properties", out var properties) &&
                properties.TryGetProperty("sessionID", out var sid) && sid.GetString() == sessionId)
                await onPermission(properties);
        }
    }

    private static async Task<JsonElement?> ReadFrameAsync(StreamReader reader, CancellationToken ct)
    {
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(data.ToString());
                    return doc.RootElement.Clone();
                }
                catch (JsonException) { data.Clear(); }
            }
            else if (line.StartsWith("data:"))
                data.AppendLine(line[5..].TrimStart(' '));
        }
        return null;
    }
}
