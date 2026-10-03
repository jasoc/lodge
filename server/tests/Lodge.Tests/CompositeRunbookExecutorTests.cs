using System.Runtime.Versioning;
using Lodge.Core.Abstractions;
using Lodge.Core.Catalog;
using Lodge.Core.Domain.Enums;
using Lodge.Infrastructure.Execution;
using Lodge.Infrastructure.Git;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lodge.Tests;

/// <summary>
/// Exercises <see cref="CompositeRunbookExecutor"/>'s dispatch: explicit routing by
/// <see cref="RunbookExecutionRequest.ExecutorKind"/>, never inference from the runbook
/// string, and a loud failure for reserved not-yet-implemented kinds. Unix-only (relies
/// on the shell/docker executors' own POSIX assumptions).
/// </summary>
[UnsupportedOSPlatform("windows")]
public class CompositeRunbookExecutorTests
{
    private static CompositeRunbookExecutor NewComposite()
    {
        var shell = new ShellCommandRunbookExecutor(Options.Create(new ShellExecutorOptions
        {
            LogDirectory = Directory.CreateTempSubdirectory("lodge-composite-shell-").FullName
        }));
        // Not disposed here: the composite (and its webhook executor) outlives this
        // factory method — none of these tests exercise a real HTTP call anyway (the
        // webhook test hits the missing-alias guard before ever touching the network).
        var http = new HttpClient();
        var webhook = new WebhookRunbookExecutor(http, Options.Create(new WebhookExecutorOptions()));
        var docker = new DockerRunbookExecutor(Options.Create(new DockerExecutorOptions
        {
            DockerBinaryPath = "/bin/true",
            LogDirectory = Directory.CreateTempSubdirectory("lodge-composite-docker-").FullName
        }), new PlaybookContextResolver(Options.Create(new GitSnapshotOptions())));
        return new CompositeRunbookExecutor(webhook, shell, docker);
    }

    [Fact]
    public async Task Shell_executor_kind_routes_to_the_shell_executor()
    {
        var composite = NewComposite();
        var handle = await composite.StartAsync(new RunbookExecutionRequest(
            "acme", "demo", "true", Guid.NewGuid(), new Dictionary<string, string?>(), ExecutorKind.Shell));

        Assert.StartsWith("shell-", handle.RunId);
    }

    [Fact]
    public async Task Docker_executor_kind_routes_to_the_docker_executor()
    {
        var composite = NewComposite();
        var config = new DockerExecutorConfig("irrelevant:latest", new[] { "noop" });
        var handle = await composite.StartAsync(new RunbookExecutionRequest(
            "acme", "demo", "homelab-ops/noop", Guid.NewGuid(), new Dictionary<string, string?>(), ExecutorKind.Docker, config));

        Assert.StartsWith("docker-", handle.RunId);
    }

    [Fact]
    public async Task Webhook_executor_kind_routes_to_the_webhook_executor()
    {
        var composite = NewComposite();
        // No entry configured in WebhookExecutor:Runbooks — proves dispatch reached the
        // webhook executor specifically (its own guard fires), not shell/docker.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => composite.StartAsync(
            new RunbookExecutionRequest("acme", "demo", "notify", Guid.NewGuid(), new Dictionary<string, string?>(), ExecutorKind.Webhook)));
        Assert.Contains("executor: webhook", ex.Message);
    }

    [Theory]
    [InlineData(ExecutorKind.Octopus)]
    [InlineData(ExecutorKind.Kubernetes)]
    public async Task Reserved_executor_kinds_fail_loudly_instead_of_falling_back_to_shell(ExecutorKind kind)
    {
        var composite = NewComposite();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => composite.StartAsync(
            new RunbookExecutionRequest("acme", "demo", "whatever", Guid.NewGuid(), new Dictionary<string, string?>(), kind)));
        Assert.Contains(kind.ToString(), ex.Message);
    }
}
