using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenCodeTelegramBridge.Services;

namespace OpenCodeTelegramBridge.Tests;

public class OpenCodeCommandRunnerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("provider/model/nested", "B021  extra arguments")]
    public async Task Native_command_preserves_arguments_and_uses_command_agent(string? model, string arguments)
    {
        using var handler = new CommandServer();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var approvals = 0;
        var run = new OpenCodeCommandRunner(http).RunAsync("ses_current", "/backend-task", arguments, model, p =>
        {
            Assert.Equal("per_current", p.GetProperty("id").GetString());
            approvals++;
            handler.Complete(HttpStatusCode.OK, """{"info":{},"parts":[{"type":"text","text":"Task ready"},{"type":"tool","text":"not output"}]}""");
            return Task.CompletedTask;
        }, timeout.Token);

        await handler.Dispatched.Task.WaitAsync(timeout.Token);
        Assert.True(handler.Subscribed);
        Assert.Equal("/session/ses_current/command", handler.Path);
        Assert.Equal("backend-task", handler.Body.GetProperty("command").GetString());
        Assert.Equal(arguments, handler.Body.GetProperty("arguments").GetString());
        Assert.False(handler.Body.TryGetProperty("agent", out _));
        Assert.False(handler.Body.TryGetProperty("permission", out _));
        Assert.False(handler.Body.TryGetProperty("parts", out _));
        if (model == null) Assert.False(handler.Body.TryGetProperty("model", out _));
        else Assert.Equal(model, handler.Body.GetProperty("model").GetString());

        // An unrelated session must not create a Telegram permission request. Idle must
        // not complete this command before its HTTP result is received.
        await handler.EventAsync("""{"type":"permission.asked","properties":{"sessionID":"other","id":"per_other"}}""");
        await handler.EventAsync("""{"type":"session.idle","properties":{"sessionID":"ses_current"}}""");
        await handler.EventAsync("""{"type":"permission.asked","properties":{"sessionID":"ses_current","id":"per_current"}}""");

        Assert.Equal("Task ready", await run);
        Assert.Equal(1, approvals);
        Assert.Equal(1, handler.PostCount);
    }

    [Theory]
    [InlineData(404, "{}")]
    [InlineData(200, "{\"info\":{\"error\":{\"name\":\"APIError\"}},\"parts\":[]}")]
    [InlineData(200, "{}")]
    public async Task Failure_is_reported_without_prompt_fallback(int status, string response)
    {
        using var handler = new CommandServer();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = new OpenCodeCommandRunner(http).RunAsync("ses_current", "missing", "", null, _ => Task.CompletedTask, timeout.Token);
        await handler.Dispatched.Task.WaitAsync(timeout.Token);
        handler.Complete((HttpStatusCode)status, response);
        await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Equal(1, handler.PostCount);
    }

    [Fact]
    public async Task Cancellation_stops_waiting_for_command_and_permissions()
    {
        using var handler = new CommandServer();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var run = new OpenCodeCommandRunner(http).RunAsync("ses_current", "backend-task", "", null, _ => Task.CompletedTask, cancel.Token);
        await handler.Dispatched.Task.WaitAsync(timeout.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(timeout.Token));
    }

    [Fact]
    public async Task Disconnected_event_stream_fails_instead_of_waiting_forever_for_approval()
    {
        using var handler = new CommandServer();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = new OpenCodeCommandRunner(http).RunAsync("ses_current", "backend-task", "", null, _ => Task.CompletedTask, timeout.Token);
        await handler.Dispatched.Task.WaitAsync(timeout.Token);
        await handler.CloseEventsAsync();
        await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
    }

    private sealed class CommandServer : HttpMessageHandler
    {
        private readonly Pipe _events = new();
        private readonly TaskCompletionSource<HttpResponseMessage> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Dispatched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Subscribed { get; private set; }
        public string? Path { get; private set; }
        public JsonElement Body { get; private set; }
        public int PostCount { get; private set; }

        public async Task EventAsync(string json) => await _events.Writer.WriteAsync(Encoding.UTF8.GetBytes($"data: {json}\n\n"));
        public async Task CloseEventsAsync() => await _events.Writer.CompleteAsync();
        public void Complete(HttpStatusCode status, string json) =>
            _response.TrySetResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("/event", request.RequestUri!.AbsolutePath);
                Subscribed = true;
                await EventAsync("""{"type":"server.connected","properties":{}}""");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(_events.Reader.AsStream()) };
            }
            Assert.True(Subscribed);
            Path = request.RequestUri!.AbsolutePath;
            PostCount++;
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Body = doc.RootElement.Clone();
            Dispatched.TrySetResult();
            return await _response.Task.WaitAsync(ct);
        }
    }
}
