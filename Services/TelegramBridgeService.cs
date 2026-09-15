using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using OpenCodeTelegramBridge.Models;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace OpenCodeTelegramBridge.Services;

/// <summary>
/// Connects to Telegram over the classic HTTP Bot API (via the Telegram.Bot NuGet package)
/// using just a bot token — no api_id/api_hash from my.telegram.org required. When
/// api.telegram.org is blocked directly on this network, the user pastes a raw VLESS proxy
/// link (<see cref="AppConfig.ProxyLink"/>) into the dashboard; this service parses it via
/// <see cref="VlessUriParser"/>, spins up a local xray-core tunnel via
/// <see cref="ProxyTunnelManager"/>, and routes Bot API traffic through the resulting local
/// SOCKS5 port — .NET's SocketsHttpHandler supports socks5:// proxies natively. No manual
/// xray-core setup or SOCKS5 configuration is needed.
///
/// Bridges Telegram chats to per-project `opencode serve` instances managed by <see cref="OpenCodeManager"/>.
/// </summary>
public class TelegramBridgeService : IHostedService, IAsyncDisposable
{
    private const int TelegramMessageLimit = 4000;

    private readonly ConfigStore _configStore;
    private readonly OpenCodeManager _openCode;
    private readonly ProxyTunnelManager _proxyTunnel;
    private readonly ActivityLog _log;
    private readonly ConcurrentDictionary<long, ChatState> _chatStates = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _inFlight = new();

    /// <summary>Permission requests currently awaiting a Telegram reply, keyed by OpenCode's
    /// request id ("per_..."). Process-memory only, matching this project's existing pattern for
    /// chat state — an app restart or unanswered request just drops silently, no persistence.</summary>
    private readonly ConcurrentDictionary<string, PendingPermission> _pendingPermissions = new();

    private TelegramBotClient? _client;
    private HttpClient? _httpClient;
    private CancellationTokenSource? _receiveCts;

    public bool IsConnected { get; private set; }
    public string? BotUsername { get; private set; }
    public long BotId { get; private set; }
    public string? LastError { get; private set; }

    public TelegramBridgeService(ConfigStore configStore, OpenCodeManager openCode, ProxyTunnelManager proxyTunnel, ActivityLog log)
    {
        _configStore = configStore;
        _openCode = openCode;
        _proxyTunnel = proxyTunnel;
        _log = log;
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
            if (!string.IsNullOrWhiteSpace(config.ProxyLink))
            {
                VlessConfig vless;
                try
                {
                    vless = VlessUriParser.Parse(config.ProxyLink);
                }
                catch (Exception ex)
                {
                    _log.Error($"Invalid proxy link: {ex.Message}");
                    throw new InvalidOperationException($"Invalid proxy link: {ex.Message}", ex);
                }

                string socks5Url;
                try
                {
                    socks5Url = await _proxyTunnel.StartAsync(vless);
                }
                catch (Exception ex)
                {
                    _log.Error($"Failed to start proxy tunnel: {ex.Message}");
                    throw new InvalidOperationException($"Failed to start proxy tunnel: {ex.Message}", ex);
                }

                var handler = new SocketsHttpHandler
                {
                    Proxy = new System.Net.WebProxy(new Uri(socks5Url)),
                    UseProxy = true,
                };
                _httpClient = new HttpClient(handler);
                _client = new TelegramBotClient(config.BotToken, _httpClient);
                _log.Info($"Connecting to Telegram via local tunnel ({socks5Url})…");
            }
            else
            {
                // No proxy link configured — DisconnectAsync() above already stopped any
                // previous tunnel, so just connect directly.
                _client = new TelegramBotClient(config.BotToken);
                _log.Info("Connecting to Telegram directly…");
            }

            var me = await _client.GetMe();
            BotId = me.Id;
            BotUsername = me.Username;

            _receiveCts = new CancellationTokenSource();
            _client.StartReceiving(
                updateHandler: OnUpdateAsync,
                errorHandler: OnPollingErrorAsync,
                receiverOptions: new ReceiverOptions { AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery } },
                cancellationToken: _receiveCts.Token);

