using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using OpenCodeTelegramBridge.Models;
using TL;

namespace OpenCodeTelegramBridge.Services;

/// <summary>
/// Connects to Telegram over raw MTProto (via WTelegramClient) instead of the classic HTTP
/// Bot API. This is what lets the bot connect through an MTProxy — the HTTP Bot API (used by
/// most Telegram bot frameworks) has no concept of MTProxy, only the native MTProto protocol
/// implemented here does.
///
/// Bridges Telegram chats to per-project `opencode serve` instances managed by <see cref="OpenCodeManager"/>.
/// </summary>
public class TelegramBridgeService : IHostedService, IAsyncDisposable
{
    private const int TelegramMessageLimit = 4000;

    private readonly ConfigStore _configStore;
    private readonly OpenCodeManager _openCode;
    private readonly ActivityLog _log;
    private readonly ConcurrentDictionary<long, ChatState> _chatStates = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _inFlight = new();

    private WTelegram.Client? _client;
    private WTelegram.UpdateManager? _manager;

    public bool IsConnected { get; private set; }
    public string? BotUsername { get; private set; }
    public long BotId { get; private set; }
    public string? LastError { get; private set; }

    public TelegramBridgeService(ConfigStore configStore, OpenCodeManager openCode, ActivityLog log)
    {
        _configStore = configStore;
        _openCode = openCode;
        _log = log;
        WTelegram.Helpers.Log = (level, message) =>
        {
            if (level >= 3) _log.Warn(message);
            // levels 0-2 are verbose protocol chatter — keep the dashboard log readable
        };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = ApplyConfigAsync(_configStore.Current);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await DisconnectAsync();

    /// <summary>(Re)connects using the given config. Safe to call again after saving new settings
    /// from the dashboard — the previous connection (if any) is torn down first.</summary>
    public async Task ApplyConfigAsync(AppConfig config)
    {
        await DisconnectAsync();

        if (!config.IsComplete)
        {
            _log.Warn("Configuration incomplete — open the dashboard to finish setup.");
            return;
        }

        try
        {
            _client = new WTelegram.Client(what => what switch
            {
                "api_id" => config.ApiId.ToString(),
                "api_hash" => config.ApiHash,
                "bot_token" => config.BotToken,
                "device_model" => "OpenCodeTelegramBridge",
                "session_pathname" => _configStore.SessionFilePath,
                _ => null
            });

            if (!string.IsNullOrWhiteSpace(config.MtProxyUrl))
            {
                _client.MTProxyUrl = config.MtProxyUrl;
                _log.Info("Connecting to Telegram via MTProxy…");
            }
            else
            {
                _log.Info("Connecting to Telegram directly…");
            }

            _manager = _client.WithUpdateManager(OnUpdateAsync);
            var me = await _client.LoginBotIfNeeded(config.BotToken);
            BotId = me.id;
            BotUsername = me.username;
            IsConnected = true;
            LastError = null;
            _log.Info($"Connected as @{me.username} ({me.id})");
        }
        catch (Exception ex)
        {
            IsConnected = false;
            LastError = ex.Message;
            _log.Error($"Telegram connection failed: {ex.Message}");
        }
    }

    private async Task DisconnectAsync()
    {
        IsConnected = false;
        _manager = null;
        if (_client != null)
        {
            var client = _client;
            _client = null;
            try { await client.DisposeAsync(); } catch { /* ignore */ }
        }
    }

    // ───────────────────────── Update handling ─────────────────────────

    private Task OnUpdateAsync(Update update)
    {
        // fire-and-forget so a slow prompt doesn't block the update pump
        if (update is UpdateNewMessage { message: Message m } && !m.flags.HasFlag(Message.Flags.out_))
            _ = Task.Run(() => HandleMessageAsync(m));
        return Task.CompletedTask;
    }

    private async Task HandleMessageAsync(Message m)
    {
        try
        {
            if (m.peer_id is not PeerUser pu) return; // v1: private chats with the bot only
            var chatId = pu.user_id;
            var config = _configStore.Current;

            if (config.AllowedUserIds.Count > 0 && !config.AllowedUserIds.Contains(chatId))
            {
                await SendAsync(chatId, "⛔ You're not authorized to use this bot.");
                _log.Warn($"Rejected message from unauthorized user {chatId}");
                return;
            }

            var text = m.message?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (text.StartsWith('/'))
                await HandleCommandAsync(chatId, text);
            else
                await RunPromptAsync(chatId, text);
        }
        catch (Exception ex)
        {
            _log.Error($"Message handling error: {ex.Message}");
        }
    }

    private ChatState GetState(long chatId) => _chatStates.GetOrAdd(chatId, _ => new ChatState());

    private async Task HandleCommandAsync(long chatId, string text)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : "";
        var state = GetState(chatId);

        switch (cmd)
        {
            case "/start":
                await SendAsync(chatId,
                    "👋 Hi! I bridge Telegram to your OpenCode projects.\n\n" +
                    "/projects — list projects\n" +
                    "/use <name> — select a project\n" +
                    "/models — list available models\n" +
                    "/model <name> — select a model\n" +
                    "/status — show current selection\n" +
                    "/stop — stop the running OpenCode server for this project\n" +
                    "/abort — cancel the in-progress prompt\n\n" +
                    "Then just send me a message to run it as a prompt.");
                break;

            case "/help":
                await SendAsync(chatId,
                    "/projects, /use <name>, /models, /model <name>, /status, /stop, /abort, /help");
                break;

            case "/projects":
            {
                var projects = _openCode.DiscoverProjects();
                if (projects.Count == 0)
                {
                    await SendAsync(chatId, "No projects found. Check the projects folder(s) in the dashboard.");
                    break;
                }
                var list = string.Join('\n', projects.Select(p => $"• {p.Name}"));
                await SendAsync(chatId, $"📁 Projects:\n{list}\n\nUse /use <name> to select one.");
                break;
            }

            case "/use":
            {
                if (string.IsNullOrWhiteSpace(arg)) { await SendAsync(chatId, "Usage: /use <project name>"); break; }
                var match = _openCode.DiscoverProjects()
                    .FirstOrDefault(p => string.Equals(p.Name, arg, StringComparison.OrdinalIgnoreCase));
                if (match == default)
                {
                    await SendAsync(chatId, $"Project '{arg}' not found. Use /projects to see the list.");
                    break;
                }
                state.ProjectPath = match.Path;
                await SendAsync(chatId, $"✅ Project set to '{match.Name}'.");
                break;
            }

            case "/models":
            {
                await SendAsync(chatId, "⏳ Fetching models…");
                try
                {
                    var models = await _openCode.GetModelsAsync();
                    await SendAsync(chatId, models.Count == 0
                        ? "No models found."
                        : $"🧠 Models:\n{string.Join('\n', models.Select(m => $"• {m}"))}\n\nUse /model <name> to select one.");
                }
                catch (Exception ex)
                {
                    await SendAsync(chatId, $"Failed to list models: {ex.Message}");
                }
                break;
            }

            case "/model":
                if (string.IsNullOrWhiteSpace(arg)) { await SendAsync(chatId, "Usage: /model <provider/model>"); break; }
                state.Model = arg;
                await SendAsync(chatId, $"✅ Model set to '{arg}'.");
                break;

            case "/status":
            {
                var running = state.ProjectPath != null &&
                               _openCode.ActiveSessions.Any(s => s.ProjectPath == state.ProjectPath && !s.Exited);
                await SendAsync(chatId,
                    $"Project: {(state.ProjectPath is { } p ? Path.GetFileName(p) : "none")}\n" +
                    $"Model: {state.Model ?? "default"}\n" +
                    $"Server running: {(running ? "yes" : "no")}");
                break;
            }

            case "/stop":
                if (state.ProjectPath != null) await _openCode.StopAsync(state.ProjectPath);
                await SendAsync(chatId, "🛑 Stopped.");
                break;

            case "/abort":
                if (_inFlight.TryRemove(chatId, out var cts)) { cts.Cancel(); await SendAsync(chatId, "Aborted."); }
                else await SendAsync(chatId, "Nothing is running.");
                break;

            default:
                await SendAsync(chatId, $"Unknown command '{cmd}'. Try /help.");
                break;
        }
    }

