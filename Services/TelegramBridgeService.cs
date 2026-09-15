using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using OpenCodeTelegramBridge.Models;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace OpenCodeTelegramBridge.Services;

/// <summary>
/// Connects to Telegram over the classic HTTP Bot API (via the Telegram.Bot NuGet package)
/// using just a bot token. Bridges Telegram chats to per-project `opencode serve` instances.
/// </summary>
public class TelegramBridgeService : IHostedService, IAsyncDisposable
{
    private const int TelegramMessageLimit = 4000;
    private const int PageSize = TelegramMenuLogic.PageSize;
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromMinutes(15);

    private readonly ConfigStore _configStore;
    private readonly OpenCodeManager _openCode;
    private readonly ProjectConfigLoader _projectConfigLoader;
    private readonly ProxyTunnelManager _proxyTunnel;
    private readonly ActivityLog _log;
    private readonly ConcurrentDictionary<long, ChatState> _chatStates = new();
    private readonly ConcurrentDictionary<long, PromptExecution> _inFlight = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _chatLocks = new();
    private readonly ConcurrentDictionary<string, MenuSnapshot> _snapshots = new(StringComparer.Ordinal);

    /// <summary>Permission requests currently awaiting a Telegram reply, keyed by OpenCode's request id.</summary>
    private readonly ConcurrentDictionary<string, PendingPermission> _pendingPermissions = new();

    private TelegramBotClient? _client;
    private HttpClient? _httpClient;
    private CancellationTokenSource? _receiveCts;

    public bool IsConnected { get; private set; }
    public string? BotUsername { get; private set; }
    public long BotId { get; private set; }
    public string? LastError { get; private set; }

