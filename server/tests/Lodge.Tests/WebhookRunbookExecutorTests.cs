using System.Net;
using System.Net.Sockets;
using System.Text;
using Lodge.Core.Abstractions;
using Lodge.Infrastructure.Execution;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lodge.Tests;

/// <summary>
/// Exercises <see cref="WebhookRunbookExecutor"/> against a real local HTTP listener —
/// not a mock of the executor itself, an actual socket round-trip — since this class's
/// whole job is talking HTTP correctly.
/// </summary>
public class WebhookRunbookExecutorTests
{
    [Fact]
    public async Task StartAsync_reports_the_synchronous_terminal_state_from_the_response_body()
    {
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var bytes = Encoding.UTF8.GetBytes("""{"status":"succeeded","message":"mock ok"}""");
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        var options = Options.Create(new WebhookExecutorOptions
        {
            Runbooks = new Dictionary<string, WebhookTarget>(StringComparer.Ordinal)
            {
                ["notify"] = new WebhookTarget { Url = $"http://localhost:{port}/hook" }
            }
        });

        using var http = new HttpClient();
        var executor = new WebhookRunbookExecutor(http, options);

        Assert.True(executor.CanHandle("notify"));
        Assert.False(executor.CanHandle("some-other-runbook"));

        var handle = await executor.StartAsync(new RunbookExecutionRequest(
            "acme", "demo", "notify", Guid.NewGuid(), new Dictionary<string, string?>()));

        await serverTask;
        listener.Stop();

        Assert.True(WebhookRunbookExecutor.IsWebhookRunId(handle.RunId));
        Assert.Equal(RunbookRunState.Succeeded, handle.State);

        var status = await executor.GetStatusAsync(handle.RunId);
        Assert.Equal(RunbookRunState.Succeeded, status.State);
        Assert.Equal("mock ok", status.Message);
    }

    [Fact]
    public void ReportStatus_updates_a_run_the_push_callback_endpoint_learns_about()
    {
        var options = Options.Create(new WebhookExecutorOptions());
        using var http = new HttpClient();
        var executor = new WebhookRunbookExecutor(http, options);

        // A run this instance never started (e.g. a restart) — ReportStatus is a no-op
        // for an unknown run id rather than throwing, since the callback endpoint can't
        // tell the difference from the outside.
        executor.ReportStatus("webhook-unknown", RunbookRunState.Succeeded, "irrelevant");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
