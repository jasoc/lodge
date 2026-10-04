using System.Runtime.Versioning;
using Lodge.Core.Abstractions;
using Lodge.Core.Catalog;
using Lodge.Core.Domain.Enums;
using Lodge.Infrastructure.Execution;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lodge.Tests;

/// <summary>Runs only with a real docker daemon and LODGE_DOCKER_TESTS=1 (it pulls images and builds the example playbook).</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LODGE_DOCKER_TESTS") != "1")
        {
            Skip = "set LODGE_DOCKER_TESTS=1 to run against a real docker daemon";
        }
    }
}

/// <summary>
/// The restrictive container profile against a real daemon: what the generated
/// <c>docker create</c> arguments actually produce, and the shipped example playbook
/// running under it.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class DockerIntegrationTests : IDisposable
{
    private const string Alpine = "alpine:3.21@sha256:ce64758a109eb420d874a118f87920e625e12d3634e03b4a5573fd9f6e5d3507";

    private readonly string _logDir = Directory.CreateTempSubdirectory("lodge-docker-it-").FullName;
    private readonly string _repoRoot;

    public DockerIntegrationTests()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "inventory")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        _repoRoot = dir ?? throw new InvalidOperationException("repo root not found");
    }

    public void Dispose() => Directory.Delete(_logDir, recursive: true);

    private ContainerRunbookExecutor NewExecutor()
    {
        var options = Options.Create(new DockerExecutorOptions { LogDirectory = _logDir });
        var docker = new DockerCli(options);
        return new ContainerRunbookExecutor(
            options, new PlaybookContextResolver(_repoRoot), new DockerImageBuilder(options, docker), new DockerContainerRunner(options, docker));
    }

    private static async Task<RunbookRunStatus> RunAsync(ContainerRunbookExecutor executor, RunbookExecutionRequest request)
    {
        var handle = await executor.StartAsync(request);
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            var status = await executor.GetStatusAsync(handle.RunId);
            if (status.State is RunbookRunState.Succeeded or RunbookRunState.Failed)
            {
                return status;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException();
    }

    private string Log(RunbookRunStatus status) => File.ReadAllText(Path.Combine(_logDir, $"{status.RunId}.log"));

    private static RunbookExecutionRequest Request(ContainerExecutorConfig config, Dictionary<string, string?>? parameters = null)
        => new("homelab", "lab", "probe/run", Guid.NewGuid(), parameters ?? new(), ExecutorKind.Container, config);

    [DockerFact]
    public async Task The_restrictive_defaults_hold_inside_a_real_container()
    {
        var script = """
            echo "uid=$(id -u)"; grep -E '^(CapEff|NoNewPrivs)' /proc/self/status
            touch /rootfs-write 2>/dev/null && echo "rootfs=WRITABLE" || echo "rootfs=read-only"
            touch /tmp/ok && echo "tmp=writable"; touch "$LODGE_WORK_DIR/ok" && echo "work=writable"
            """;
        var status = await RunAsync(NewExecutor(), Request(new ContainerExecutorConfig(
            Alpine, new[] { script }, new[] { "/bin/sh", "-c" })));

        Assert.Equal(RunbookRunState.Succeeded, status.State);
        var log = Log(status);
        Assert.Contains("uid=65534", log);
        Assert.Contains("CapEff:\t0000000000000000", log);
        Assert.Contains("NoNewPrivs:\t1", log);
        Assert.Contains("rootfs=read-only", log);
        Assert.Contains("tmp=writable", log);
        Assert.Contains("work=writable", log);
    }

    [DockerFact]
    public async Task Explicit_relaxations_take_effect()
    {
        var status = await RunAsync(NewExecutor(), Request(new ContainerExecutorConfig(
            Alpine, new[] { "echo uid=$(id -u); touch /rootfs-write && echo rootfs=WRITABLE; grep NoNewPrivs /proc/self/status" },
            new[] { "/bin/sh", "-c" },
            Security: new ContainerSecurity(NoNewPrivileges: false, ReadOnlyRootfs: false, User: ContainerSecurity.UserImage))));

        var log = Log(status);
        Assert.Equal(RunbookRunState.Succeeded, status.State);
        Assert.Contains("uid=0", log);
        Assert.Contains("rootfs=WRITABLE", log);
        Assert.Contains("NoNewPrivs:\t0", log);
    }

    [DockerFact]
    public async Task A_run_past_its_timeout_is_really_killed()
    {
        var started = DateTime.UtcNow;
        var status = await RunAsync(NewExecutor(), Request(new ContainerExecutorConfig(
            Alpine, new[] { "sleep 300" }, new[] { "/bin/sh", "-c" }, TimeoutSeconds: 2)));

        Assert.Equal(RunbookRunState.Failed, status.State);
        Assert.Contains("timed out after 2 s", status.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(60));
    }

    [DockerFact]
    public async Task Resource_limits_are_applied()
    {
        var status = await RunAsync(NewExecutor(), Request(new ContainerExecutorConfig(
            Alpine, new[] { "cat /sys/fs/cgroup/memory.max /sys/fs/cgroup/pids.max" }, new[] { "/bin/sh", "-c" },
            Resources: new ContainerResources("64m", 0.5, 32))));

        Assert.Equal(RunbookRunState.Succeeded, status.State);
        Assert.Contains("67108864", Log(status));
        Assert.Contains("32", Log(status));
    }

    [DockerFact]
    public async Task The_example_verify_playbook_builds_and_runs_under_the_restrictive_profile()
    {
        var provider = new InventoryCatalogLoader(_repoRoot, new PlaybookContextResolver(_repoRoot));
        var catalog = provider.LoadCatalog("homelab", "lab");
        Assert.Empty(catalog.Errors);
        var verify = catalog.Catalog.Capabilities.SelectMany(c => c.Signals).SelectMany(s => s.Rules)
            .SelectMany(r => r.Actions).Single(a => a.Key == "verify_vm").Container!;

        // Nothing listens on this loopback port inside the container, so the check must fail
        // — with the playbook's own message, which proves it built, started as non-root and ran.
        var status = await RunAsync(NewExecutor(), Request(verify, new() { ["host"] = "127.0.0.1/24", ["vm_name"] = "probe", ["port"] = "9" }));

        Assert.Equal(RunbookRunState.Failed, status.State);
        Assert.Contains("exit 1", status.Message);
        Assert.Contains("NO: nothing answers on 127.0.0.1:9", Log(status));
    }
}
