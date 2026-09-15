using System.Text.Json;
using OpenCodeTelegramBridge.Models;
using OpenCodeTelegramBridge.Services;

// Resolve all app-relative paths (data/, wwwroot/, appsettings.json) against the directory the
// executable itself lives in, not the process's current working directory. This makes the app
// behave identically whether launched via "dotnet run" from the project folder, a published
// single-file binary invoked from an arbitrary cwd, or a systemd service (whose WorkingDirectory
// should already be correct, but this removes the dependency on that being set right).
var appDir = AppContext.BaseDirectory;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = appDir,
});
builder.WebHost.UseUrls("http://0.0.0.0:5080");
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<ActivityLog>();
builder.Services.AddSingleton<OpenCodeManager>();
builder.Services.AddSingleton<ProjectConfigLoader>();
builder.Services.AddSingleton<XrayBinaryProvider>();
builder.Services.AddSingleton<ProxyTunnelManager>();
builder.Services.AddSingleton<TelegramBridgeService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelegramBridgeService>());

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

// ───────────────────────── Dashboard API ─────────────────────────

app.MapGet("/api/status", (TelegramBridgeService tg, OpenCodeManager oc, ConfigStore cfg) =>
{
    var sessions = oc.ActiveSessions.Select(s => new
    {
        project = Path.GetFileName(s.ProjectPath.TrimEnd('/')),
        path = s.ProjectPath,
        port = s.Port,
        startedAt = s.StartedAt,
        lastActivity = s.LastActivity,
        exited = s.Exited,
    });
    return Results.Json(new
    {
        connected = tg.IsConnected,
        botUsername = tg.BotUsername,
        lastError = tg.LastError,
        configComplete = cfg.Current.IsComplete,
        activeSessions = sessions,
    }, jsonOpts);
});

app.MapGet("/api/config", (ConfigStore cfg) => Results.Json(cfg.Current, jsonOpts));

app.MapPost("/api/config", (AppConfig config, ConfigStore cfg, TelegramBridgeService tg, ActivityLog log) =>
{
    cfg.Save(config);
    log.Info("Configuration saved from dashboard — reconnecting…");
    _ = tg.ApplyConfigAsync(config);
    return Results.Json(new { ok = true });
});

app.MapGet("/api/projects", (OpenCodeManager oc) =>
    Results.Json(oc.DiscoverProjects().Select(p => new { name = p.Name, path = p.Path }), jsonOpts));

app.MapPost("/api/projects/{name}/stop", async (string name, OpenCodeManager oc) =>
{
    var match = oc.DiscoverProjects().FirstOrDefault(p => p.Name == name);
    if (match == default) return Results.NotFound();
    await oc.StopAsync(match.Path);
    return Results.Json(new { ok = true });
});

app.MapGet("/api/log", (ActivityLog log) => Results.Json(log.Snapshot(), jsonOpts));

app.MapGet("/api/log/stream", async (HttpContext ctx, ActivityLog log, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";

    var channel = System.Threading.Channels.Channel.CreateUnbounded<LogEntry>();
    void Handler(LogEntry e) => channel.Writer.TryWrite(e);
    log.OnEntry += Handler;
    try
    {
        await foreach (var entry in channel.Reader.ReadAllAsync(ct))
        {
            var json = JsonSerializer.Serialize(entry, jsonOpts);
            await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { /* client disconnected */ }
    finally { log.OnEntry -= Handler; }
});

app.Run();