    // ───────────────────────── Prompt execution ─────────────────────────

    private async Task RunPromptAsync(long chatId, string text)
    {
        var state = GetState(chatId);
        if (state.ProjectPath == null)
        {
            await SendAsync(chatId, "⚠️ No project selected. Use /projects then /use <name> first.");
            return;
        }

        // Cancel any previous in-flight prompt for this chat (mirrors the upstream tool's behaviour).
        if (_inFlight.TryRemove(chatId, out var previous)) previous.Cancel();
        var cts = new CancellationTokenSource();
        _inFlight[chatId] = cts;
        var ct = cts.Token;

        long? placeholderId = null;
        try
        {
            var wasRunning = _openCode.ActiveSessions.Any(s => s.ProjectPath == state.ProjectPath && !s.Exited);
            if (!wasRunning)
                placeholderId = await SendAsync(chatId, "⏳ Starting OpenCode server…");

            var session = await _openCode.GetOrStartAsync(state.ProjectPath, ct);
            var openCodeSessionId = await _openCode.CreateOpenCodeSessionAsync(session, ct);

            var accumulated = new StringBuilder();
            var thinkingStarted = false;
            var lastThinkingEdit = DateTimeOffset.MinValue;

            // Kick off the prompt, then read the response back off the /event stream.
            await _openCode.SendPromptAsync(session, openCodeSessionId, text, state.Model, ct);

            await foreach (var evt in _openCode.ListenEventsAsync(session, ct))
            {
                switch (evt.Type)
                {
                    case "session.status":
                        if (!thinkingStarted &&
                            evt.Properties.TryGetProperty("status", out var st) &&
                            st.TryGetProperty("type", out var stType) &&
                            stType.GetString() == "busy")
                        {
                            thinkingStarted = true;
                            placeholderId ??= await SendAsync(chatId, "🤔 Thinking…");
                        }
                        break;

                    case "message.part.delta":
                        if (evt.Properties.TryGetProperty("field", out var field) && field.GetString() == "text" &&
                            evt.Properties.TryGetProperty("delta", out var delta))
                        {
                            var s = delta.GetString();
                            if (!string.IsNullOrEmpty(s)) accumulated.Append(s);
                        }
                        break;

                    case "session.idle":
                        if (MatchesSession(evt.Properties, openCodeSessionId))
                        {
                            await FinishAsync(chatId, placeholderId, accumulated.ToString());
                            _inFlight.TryRemove(chatId, out _);
                            return;
                        }
                        break;

                    case "session.error":
                        if (MatchesSession(evt.Properties, openCodeSessionId))
                        {
                            var msg = evt.Properties.TryGetProperty("error", out var err) &&
                                      err.TryGetProperty("data", out var data) &&
                                      data.TryGetProperty("message", out var dm)
                                ? dm.GetString()
                                : "Unknown error";
                            if (placeholderId is { } pid) await TryDeleteAsync(chatId, pid);
                            await SendAsync(chatId, $"❌ Error: {msg}");
                            _inFlight.TryRemove(chatId, out _);
                            return;
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (placeholderId is { } pid) await TryDeleteAsync(chatId, pid);
            await SendAsync(chatId, "⏹️ Aborted.");
        }
        catch (Exception ex)
        {
            _log.Error($"[prompt] chat={chatId} error={ex.Message}");
            if (placeholderId is { } pid) await TryDeleteAsync(chatId, pid);
            await SendAsync(chatId, $"❌ Error: {ex.Message}");
        }
        finally
        {
            _inFlight.TryRemove(chatId, out _);
        }
    }

    private static bool MatchesSession(JsonElement properties, string sessionId) =>
        properties.TryGetProperty("sessionID", out var sid) && sid.GetString() == sessionId;

    private async Task FinishAsync(long chatId, long? placeholderId, string text)
    {
        if (placeholderId is { } pid) await TryDeleteAsync(chatId, pid);
        var finalText = string.IsNullOrWhiteSpace(text) ? "✅ Done." : text;
        foreach (var chunk in SplitMessage(finalText))
            await SendAsync(chatId, chunk);
    }

    private static IEnumerable<string> SplitMessage(string text)
    {
        if (text.Length <= TelegramMessageLimit) { yield return text; yield break; }
        for (var i = 0; i < text.Length; i += TelegramMessageLimit)
            yield return text.Substring(i, Math.Min(TelegramMessageLimit, text.Length - i));
    }

    // ───────────────────────── Telegram send helpers ─────────────────────────

    private async Task<long?> SendAsync(long chatId, string text)
    {
        if (_client == null || _manager == null) return null;
        try
        {
            var peer = ResolvePeer(chatId);
            if (peer == null) return null;
            var msg = await _client.SendMessageAsync(peer, text);
            return msg?.id;
        }
        catch (Exception ex)
        {
            _log.Error($"Send failed to {chatId}: {ex.Message}");
            return null;
        }
    }

    private async Task TryDeleteAsync(long chatId, long messageId)
    {
        if (_client == null) return;
        try { await _client.Messages_DeleteMessages(new[] { (int)messageId }); }
        catch { /* best effort */ }
    }

    private InputPeer? ResolvePeer(long chatId)
    {
        if (_manager == null) return null;
        return _manager.Users.TryGetValue(chatId, out var user) ? user.ToInputPeer() : null;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