            IsConnected = true;
            LastError = null;
            _log.Info($"Connected as @{me.Username} ({me.Id})");
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
        if (_receiveCts != null)
        {
            try { await _receiveCts.CancelAsync(); } catch { /* ignore */ }
            _receiveCts.Dispose();
            _receiveCts = null;
        }
        _client = null;
        if (_httpClient != null)
        {
            _httpClient.Dispose();
            _httpClient = null;
        }
        await _proxyTunnel.StopAsync();
    }

    // ───────────────────────── Update handling ─────────────────────────

    private Task OnUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        // fire-and-forget so a slow prompt doesn't block the update pump
        if (update.Message is { } m)
            _ = Task.Run(() => HandleMessageAsync(m));
        else if (update.CallbackQuery is { } cq)
            _ = Task.Run(() => HandleCallbackQueryAsync(cq));
        return Task.CompletedTask;
    }

    private Task OnPollingErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
    {
        var message = exception is ApiRequestException apiEx
            ? $"Telegram API error: [{apiEx.ErrorCode}] {apiEx.Message}"
            : exception.Message;
        LastError = message;
        _log.Error($"Polling error: {message}");
        return Task.CompletedTask;
    }

    private async Task HandleMessageAsync(Message m)
    {
        try
        {
            if (m.Chat.Type != ChatType.Private) return; // v1: private chats with the bot only
            var chatId = m.Chat.Id;
            var config = _configStore.Current;

            if (config.AllowedUserIds.Count > 0 && !config.AllowedUserIds.Contains(chatId))
            {
                await SendAsync(chatId, "⛔ You're not authorized to use this bot.");
                _log.Warn($"Rejected message from unauthorized user {chatId}");
                return;
            }

            var text = m.Text?.Trim();
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

                    // Confirmed live on this OpenCode install (see README "Permission approvals"):
                    // the legacy `permission.asked` event/endpoint namespace, not `permission.v2.*`.
                    case "permission.asked":
                        if (MatchesSession(evt.Properties, openCodeSessionId))
                            await HandlePermissionAskedAsync(chatId, state.ProjectPath, evt.Properties);
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

    // ───────────────────────── Permission approvals ─────────────────────────

    /// <summary>
    /// Fired when OpenCode's /event stream emits a live `permission.asked` frame (see
    /// Models/PermissionModels.cs for how this shape was confirmed against a real running
    /// `opencode serve`). Tracks the request and asks the user in Telegram with inline buttons.
    /// </summary>
    private async Task HandlePermissionAskedAsync(long chatId, string projectPath, JsonElement properties)
    {
        PermissionAskedEvent asked;
        try
        {
            asked = properties.Deserialize<PermissionAskedEvent>()
                    ?? throw new InvalidOperationException("null payload");
        }
        catch (Exception ex)
        {
            _log.Error($"[permission] failed to parse permission.asked payload: {ex.Message}");
            return;
        }

        var projectName = Path.GetFileName(projectPath.TrimEnd('/'));
        var pending = new PendingPermission
        {
            RequestId = asked.Id,
            SessionId = asked.SessionID,
            ChatId = chatId,
            ProjectPath = projectPath,
            ProjectName = string.IsNullOrEmpty(projectName) ? projectPath : projectName,
            Action = asked.Permission,
            Resources = asked.Patterns,
        };
        _pendingPermissions[asked.Id] = pending;

        var resourcesText = pending.Resources.Count > 0 ? string.Join('\n', pending.Resources) : "(none specified)";
        var text =
            "⚠️ OpenCode needs your approval\n" +
            $"Project: {pending.ProjectName}\n" +
            $"Action: {pending.Action}\n" +
            $"Resources:\n{resourcesText}\n\n" +
            "Reply with one of the buttons below.";

        // Keep callback data short (Telegram caps it at 64 bytes) — encode just the reply kind
        // + request id; the rest is looked up from _pendingPermissions when the button is tapped.
        var keyboard = new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(new[]
        {
            Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("✅ Once", $"perm:once:{asked.Id}"),
            Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("🔁 Always", $"perm:always:{asked.Id}"),
            Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("❌ Reject", $"perm:reject:{asked.Id}"),
        });

        var messageId = await SendAsync(chatId, text, keyboard);
        if (messageId is { } mid) pending.MessageId = mid;
    }

    /// <summary>Handles a tap on one of the ✅/🔁/❌ inline buttons attached by
    /// <see cref="HandlePermissionAskedAsync"/>, replying to OpenCode and updating the message.</summary>
    private async Task HandleCallbackQueryAsync(Telegram.Bot.Types.CallbackQuery cq)
    {
        var data = cq.Data;
        var chatId = cq.Message?.Chat.Id ?? cq.From.Id;
        try
        {
            if (string.IsNullOrEmpty(data) || !data.StartsWith("perm:"))
            {
                await AnswerCallbackAsync(cq.Id, null);
                return;
            }

            var parts = data.Split(':', 3);
            if (parts.Length != 3)
            {
                await AnswerCallbackAsync(cq.Id, null);
                return;
            }
            var replyKind = parts[1]; // "once" | "always" | "reject"
            var requestId = parts[2];

            if (!_pendingPermissions.TryRemove(requestId, out var pending))
            {
                await AnswerCallbackAsync(cq.Id, "Already answered or expired.");
                if (cq.Message is { } staleMsg)
                    await TryEditAsync(staleMsg.Chat.Id, staleMsg.MessageId, staleMsg.Text + "\n\n(No longer pending.)");
                return;
            }

            var session = _openCode.ActiveSessions.FirstOrDefault(s => s.ProjectPath == pending.ProjectPath && !s.Exited);
            if (session == null)
            {
                await AnswerCallbackAsync(cq.Id, "Project server is no longer running.");
                await TryEditAsync(pending.ChatId, pending.MessageId,
                    "⚠️ Could not reply — the OpenCode server for this project is no longer running.");
                return;
            }

            try
            {
                await _openCode.ReplyPermissionAsync(session, requestId, replyKind);
                var outcome = replyKind switch
                {
                    "once" => "✅ Approved (once)",
                    "always" => "🔁 Always approved",
                    "reject" => "❌ Rejected",
                    _ => $"Replied: {replyKind}",
                };
                await TryEditAsync(pending.ChatId, pending.MessageId,
                    $"{outcome}\nProject: {pending.ProjectName}\nAction: {pending.Action}");
                await AnswerCallbackAsync(cq.Id, null);
            }
            catch (Exception ex)
            {
                _log.Error($"[permission] reply failed for {requestId}: {ex.Message}");
                await TryEditAsync(pending.ChatId, pending.MessageId,
                    $"⚠️ Failed to send your decision to OpenCode: {ex.Message}");
                await AnswerCallbackAsync(cq.Id, "Failed — see log.");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[permission] callback handling error: {ex.Message}");
            await AnswerCallbackAsync(cq.Id, null);
        }
    }

    private async Task AnswerCallbackAsync(string callbackQueryId, string? text)
    {
        if (_client == null) return;
        try { await _client.AnswerCallbackQuery(callbackQueryId, text); }
        catch { /* best effort */ }
    }

    private async Task TryEditAsync(long chatId, long? messageId, string text)
    {
        if (_client == null || messageId is not { } mid) return;
        try { await _client.EditMessageText(chatId, (int)mid, text); }
        catch { /* best effort — message may have been deleted, or text unchanged */ }
    }

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

    private async Task<long?> SendAsync(long chatId, string text, Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup? replyMarkup = null)
    {
        if (_client == null) return null;
        try
        {
            var msg = await _client.SendMessage(chatId, text, replyMarkup: replyMarkup);
            return msg.MessageId;
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
        try { await _client.DeleteMessage(chatId, (int)messageId); }
        catch { /* best effort */ }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
