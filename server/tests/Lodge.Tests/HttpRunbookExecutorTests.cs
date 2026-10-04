using System.Net;
using System.Text.Json;
using Lodge.Core.Abstractions;
using Lodge.Core.Catalog;
using Lodge.Core.Domain.Enums;
using Lodge.Infrastructure.Execution;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lodge.Tests;

/// <summary>
/// Exercises <see cref="HttpRunbookExecutor"/> end to end against a fake message handler:
/// what actually goes on the wire (method, url, query, headers, body) after parameter
/// substitution, how the outcome is decided, and that secrets never reach the run log.
/// </summary>
public sealed class HttpRunbookExecutorTests : IDisposable
{
    private readonly string _logDir = Directory.CreateTempSubdirectory("lodge-http-test-").FullName;

    public void Dispose() => Directory.Delete(_logDir, recursive: true);

    private sealed class FakeHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }

    private (HttpRunbookExecutor Executor, FakeHandler Handler) NewExecutor(HttpStatusCode status = HttpStatusCode.OK, string response = "{\"ok\":true}")
    {
        var handler = new FakeHandler(status, response);
        var executor = new HttpRunbookExecutor(new HttpClient(handler),
            Options.Create(new HttpExecutorOptions { LogDirectory = _logDir }));
        return (executor, handler);
    }

    private static RunbookExecutionRequest Request(HttpExecutorConfig config, Dictionary<string, string?> parameters, params string[] secrets)
        => new("homelab", "lab", "notify/send", Guid.NewGuid(), parameters, ExecutorKind.Http, HttpConfig: config, SecretNames: secrets);

    private static async Task<RunbookRunStatus> WaitAsync(HttpRunbookExecutor executor, string runId)
    {
        for (var i = 0; i < 200; i++)
        {
            var status = await executor.GetStatusAsync(runId);
            if (status.State != RunbookRunState.Running)
            {
                return status;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("run did not finish");
    }

    [Fact]
    public async Task Substitutes_parameters_into_url_query_headers_and_a_structured_body()
    {
        var (executor, handler) = NewExecutor();
        var config = new HttpExecutorConfig(
            "PUT",
            "https://api.test/zones/{{ zone }}/records",
            Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer {{ token }}" },
            Query: new Dictionary<string, string> { ["name"] = "{{ name }}" },
            Body: """{"record":"{{ item }}","note":"for {{ name }}","ttl":1}""",
            BodyIsJson: true);
        var parameters = new Dictionary<string, string?>
        {
            ["zone"] = "abc123",
            ["token"] = "s3cr3t-token",
            ["name"] = "a b",
            ["item"] = """{"type":"A","content":"192.0.2.1"}"""
        };

        var handle = await executor.StartAsync(Request(config, parameters, "token"));
        var status = await WaitAsync(executor, handle.RunId);

        Assert.Equal(RunbookRunState.Succeeded, status.State);
        Assert.Equal(HttpMethod.Put, handler.Request!.Method);
        Assert.Equal("https://api.test/zones/abc123/records?name=a%20b", handler.Request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer s3cr3t-token", handler.Request.Headers.Authorization!.ToString());

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("A", body.RootElement.GetProperty("record").GetProperty("type").GetString()); // embedded, not a string
        Assert.Equal("for a b", body.RootElement.GetProperty("note").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("ttl").GetInt32());
    }

    [Fact]
    public async Task Secrets_are_masked_in_the_run_log()
    {
        var (executor, _) = NewExecutor(response: "echo s3cr3t-token back");
        var config = new HttpExecutorConfig("POST", "https://api.test/hook?key={{ token }}", Body: "token={{ token }}");

        var handle = await executor.StartAsync(Request(config, new() { ["token"] = "s3cr3t-token" }, "token"));
        await WaitAsync(executor, handle.RunId);

        var log = (await executor.ReadLogAsync(handle.RunId, 0, 1 << 16))!.Text;
        Assert.DoesNotContain("s3cr3t-token", log);
        Assert.Contains("***", log);
        Assert.Contains("<- 200", log);
    }

    [Fact]
    public async Task An_unexpected_status_fails_the_run()
    {
        var (executor, _) = NewExecutor(HttpStatusCode.Conflict, "already exists");
        var handle = await executor.StartAsync(Request(new HttpExecutorConfig("POST", "https://api.test/x"), new()));

        var status = await WaitAsync(executor, handle.RunId);

        Assert.Equal(RunbookRunState.Failed, status.State);
        Assert.Contains("409", status.Message);
    }

    [Fact]
    public async Task Expect_status_overrides_the_2xx_default()
    {
        var (executor, _) = NewExecutor(HttpStatusCode.NotFound, "");
        var config = new HttpExecutorConfig("DELETE", "https://api.test/x", ExpectStatus: new[] { 200, 404 });

        var status = await WaitAsync(executor, (await executor.StartAsync(Request(config, new()))).RunId);

        Assert.Equal(RunbookRunState.Succeeded, status.State);
    }

    [Fact]
    public async Task A_reference_to_an_unknown_parameter_fails_before_anything_is_sent()
    {
        var (executor, handler) = NewExecutor();
        var config = new HttpExecutorConfig("POST", "https://api.test/{{ missing }}");

        var status = await WaitAsync(executor, (await executor.StartAsync(Request(config, new()))).RunId);

        Assert.Equal(RunbookRunState.Failed, status.State);
        Assert.Contains("missing", status.Message);
        Assert.Null(handler.Request);
    }
}