    public TelegramBridgeService(ConfigStore configStore, OpenCodeManager openCode, ProjectConfigLoader projectConfigLoader, ProxyTunnelManager proxyTunnel, ActivityLog log)
    {
        _configStore = configStore;
        _openCode = openCode;
        _projectConfigLoader = projectConfigLoader;
        _proxyTunnel = proxyTunnel;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = ApplyConfigAsync(_configStore.Current);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await DisconnectAsync();

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

                var socks5Url = await _proxyTunnel.StartAsync(vless);
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
            try { await _receiveCts.CancelAsync(); } catch { }
            _receiveCts.Dispose();
            _receiveCts = null;
        }
        _client = null;
        _httpClient?.Dispose();
        _httpClient = null;
        await _proxyTunnel.StopAsync();
    }

    private Task OnUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
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
            if (m.Chat.Type != ChatType.Private) return;
            var chatId = m.Chat.Id;
            if (!IsAllowed(chatId))
            {
                await SendAsync(chatId, "⛔ شما اجازه استفاده از این ربات را ندارید.");
                _log.Warn($"Rejected message from unauthorized user {chatId}");
                return;
            }

            var text = m.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (text.StartsWith('/')) await HandleCommandAsync(chatId, m.From?.Id ?? chatId, text);
            else if (await TryRunPendingCommandAsync(chatId, text)) return;
            else await RunPromptAsync(chatId, text);
        }
        catch (Exception ex)
        {
            _log.Error($"Message handling error: {ex.Message}");
        }
    }

    private bool IsAllowed(long userId)
    {
        var allowed = _configStore.Current.AllowedUserIds;
        return allowed.Count == 0 || allowed.Contains(userId);
    }

    private ChatState GetState(long chatId) => _chatStates.GetOrAdd(chatId, _ => new ChatState());
    private SemaphoreSlim GetChatLock(long chatId) => _chatLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));

    private async Task HandleCommandAsync(long chatId, long userId, string text)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : "";

        switch (cmd)
        {
            case "/start":
            case "/menu":
                await ShowMainMenuAsync(chatId, userId);
                break;
            case "/help":
                await SendAsync(chatId, HelpText(), MainMenuOnlyKeyboard());
                break;
            case "/projects":
                await ShowProjectsAsync(chatId, userId);
                break;
            case "/use":
                await UseProjectByNameAsync(chatId, userId, arg);
                break;
            case "/models":
                await ShowModelProvidersAsync(chatId, userId, refresh: true);
                break;
            case "/model":
                await SelectModelByTextAsync(chatId, userId, arg);
                break;
            case "/section":
            case "/sections":
                await ShowSectionsAsync(chatId, userId);
                break;
            case "/status":
                await ShowStatusAsync(chatId, userId);
                break;
            case "/stop":
                await StopProjectServerAsync(chatId, userId);
                break;
            case "/abort":
                await AbortPromptAsync(chatId, userId);
                break;
            default:
                await SendAsync(chatId, $"دستور ناشناخته است: {cmd}\nبرای دیدن گزینه‌ها /menu را بفرستید.", MainMenuOnlyKeyboard());
                break;
        }
    }

    private async Task ShowMainMenuAsync(long chatId, long userId, int? messageId = null, string? prefix = null)
    {
        CleanupSnapshots();
        var state = GetState(chatId);
        var projectName = GetSelectedProjectName(state.ProjectPath) ?? "انتخاب نشده";
        var sectionTitle = GetSelectedSectionTitle(state) ?? "انتخاب نشده";
        var model = state.Model ?? "پیش‌فرض OpenCode";
        var workingDirectory = GetCurrentWorkingDirectory(state);
        var running = workingDirectory != null && IsProjectRunning(workingDirectory);
        var text =
            (string.IsNullOrWhiteSpace(prefix) ? "" : prefix.TrimEnd() + "\n\n") +
            "منوی OpenCode Telegram Bridge\n\n" +
            $"پروژه: {projectName}\n" +
            $"بخش: {sectionTitle}\n" +
            $"مدل: {model}\n" +
            $"سرور بخش: {(running ? "در حال اجرا" : "متوقف")}";

        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData("📁 پروژه‌ها", "menu:projects"), InlineKeyboardButton.WithCallbackData("🧩 تغییر بخش", "menu:sections") },
            new[] { InlineKeyboardButton.WithCallbackData("⚡ اقدام‌ها", "menu:actions"), InlineKeyboardButton.WithCallbackData("🧠 تغییر مدل", "menu:models") },
            new[] { InlineKeyboardButton.WithCallbackData("📊 وضعیت", "menu:status"), InlineKeyboardButton.WithCallbackData("⏹ لغو درخواست", "menu:abort") },
            new[] { InlineKeyboardButton.WithCallbackData("🛑 توقف سرور بخش", "menu:stop") },
            new[] { InlineKeyboardButton.WithCallbackData("🔄 بارگذاری مجدد تنظیمات پروژه", "menu:reload") },
            new[] { InlineKeyboardButton.WithCallbackData("❓ راهنما", "menu:help") },
        };
        var keyboard = new InlineKeyboardMarkup(rows);
        await SendOrEditMenuAsync(chatId, messageId, text, keyboard);
    }

    private async Task ShowProjectsAsync(long chatId, long userId, int? messageId = null, int page = 0, string? prefix = null)
    {
        CleanupSnapshots();
        var projects = _openCode.DiscoverProjects();
        if (projects.Count == 0)
        {
            await SendOrEditMenuAsync(chatId, messageId,
                (prefix is null ? "" : prefix + "\n\n") + "هیچ پروژه‌ای پیدا نشد. مسیر پروژه‌ها را در داشبورد بررسی کنید.",
                new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "menu:main") } }));
            return;
        }

        page = Math.Clamp(page, 0, Math.Max(0, (projects.Count - 1) / PageSize));
        var labels = BuildProjectLabels(projects);
        var snapshot = CreateSnapshot(chatId, userId, messageId ?? 0);
        var rows = new List<InlineKeyboardButton[]>();
        var state = GetState(chatId);
        foreach (var (project, label) in projects.Zip(labels).Skip(page * PageSize).Take(PageSize))
        {
            var token = AddToken(snapshot, MenuItemKind.Project, project.Path);
            var selected = state.ProjectPath == project.Path ? "✅ " : "";
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData(selected + label, $"project:select:{snapshot.Id}:{token}") });
        }
        AddPager(rows, "project:page", snapshot.Id, page, projects.Count);
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "menu:main") });

        var text = (prefix is null ? "" : prefix + "\n\n") + "📁 پروژه را انتخاب کنید:";
        var sentId = await SendOrEditMenuAsync(chatId, messageId, text, new InlineKeyboardMarkup(rows));
        snapshot.MessageId = (int)(sentId ?? messageId ?? 0);
    }

    private async Task UseProjectByNameAsync(long chatId, long userId, string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await SendAsync(chatId, "روش استفاده: /use <name>", MainMenuOnlyKeyboard());
            return;
        }
        var projects = _openCode.DiscoverProjects();
        var matches = projects.Where(p => string.Equals(p.Name, arg, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            await SendAsync(chatId, $"پروژه‌ای با نام '{arg}' پیدا نشد.", ProjectsOnlyKeyboard());
            return;
        }
        if (matches.Count > 1)
        {
            var labels = BuildProjectLabels(matches);
            var snapshot = CreateSnapshot(chatId, userId, 0);
            var rows = matches.Zip(labels).Select(pair =>
            {
                var token = AddToken(snapshot, MenuItemKind.Project, pair.First.Path);
                return new[] { InlineKeyboardButton.WithCallbackData(pair.Second, $"project:select:{snapshot.Id}:{token}") };
            }).ToList();
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "menu:main") });
            var id = await SendAsync(chatId, "چند پروژه با این نام پیدا شد. یکی را انتخاب کنید:", new InlineKeyboardMarkup(rows));
            snapshot.MessageId = (int)(id ?? 0);
            return;
        }
        await SelectProjectPathAsync(chatId, userId, matches[0].Path);
    }

    private async Task SelectProjectPathAsync(long chatId, long userId, string projectPath, int? messageId = null)
    {
        var projects = _openCode.DiscoverProjects();
        var match = projects.FirstOrDefault(p => p.Path == projectPath);
        if (match == default)
        {
            await ShowProjectsAsync(chatId, userId, messageId, prefix: "این پروژه دیگر در فهرست فعلی وجود ندارد.");
            return;
        }
        var gate = GetChatLock(chatId);
        await gate.WaitAsync();
        try
        {
            var state = GetState(chatId);
            state.ProjectPath = match.Path;
            state.SectionId = null;
            state.SectionWorkingDirectory = null;
            state.PendingCommand = null;
        }
        finally { gate.Release(); }

        var config = _projectConfigLoader.Load(match.Path, match.Name);
        if (config.IsValid)
            await ShowSectionsAsync(chatId, userId, messageId, $"✅ پروژه '{config.Config!.Name}' انتخاب شد. حالا بخش را انتخاب کنید.");
        else if (config.Exists)
            await ShowMainMenuAsync(chatId, userId, messageId, $"✅ پروژه '{match.Name}' انتخاب شد، اما telegram-bridge.json معتبر نیست:\n{config.Error}");
        else
            await ShowMainMenuAsync(chatId, userId, messageId, $"✅ پروژه '{match.Name}' انتخاب شد.");
    }

    private async Task ShowSectionsAsync(long chatId, long userId, int? messageId = null, string? prefix = null, int page = 0)
    {
        var state = GetState(chatId);
        var projectPath = state.ProjectPath;
        if (projectPath == null)
        {
            await ShowProjectsAsync(chatId, userId, messageId, prefix: "ابتدا پروژه را انتخاب کنید.");
            return;
        }
        var project = _openCode.DiscoverProjects().FirstOrDefault(p => p.Path == projectPath);
        if (project == default)
        {
            state.ProjectPath = null;
            state.SectionId = null;
            state.SectionWorkingDirectory = null;
            state.PendingCommand = null;
            await ShowProjectsAsync(chatId, userId, messageId, prefix: "پروژه انتخاب‌شده دیگر وجود ندارد.");
            return;
        }
        var loaded = _projectConfigLoader.Load(project.Path, project.Name);
        if (!loaded.Exists)
        {
            state.SectionId = null;
            state.SectionWorkingDirectory = null;
            await ShowMainMenuAsync(chatId, userId, messageId, "این پروژه فایل telegram-bridge.json ندارد و با حالت ریشه پروژه اجرا می‌شود.");
            return;
        }
        if (!loaded.IsValid)
        {
            await SendOrEditMenuAsync(chatId, messageId, $"telegram-bridge.json معتبر نیست:\n{loaded.Error}", new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("🔄 تلاش دوباره", "menu:reload") },
                new[] { InlineKeyboardButton.WithCallbackData("📁 تغییر پروژه", "menu:projects") },
                new[] { InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "menu:main") },
            }));
            return;
        }
        var config = loaded.Config!;
        if (config.Sections.Count == 0)
        {
            await SendOrEditMenuAsync(chatId, messageId, "این پروژه هیچ بخشی تعریف نکرده است.", MainMenuOnlyKeyboard());
            return;
        }
        var snapshot = CreateSnapshot(chatId, userId, messageId ?? 0);
        var rows = new List<InlineKeyboardButton[]>();
        page = Math.Clamp(page, 0, Math.Max(0, (config.Sections.Count - 1) / PageSize));
        foreach (var section in config.Sections.Skip(page * PageSize).Take(PageSize))
        {
            var token = AddToken(snapshot, MenuItemKind.Section, section.Id);
            var selected = state.SectionId == section.Id ? "✅ " : "";
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData(selected + section.Title, $"section:select:{snapshot.Id}:{token}") });
        }
        AddPager(rows, "section:page", snapshot.Id, page, config.Sections.Count);
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("📁 تغییر پروژه", "menu:projects"), InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "menu:main") });
        var text = (prefix is null ? "" : prefix + "\n\n") + $"🧩 بخش پروژه {config.Name} را انتخاب کنید:";
        var sentId = await SendOrEditMenuAsync(chatId, messageId, text, new InlineKeyboardMarkup(rows));
        snapshot.MessageId = (int)(sentId ?? messageId ?? 0);
    }

    private async Task SelectSectionAsync(long chatId, long userId, string sectionId, int? messageId = null)
    {
        var state = GetState(chatId);
        if (state.ProjectPath == null)
        {
            await ShowProjectsAsync(chatId, userId, messageId, prefix: "ابتدا پروژه را انتخاب کنید.");
            return;
        }
        var project = _openCode.DiscoverProjects().FirstOrDefault(p => p.Path == state.ProjectPath);
        if (project == default)
        {
            await ShowProjectsAsync(chatId, userId, messageId, prefix: "پروژه انتخاب‌شده دیگر وجود ندارد.");
            return;
        }
        var loaded = _projectConfigLoader.Load(project.Path, project.Name);
        if (!loaded.IsValid)
        {
            await ShowSectionsAsync(chatId, userId, messageId, "تنظیمات پروژه معتبر نیست یا تغییر کرده است.");
            return;
        }
        var section = loaded.Config!.Sections.FirstOrDefault(s => string.Equals(s.Id, sectionId, StringComparison.OrdinalIgnoreCase));
        if (section == null)
        {
            await ShowSectionsAsync(chatId, userId, messageId, "این بخش دیگر وجود ندارد.");
            return;
        }
        var gate = GetChatLock(chatId);
        await gate.WaitAsync();
        try
        {
            state.SectionId = section.Id;
            state.SectionWorkingDirectory = section.ResolvedDirectory;
            state.PendingCommand = null;
        }
        finally { gate.Release(); }
        await ShowSectionMenuAsync(chatId, userId, messageId, $"✅ بخش '{section.Title}' انتخاب شد.");
    }

    private async Task ShowSectionMenuAsync(long chatId, long userId, int? messageId = null, string? prefix = null, int page = 0)
    {
        var state = GetState(chatId);
        var section = GetCurrentSection(state, out var loaded);
        if (state.ProjectPath == null)
        {
            await ShowProjectsAsync(chatId, userId, messageId, prefix: "ابتدا پروژه را انتخاب کنید.");
            return;
        }
        if (loaded?.Exists == false)
        {
            await ShowMainMenuAsync(chatId, userId, messageId, "این پروژه تنظیمات بخشی ندارد.");
            return;
        }
        if (loaded?.IsValid != true || section == null)
        {
            await ShowSectionsAsync(chatId, userId, messageId, "ابتدا یک بخش معتبر انتخاب کنید.");
            return;
        }
        var commands = section.Commands;
        page = Math.Clamp(page, 0, Math.Max(0, (commands.Count - 1) / PageSize));
        var snapshot = CreateSnapshot(chatId, userId, messageId ?? 0);
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var command in commands.Skip(page * PageSize).Take(PageSize))
        {
            var token = AddToken(snapshot, MenuItemKind.Command, command.Id);
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData(command.Title, $"command:run:{snapshot.Id}:{token}") });
        }
        AddPager(rows, "command:page", snapshot.Id, page, commands.Count);
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🧩 تغییر بخش", "menu:sections"), InlineKeyboardButton.WithCallbackData("📁 تغییر پروژه", "menu:projects") });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🧠 تغییر مدل", "menu:models"), InlineKeyboardButton.WithCallbackData("📊 وضعیت", "menu:status") });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🔄 بارگذاری مجدد", "menu:reload"), InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "menu:main") });
        var text = (prefix is null ? "" : prefix + "\n\n") + $"⚡ اقدام‌های بخش {section.Title}:" + (commands.Count == 0 ? "\nبرای این بخش اقدامی تعریف نشده است. پیام عادی شما در همین بخش اجرا می‌شود." : "");
        var sentId = await SendOrEditMenuAsync(chatId, messageId, text, new InlineKeyboardMarkup(rows));
        snapshot.MessageId = (int)(sentId ?? messageId ?? 0);
    }

    private async Task RunConfiguredCommandAsync(long chatId, long userId, string commandId, int? messageId = null)
    {
        var state = GetState(chatId);
        var section = GetCurrentSection(state, out var loaded);
        if (loaded?.IsValid != true || section == null)
        {
            await ShowSectionsAsync(chatId, userId, messageId, "بخش انتخاب‌شده معتبر نیست.");
            return;
        }
        var command = section.Commands.FirstOrDefault(c => string.Equals(c.Id, commandId, StringComparison.OrdinalIgnoreCase));
        if (command == null)
        {
            await ShowSectionMenuAsync(chatId, userId, messageId, "این اقدام دیگر وجود ندارد.");
            return;
        }
        if (command.Type == "prompt")
        {
            await SendOrEditMenuAsync(chatId, messageId, $"در حال اجرای اقدام: {command.Title}", MainMenuOnlyKeyboard());
            await RunPromptAsync(chatId, command.Text!);
            return;
        }
        if (command.Type == "opencode-command")
        {
            var slashCommand = "/" + command.Command!.TrimStart('/');
            if (command.AskForArguments)
            {
                var gate = GetChatLock(chatId);
                await gate.WaitAsync();
                try
                {
                    state.PendingCommand = new PendingCommandInput
                    {
                        ProjectPath = state.ProjectPath!,
                        SectionId = section.Id,
                        WorkingDirectory = section.ResolvedDirectory,
                        Command = slashCommand,
                        Title = command.Title,
                    };
                }
                finally { gate.Release(); }
                await SendOrEditMenuAsync(chatId, messageId, $"آرگومان‌های '{command.Title}' را در پیام بعدی بفرستید. برای اجرای بدون آرگومان فقط '-' را بفرستید.", MainMenuOnlyKeyboard());
                return;
            }
            await RunPromptAsync(chatId, slashCommand);
        }
    }

    private async Task<bool> TryRunPendingCommandAsync(long chatId, string text)
    {
        PendingCommandInput? pending;
        var gate = GetChatLock(chatId);
        await gate.WaitAsync();
        try
        {
            var state = GetState(chatId);
            pending = state.PendingCommand;
            state.PendingCommand = null;
        }
        finally { gate.Release(); }
        if (pending == null) return false;
        var args = text.Trim() == "-" ? "" : " " + text.Trim();
        await RunPromptInContextAsync(chatId, pending.WorkingDirectory, pending.Command + args, GetState(chatId).Model);
        return true;
    }

    private async Task ShowModelProvidersAsync(long chatId, long userId, int? messageId = null, bool refresh = false, string? prefix = null)
    {
        var loadingId = messageId;
        if (refresh || messageId == null)
            loadingId = (int?)await SendOrEditMenuAsync(chatId, messageId, "⏳ در حال دریافت فهرست مدل‌ها…", MainMenuOnlyKeyboard());
        try
        {
            var models = await _openCode.GetModelsAsync();
            await ShowModelProvidersFromListAsync(chatId, userId, loadingId, models, prefix);
        }
        catch (Exception ex)
        {
            _log.Error($"[models] failed: {ex.Message}");
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("🔄 تلاش دوباره", "model:refresh") },
                new[] { InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "menu:main") },
            });
            await SendOrEditMenuAsync(chatId, loadingId, $"دریافت مدل‌ها ناموفق بود:\n{ex.Message}", keyboard);
        }
    }

    private async Task ShowModelProvidersFromListAsync(long chatId, long userId, int? messageId, List<string> models, string? prefix = null)
    {
        CleanupSnapshots();
        var snapshot = CreateSnapshot(chatId, userId, messageId ?? 0);
        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData(GetState(chatId).Model == null ? "✅ پیش‌فرض OpenCode" : "پیش‌فرض OpenCode", $"model:default:{snapshot.Id}") }
        };

        var providers = models.Select(m => OpenCodeManager.ParseModel(m)?.providerId)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p)
            .ToList();

        if (models.Count == 0)
        {
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🔄 تازه‌سازی", "model:refresh") });
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "menu:main") });
            await SendOrEditMenuAsync(chatId, messageId, "مدلی پیدا نشد.", new InlineKeyboardMarkup(rows));
            return;
        }

        foreach (var provider in providers.Take(PageSize))
        {
            var token = AddToken(snapshot, MenuItemKind.Provider, provider!);
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData(provider!, $"model:provider:{snapshot.Id}:{token}") });
        }
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🔄 تازه‌سازی", "model:refresh") });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "menu:main") });
        var text = (prefix is null ? "" : prefix + "\n\n") + "🧠 ارائه‌دهنده مدل را انتخاب کنید:";
        var sentId = await SendOrEditMenuAsync(chatId, messageId, text, new InlineKeyboardMarkup(rows));
        snapshot.MessageId = (int)(sentId ?? messageId ?? 0);
    }

    private async Task ShowModelsForProviderAsync(long chatId, long userId, string provider, int? messageId, int page = 0)
    {
        try
        {
            var models = await _openCode.GetModelsAsync();
            var providerModels = models.Where(m => OpenCodeManager.ParseModel(m)?.providerId.Equals(provider, StringComparison.OrdinalIgnoreCase) == true).ToList();
            if (providerModels.Count == 0)
            {
                await ShowModelProvidersAsync(chatId, userId, messageId, refresh: true, prefix: "برای این ارائه‌دهنده مدلی پیدا نشد.");
                return;
            }
            page = Math.Clamp(page, 0, Math.Max(0, (providerModels.Count - 1) / PageSize));
            var snapshot = CreateSnapshot(chatId, userId, messageId ?? 0);
            var rows = new List<InlineKeyboardButton[]>();
            foreach (var model in providerModels.Skip(page * PageSize).Take(PageSize))
            {
                var token = AddToken(snapshot, MenuItemKind.Model, model);
                var label = GetState(chatId).Model == model ? "✅ " + model : model;
                rows.Add(new[] { InlineKeyboardButton.WithCallbackData(label, $"model:select:{snapshot.Id}:{token}") });
            }
            var providerPageToken = AddToken(snapshot, MenuItemKind.Provider, provider);
            AddPager(rows, "model:page", snapshot.Id, page, providerModels.Count, providerPageToken);
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ بازگشت", "model:refresh"), InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "menu:main") });
            var sentId = await SendOrEditMenuAsync(chatId, messageId, $"مدل‌های {provider}:", new InlineKeyboardMarkup(rows));
            snapshot.MessageId = (int)(sentId ?? messageId ?? 0);
        }
        catch (Exception ex)
        {
            await SendOrEditMenuAsync(chatId, messageId, $"دریافت مدل‌ها ناموفق بود:\n{ex.Message}", new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("🔄 تلاش دوباره", "model:refresh") } }));
        }
    }

    private async Task SelectModelByTextAsync(long chatId, long userId, string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await SendAsync(chatId, "روش استفاده: /model <provider/model> یا /model default", MainMenuOnlyKeyboard());
            return;
        }
        if (string.Equals(arg, "default", StringComparison.OrdinalIgnoreCase))
        {
            await SetModelAsync(chatId, userId, null);
            return;
        }
        await SendAsync(chatId, "⏳ در حال بررسی مدل…");
        var models = await _openCode.GetModelsAsync();
        var match = models.FirstOrDefault(m => string.Equals(m, arg, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            await SendAsync(chatId, "این مدل در فهرست OpenCode پیدا نشد. از /models استفاده کنید.", ChangeModelOnlyKeyboard());
            return;
        }
        await SetModelAsync(chatId, userId, match);
    }

    private async Task SetModelAsync(long chatId, long userId, string? model, int? messageId = null)
    {
        var gate = GetChatLock(chatId);
        await gate.WaitAsync();
        try { GetState(chatId).Model = model; }
        finally { gate.Release(); }
        await ShowMainMenuAsync(chatId, userId, messageId, model == null ? "✅ مدل به پیش‌فرض OpenCode برگشت." : $"✅ مدل '{model}' انتخاب شد.");
    }

    private async Task ShowStatusAsync(long chatId, long userId, int? messageId = null)
    {
        var state = GetState(chatId);
        var workingDirectory = GetCurrentWorkingDirectory(state);
        var text =
            $"📊 وضعیت\n\nپروژه: {GetSelectedProjectName(state.ProjectPath) ?? "انتخاب نشده"}\n" +
            $"بخش: {GetSelectedSectionTitle(state) ?? "انتخاب نشده"}\n" +
            $"مدل: {state.Model ?? "پیش‌فرض OpenCode"}\n" +
            $"سرور بخش: {(workingDirectory != null && IsProjectRunning(workingDirectory) ? "در حال اجرا" : "متوقف")}";
        await SendOrEditMenuAsync(chatId, messageId, text, MainMenuOnlyKeyboard());
    }

    private async Task StopProjectServerAsync(long chatId, long userId, int? messageId = null)
    {
        var workingDirectory = GetCurrentWorkingDirectory(GetState(chatId));
        if (workingDirectory != null) await _openCode.StopAsync(workingDirectory);
        await ShowMainMenuAsync(chatId, userId, messageId, workingDirectory == null ? "پروژه یا بخش انتخاب نشده است." : "🛑 سرور بخش متوقف شد.");
    }

    private async Task AbortPromptAsync(long chatId, long userId, int? messageId = null)
    {
        if (!_inFlight.TryRemove(chatId, out var execution))
        {
            await ShowMainMenuAsync(chatId, userId, messageId, "درخواستی در حال اجرا نیست.");
            return;
        }
        if (execution.ProjectPath != null && execution.OpenCodeSessionId != null)
        {
            var session = _openCode.ActiveSessions.FirstOrDefault(s => s.ProjectPath == execution.ProjectPath && !s.Exited);
            if (session != null) await _openCode.AbortAsync(session, execution.OpenCodeSessionId);
        }
        execution.Cancellation.Cancel();
        await ShowMainMenuAsync(chatId, userId, messageId, "⏹ درخواست لغو شد.");
    }

    private async Task RunPromptAsync(long chatId, string text)
    {
        string? projectPath;
        string? model;
        var gate = GetChatLock(chatId);
        await gate.WaitAsync();
        try
        {
            var state = GetState(chatId);
            projectPath = state.ProjectPath;
            model = state.Model;
        }
        finally { gate.Release(); }

        if (projectPath == null)
        {
            await SendAsync(chatId, "برای ارسال درخواست ابتدا یک پروژه انتخاب کنید.", ProjectsOnlyKeyboard());
            return;
        }

        var workingDirectory = GetCurrentWorkingDirectory(GetState(chatId));
        if (workingDirectory == null)
        {
            await ShowSectionsAsync(chatId, chatId, prefix: "برای این پروژه ابتدا یک بخش انتخاب کنید.");
            return;
        }
        await RunPromptInContextAsync(chatId, workingDirectory, text, model);
    }

    private async Task RunPromptInContextAsync(long chatId, string workingDirectory, string text, string? model)
    {
        if (_inFlight.TryRemove(chatId, out var previous)) previous.Cancellation.Cancel();
        var execution = new PromptExecution { Cancellation = new CancellationTokenSource(), ProjectPath = workingDirectory };
        _inFlight[chatId] = execution;
        var ct = execution.Cancellation.Token;

        long? placeholderId = null;
        try
        {
            var wasRunning = IsProjectRunning(workingDirectory);
            if (!wasRunning) placeholderId = await SendAsync(chatId, "⏳ در حال راه‌اندازی سرور OpenCode…");

            var session = await _openCode.GetOrStartAsync(workingDirectory, ct);
            var openCodeSessionId = await _openCode.CreateOpenCodeSessionAsync(session, ct);
            execution.OpenCodeSessionId = openCodeSessionId;

            var accumulated = new StringBuilder();
            var thinkingStarted = false;

            await _openCode.SendPromptAsync(session, openCodeSessionId, text, model, ct);

            await foreach (var evt in _openCode.ListenEventsAsync(session, ct))
            {
                switch (evt.Type)
                {
                    case "session.status":
                        if (!thinkingStarted && evt.Properties.TryGetProperty("status", out var st) && st.TryGetProperty("type", out var stType) && stType.GetString() == "busy")
                        {
                            thinkingStarted = true;
                            placeholderId ??= await SendAsync(chatId, "🤔 در حال فکر کردن…");
                        }
                        break;
                    case "message.part.delta":
                        if (evt.Properties.TryGetProperty("field", out var field) && field.GetString() == "text" && evt.Properties.TryGetProperty("delta", out var delta))
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
                            var msg = evt.Properties.TryGetProperty("error", out var err) && err.TryGetProperty("data", out var data) && data.TryGetProperty("message", out var dm)
                                ? dm.GetString()
                                : "خطای نامشخص";
                            if (placeholderId is { } pid) await TryDeleteAsync(chatId, pid);
                            await SendAsync(chatId, $"❌ خطا: {msg}", MainMenuOnlyKeyboard());
                            _inFlight.TryRemove(chatId, out _);
                            return;
                        }
                        break;
                    case "permission.asked":
                        if (MatchesSession(evt.Properties, openCodeSessionId)) await HandlePermissionAskedAsync(chatId, workingDirectory, evt.Properties);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (placeholderId is { } pid) await TryDeleteAsync(chatId, pid);
            await SendAsync(chatId, "⏹ درخواست لغو شد.", MainMenuOnlyKeyboard());
        }
        catch (Exception ex)
        {
            _log.Error($"[prompt] chat={chatId} error={ex.Message}");
            if (placeholderId is { } pid) await TryDeleteAsync(chatId, pid);
            await SendAsync(chatId, $"❌ خطا: {ex.Message}", MainMenuOnlyKeyboard());
        }
        finally
        {
            _inFlight.TryRemove(chatId, out _);
        }
    }

    private static bool MatchesSession(JsonElement properties, string sessionId) =>
        properties.TryGetProperty("sessionID", out var sid) && sid.GetString() == sessionId;

    private async Task HandlePermissionAskedAsync(long chatId, string projectPath, JsonElement properties)
    {
        PermissionAskedEvent asked;
        try
        {
            asked = properties.Deserialize<PermissionAskedEvent>() ?? throw new InvalidOperationException("null payload");
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

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            InlineKeyboardButton.WithCallbackData("✅ Once", $"perm:once:{asked.Id}"),
            InlineKeyboardButton.WithCallbackData("🔁 Always", $"perm:always:{asked.Id}"),
            InlineKeyboardButton.WithCallbackData("❌ Reject", $"perm:reject:{asked.Id}"),
        });

        var messageId = await SendAsync(chatId, text, keyboard);
        if (messageId is { } mid) pending.MessageId = mid;
    }

    private async Task HandleCallbackQueryAsync(CallbackQuery cq)
    {
        var data = cq.Data ?? "";
        var chatId = cq.Message?.Chat.Id ?? cq.From.Id;
        var messageId = cq.Message?.MessageId;
        try
        {
            if (cq.Message?.Chat.Type != ChatType.Private && !data.StartsWith("perm:", StringComparison.Ordinal))
            {
                await AnswerCallbackAsync(cq.Id, null);
                return;
            }
            if (!IsAllowed(cq.From.Id))
            {
                await AnswerCallbackAsync(cq.Id, "اجازه دسترسی ندارید.");
                return;
            }
            await AnswerCallbackAsync(cq.Id, null);

            if (data.StartsWith("perm:", StringComparison.Ordinal))
            {
                await HandlePermissionCallbackAsync(cq, data);
                return;
            }
            if (string.IsNullOrWhiteSpace(data)) return;

            var parts = data.Split(':');
            switch (parts[0])
            {
                case "menu":
                    await HandleMenuCallbackAsync(chatId, cq.From.Id, messageId, parts);
                    break;
                case "project":
                    await HandleProjectCallbackAsync(chatId, cq.From.Id, messageId, parts);
                    break;
                    case "model":
                    await HandleModelCallbackAsync(chatId, cq.From.Id, messageId, parts);
                    break;
                case "section":
                    await HandleSectionCallbackAsync(chatId, cq.From.Id, messageId, parts);
                    break;
                case "command":
                    await HandleCommandCallbackAsync(chatId, cq.From.Id, messageId, parts);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[callback] handling error: {ex.Message}");
            await AnswerCallbackAsync(cq.Id, "خطا رخ داد.");
        }
    }

    private async Task HandleMenuCallbackAsync(long chatId, long userId, int? messageId, string[] parts)
    {
        if (parts.Length < 2) return;
        switch (parts[1])
        {
            case "main": await ShowMainMenuAsync(chatId, userId, messageId); break;
            case "projects": await ShowProjectsAsync(chatId, userId, messageId); break;
            case "models": await ShowModelProvidersAsync(chatId, userId, messageId, refresh: true); break;
            case "sections": await ShowSectionsAsync(chatId, userId, messageId); break;
            case "actions": await ShowSectionMenuAsync(chatId, userId, messageId); break;
            case "reload": await ReloadProjectConfigAsync(chatId, userId, messageId); break;
            case "status": await ShowStatusAsync(chatId, userId, messageId); break;
            case "abort": await AbortPromptAsync(chatId, userId, messageId); break;
            case "stop": await StopProjectServerAsync(chatId, userId, messageId); break;
            case "help": await SendOrEditMenuAsync(chatId, messageId, HelpText(), MainMenuOnlyKeyboard()); break;
        }
    }

    private async Task HandleProjectCallbackAsync(long chatId, long userId, int? messageId, string[] parts)
    {
        if (parts.Length < 2) return;
        if (parts[1] == "page" && parts.Length == 4 && int.TryParse(parts[3], out var page))
        {
            await ShowProjectsAsync(chatId, userId, messageId, page);
            return;
        }
        if (parts[1] == "select" && parts.Length == 4 && TryResolveToken(parts[2], parts[3], chatId, userId, messageId, MenuItemKind.Project, out var token))
        {
            await SelectProjectPathAsync(chatId, userId, token.Value, messageId);
        }
        else
        {
            await SendOrEditMenuAsync(chatId, messageId, "این دکمه منقضی شده است. فهرست تازه را باز کنید.", ProjectsOnlyKeyboard());
        }
    }

    private async Task HandleModelCallbackAsync(long chatId, long userId, int? messageId, string[] parts)
    {
        if (parts.Length < 2) return;
        if (parts[1] == "refresh") { await ShowModelProvidersAsync(chatId, userId, messageId, refresh: true); return; }
        if (parts[1] == "default" && parts.Length == 3)
        {
            if (!TryValidateSnapshot(parts[2], chatId, userId, messageId))
                await SendOrEditMenuAsync(chatId, messageId, "این دکمه منقضی شده است. منوی مدل را دوباره باز کنید.", ChangeModelOnlyKeyboard());
            else
                await SetModelAsync(chatId, userId, null, messageId);
            return;
        }
        if (parts[1] == "provider" && parts.Length == 4 && TryResolveToken(parts[2], parts[3], chatId, userId, messageId, MenuItemKind.Provider, out var providerToken))
        {
            await ShowModelsForProviderAsync(chatId, userId, providerToken.Value, messageId);
            return;
        }
        if (parts[1] == "select" && parts.Length == 4 && TryResolveToken(parts[2], parts[3], chatId, userId, messageId, MenuItemKind.Model, out var modelToken))
        {
            await SetModelAsync(chatId, userId, modelToken.Value, messageId);
            return;
        }
        if (parts[1] == "page" && parts.Length == 5 && int.TryParse(parts[4], out var page) &&
            TryResolveToken(parts[2], parts[3], chatId, userId, messageId, MenuItemKind.Provider, out var providerPageToken))
        {
            await ShowModelsForProviderAsync(chatId, userId, providerPageToken.Value, messageId, page);
            return;
        }
        await SendOrEditMenuAsync(chatId, messageId, "این دکمه منقضی شده است. منوی مدل را دوباره باز کنید.", ChangeModelOnlyKeyboard());
    }

    private async Task HandleSectionCallbackAsync(long chatId, long userId, int? messageId, string[] parts)
    {
        if (parts.Length == 4 && parts[1] == "page" && int.TryParse(parts[3], out var page))
        {
            await ShowSectionsAsync(chatId, userId, messageId, page: page);
            return;
        }
        if (parts.Length == 4 && parts[1] == "select" && TryResolveToken(parts[2], parts[3], chatId, userId, messageId, MenuItemKind.Section, out var token))
            await SelectSectionAsync(chatId, userId, token.Value, messageId);
        else
            await ShowSectionsAsync(chatId, userId, messageId, "این دکمه منقضی شده است. فهرست بخش‌ها را تازه کنید.");
    }

    private async Task HandleCommandCallbackAsync(long chatId, long userId, int? messageId, string[] parts)
    {
        if (parts.Length == 4 && parts[1] == "run" && TryResolveToken(parts[2], parts[3], chatId, userId, messageId, MenuItemKind.Command, out var token))
        {
            await RunConfiguredCommandAsync(chatId, userId, token.Value, messageId);
            return;
        }
        if (parts.Length == 4 && parts[1] == "page" && int.TryParse(parts[3], out var page))
        {
            await ShowSectionMenuAsync(chatId, userId, messageId, page: page);
            return;
        }
        await ShowSectionMenuAsync(chatId, userId, messageId, "این دکمه منقضی شده است. فهرست اقدام‌ها را تازه کنید.");
    }

    private async Task ReloadProjectConfigAsync(long chatId, long userId, int? messageId)
    {
        var state = GetState(chatId);
        if (state.ProjectPath == null)
        {
            await ShowProjectsAsync(chatId, userId, messageId, prefix: "ابتدا پروژه را انتخاب کنید.");
            return;
        }
        var section = GetCurrentSection(state, out var loaded);
        if (loaded?.IsValid == true && section != null)
            await ShowSectionMenuAsync(chatId, userId, messageId, "تنظیمات پروژه دوباره خوانده شد.");
        else
            await ShowSectionsAsync(chatId, userId, messageId, "تنظیمات پروژه دوباره خوانده شد.");
    }

    private async Task HandlePermissionCallbackAsync(CallbackQuery cq, string data)
    {
        var parts = data.Split(':', 3);
        if (parts.Length != 3) return;
        var replyKind = parts[1];
        if (replyKind is not ("once" or "always" or "reject")) return;
        var requestId = parts[2];
        if (!_pendingPermissions.TryGetValue(requestId, out var pending))
        {
            if (cq.Message is { } staleMsg) await TryEditAsync(staleMsg.Chat.Id, staleMsg.MessageId, staleMsg.Text + "\n\n(No longer pending.)");
            return;
        }
        if (pending.ChatId != (cq.Message?.Chat.Id ?? cq.From.Id)) return;
        if (!_pendingPermissions.TryRemove(requestId, out pending)) return;

        var session = _openCode.ActiveSessions.FirstOrDefault(s => s.ProjectPath == pending.ProjectPath && !s.Exited);
        if (session == null)
        {
            await TryEditAsync(pending.ChatId, pending.MessageId, "⚠️ Could not reply — the OpenCode server for this project is no longer running.");
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
            await TryEditAsync(pending.ChatId, pending.MessageId, $"{outcome}\nProject: {pending.ProjectName}\nAction: {pending.Action}");
        }
        catch (Exception ex)
        {
            _log.Error($"[permission] reply failed for {requestId}: {ex.Message}");
            await TryEditAsync(pending.ChatId, pending.MessageId, $"⚠️ Failed to send your decision to OpenCode: {ex.Message}");
        }
    }

    private MenuSnapshot CreateSnapshot(long chatId, long userId, int messageId)
    {
        var snapshot = new MenuSnapshot { Id = NewToken(), ChatId = chatId, UserId = userId, MessageId = messageId };
        _snapshots[snapshot.Id] = snapshot;
        return snapshot;
    }

    private static string AddToken(MenuSnapshot snapshot, MenuItemKind kind, string value)
    {
        var token = NewToken();
        snapshot.Tokens[token] = new MenuItemToken { Token = token, Kind = kind, Value = value };
        return token;
    }

    private bool TryResolveToken(string snapshotId, string tokenId, long chatId, long userId, int? messageId, MenuItemKind kind, out MenuItemToken token)
    {
        token = null!;
        if (!TryValidateSnapshot(snapshotId, chatId, userId, messageId)) return false;
        if (!_snapshots[snapshotId].Tokens.TryGetValue(tokenId, out var resolved)) return false;
        token = resolved;
        return token.Kind == kind;
    }

    private bool TryValidateSnapshot(string snapshotId, long chatId, long userId, int? messageId)
    {
        CleanupSnapshots();
        if (!_snapshots.TryGetValue(snapshotId, out var snapshot)) return false;
        if (snapshot.ChatId != chatId || snapshot.UserId != userId) return false;
        return snapshot.MessageId == 0 || messageId == null || snapshot.MessageId == messageId;
    }

    private void CleanupSnapshots()
    {
        var cutoff = DateTimeOffset.UtcNow - SnapshotTtl;
        foreach (var pair in _snapshots.ToArray())
        {
            if (pair.Value.CreatedAt < cutoff) _snapshots.TryRemove(pair.Key, out _);
        }
        if (_snapshots.Count <= 100) return;
        foreach (var old in _snapshots.OrderBy(p => p.Value.CreatedAt).Take(_snapshots.Count - 100))
            _snapshots.TryRemove(old.Key, out _);
    }

    private static string NewToken() => Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant()[..12];

    private static List<string> BuildProjectLabels(List<(string Name, string Path)> projects)
    {
        return TelegramMenuLogic.BuildProjectLabels(projects);
    }

    private static void AddPager(List<InlineKeyboardButton[]> rows, string prefix, string snapshotId, int page, int count, string? extraToken = null)
    {
        var maxPage = Math.Max(0, (count - 1) / PageSize);
        if (maxPage == 0) return;
        var buttons = new List<InlineKeyboardButton>();
        var baseData = extraToken == null ? $"{prefix}:{snapshotId}" : $"{prefix}:{snapshotId}:{extraToken}";
        if (page > 0) buttons.Add(InlineKeyboardButton.WithCallbackData("⬅️ قبلی", $"{baseData}:{page - 1}"));
        buttons.Add(InlineKeyboardButton.WithCallbackData($"{page + 1}/{maxPage + 1}", "menu:noop"));
        if (page < maxPage) buttons.Add(InlineKeyboardButton.WithCallbackData("بعدی ➡️", $"{baseData}:{page + 1}"));
        rows.Add(buttons.ToArray());
    }

    private string? GetCurrentWorkingDirectory(ChatState state)
    {
        if (state.ProjectPath == null) return null;
        var project = _openCode.DiscoverProjects().FirstOrDefault(p => p.Path == state.ProjectPath);
        if (project == default) return null;
        var loaded = _projectConfigLoader.Load(project.Path, project.Name);
        if (!loaded.Exists) return project.Path;
        if (!loaded.IsValid || string.IsNullOrWhiteSpace(state.SectionId)) return null;
        var section = loaded.Config!.Sections.FirstOrDefault(s => string.Equals(s.Id, state.SectionId, StringComparison.OrdinalIgnoreCase));
        return section?.ResolvedDirectory;
    }

    private TelegramBridgeSectionConfig? GetCurrentSection(ChatState state, out ProjectConfigLoadResult? loaded)
    {
        loaded = null;
        if (state.ProjectPath == null) return null;
        var project = _openCode.DiscoverProjects().FirstOrDefault(p => p.Path == state.ProjectPath);
        if (project == default) return null;
        loaded = _projectConfigLoader.Load(project.Path, project.Name);
        if (!loaded.IsValid || string.IsNullOrWhiteSpace(state.SectionId)) return null;
        return loaded.Config!.Sections.FirstOrDefault(s => string.Equals(s.Id, state.SectionId, StringComparison.OrdinalIgnoreCase));
    }

    private string? GetSelectedSectionTitle(ChatState state)
    {
        var section = GetCurrentSection(state, out var loaded);
        if (loaded?.Exists == false) return "ریشه پروژه";
        return section?.Title;
    }

    private bool IsProjectRunning(string projectPath) => _openCode.ActiveSessions.Any(s => s.ProjectPath == projectPath && !s.Exited);
    private string? GetSelectedProjectName(string? projectPath) => projectPath == null ? null : Path.GetFileName(projectPath.TrimEnd('/'));

    private static InlineKeyboardMarkup MainMenuOnlyKeyboard() => new(new[] { new[] { InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "menu:main") } });
    private static InlineKeyboardMarkup ProjectsOnlyKeyboard() => new(new[] { new[] { InlineKeyboardButton.WithCallbackData("📁 انتخاب پروژه", "menu:projects") }, new[] { InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "menu:main") } });
    private static InlineKeyboardMarkup ChangeModelOnlyKeyboard() => new(new[] { new[] { InlineKeyboardButton.WithCallbackData("🧠 تغییر مدل", "menu:models") }, new[] { InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "menu:main") } });

    private static string HelpText() =>
        "❓ راهنما\n\n" +
        "با /menu منوی دکمه‌ای را باز کنید.\n" +
        "دستورهای متنی هنوز فعال هستند:\n" +
        "/projects، /use <name>، /models، /model <provider/model>، /model default، /status، /stop، /abort\n\n" +
        "بعد از انتخاب پروژه، هر متن معمولی به عنوان درخواست برای OpenCode ارسال می‌شود.";

    private async Task<long?> SendOrEditMenuAsync(long chatId, int? messageId, string text, InlineKeyboardMarkup replyMarkup)
    {
        if (_client == null) return null;
        if (messageId is { } mid)
        {
            try
            {
                var edited = await _client.EditMessageText(chatId, mid, text, replyMarkup: replyMarkup);
                return edited.MessageId;
            }
            catch (Exception ex)
            {
                _log.Warn($"Menu edit failed for {chatId}/{mid}: {ex.Message}; sending replacement");
            }
        }
        return await SendAsync(chatId, text, replyMarkup);
    }

    private async Task FinishAsync(long chatId, long? placeholderId, string text)
    {
        if (placeholderId is { } pid) await TryDeleteAsync(chatId, pid);
        var finalText = string.IsNullOrWhiteSpace(text) ? "✅ انجام شد." : text;
        var chunks = SplitMessage(finalText).ToList();
        for (var i = 0; i < chunks.Count; i++)
            await SendAsync(chatId, chunks[i], i == chunks.Count - 1 ? MainMenuOnlyKeyboard() : null);
    }

    private static IEnumerable<string> SplitMessage(string text)
    {
        if (text.Length <= TelegramMessageLimit) { yield return text; yield break; }
        for (var i = 0; i < text.Length; i += TelegramMessageLimit)
            yield return text.Substring(i, Math.Min(TelegramMessageLimit, text.Length - i));
    }

    private async Task AnswerCallbackAsync(string callbackQueryId, string? text)
    {
        if (_client == null) return;
        try { await _client.AnswerCallbackQuery(callbackQueryId, text); }
        catch { }
    }

    private async Task TryEditAsync(long chatId, long? messageId, string text)
    {
        if (_client == null || messageId is not { } mid) return;
        try { await _client.EditMessageText(chatId, (int)mid, text); }
        catch { }
    }

    private async Task<long?> SendAsync(long chatId, string text, InlineKeyboardMarkup? replyMarkup = null)
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
        catch { }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
