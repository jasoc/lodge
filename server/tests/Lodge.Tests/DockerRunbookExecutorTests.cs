using System.Runtime.Versioning;
using System.Text.Json;
using Lodge.Core.Abstractions;
using Lodge.Core.Catalog;
using Lodge.Core.Domain.Enums;
using Lodge.Infrastructure.Execution;
using Lodge.Infrastructure.Git;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lodge.Tests;

/// <summary>
/// Exercises <see cref="DockerRunbookExecutor"/> against a real spawned process — not a
/// mock, an actual argv/env round-trip — but against a small fake "docker" script rather
/// than a real docker daemon, which can't be assumed in CI. The fake script appends every
/// invocation's argv (and, for <c>run</c>, every LODGE_* env var it received) to a calls
/// file, and simulates the image cache: <c>build</c> marks a tag as present,
/// <c>image inspect</c> reports it. Paths and exit codes are baked into each test's own
/// script, so tests never share process-global environment. Unix-only.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class DockerRunbookExecutorTests : IDisposable
{
    private readonly string _scratchDir = Directory.CreateTempSubdirectory("lodge-docker-test-").FullName;
    private string CallsFile => Path.Combine(_scratchDir, "calls.txt");
    private string RepoRoot => Path.Combine(_scratchDir, "repo");

    public void Dispose() => Directory.Delete(_scratchDir, recursive: true);

    private DockerRunbookExecutor NewExecutor(int runExit = 0, int buildExit = 0, string? logDir = null)
    {
        var scriptPath = Path.Combine(_scratchDir, $"fake-docker-{Guid.NewGuid():N}.sh");
        var builtDir = Path.Combine(_scratchDir, "built");
        Directory.CreateDirectory(builtDir);
        File.WriteAllText(scriptPath, $$"""
            #!/bin/sh
            calls='{{CallsFile}}'
            built='{{builtDir}}'
            echo "CALL $*" >> "$calls"
            case "$1 $2" in
              "image inspect")
                tag=$(echo "$5" | tr '/:' '__')
                [ -f "$built/$tag" ] && exit 0 || exit 1 ;;
              "image ls"|"image rm")
                exit 0 ;;
            esac
            if [ "$1" = "build" ]; then
              tag=$(echo "$3" | tr '/:' '__')
              [ {{buildExit}} -eq 0 ] && touch "$built/$tag"
              exit {{buildExit}}
            fi
            i=1
            for a in "$@"; do
              echo "ARGV[$i]=$a" >> "$calls"
              i=$((i+1))
            done
            env | grep '^LODGE_' | sort >> "$calls"
            exit {{runExit}}
            """.ReplaceLineEndings("\n"));
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return new DockerRunbookExecutor(
            Options.Create(new DockerExecutorOptions
            {
                DockerBinaryPath = scriptPath,
                LogDirectory = logDir ?? Path.Combine(_scratchDir, "logs")
            }),
            new PlaybookContextResolver(Options.Create(new GitSnapshotOptions { RepoRoot = RepoRoot })));
    }

    private static async Task<RunbookRunStatus> WaitUntilTerminalAsync(DockerRunbookExecutor executor, string runId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var status = await executor.GetStatusAsync(runId);
            if (status.State is RunbookRunState.Succeeded or RunbookRunState.Failed)
            {
                return status;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException($"Run '{runId}' did not reach a terminal state in time.");
    }

    private static RunbookExecutionRequest Request(DockerExecutorConfig? config, Dictionary<string, string?>? parameters = null)
        => new("homelab", "personal", "homelab-ops/probe", Guid.NewGuid(),
            parameters ?? new Dictionary<string, string?>(), ExecutorKind.Docker, config);

    private string[] Calls() => File.Exists(CallsFile) ? File.ReadAllLines(CallsFile) : Array.Empty<string>();

    private static string Arg(IEnumerable<string> lines, int index)
        => lines.Single(l => l.StartsWith($"ARGV[{index}]=", StringComparison.Ordinal)).Split('=', 2)[1];

    private DockerBuildConfig WritePlaybook(string body = "echo hi", IReadOnlyDictionary<string, string>? args = null)
    {
        var dir = Path.Combine(RepoRoot, "inventory", "homelab", "playbooks", "probe");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Dockerfile"), "FROM alpine:3.20\nCOPY run.sh /run.sh\n");
        File.WriteAllText(Path.Combine(dir, "run.sh"), body);
        var build = new DockerBuildConfig("playbooks/probe", Args: args);
        var fingerprint = new PlaybookContextResolver(Options.Create(new GitSnapshotOptions { RepoRoot = RepoRoot }))
            .ComputeFingerprint("homelab", build);
        return build with { Fingerprint = fingerprint };
    }

    [Fact]
    public async Task Image_run_passes_params_as_env_names_only_and_never_targets_a_remote_host()
    {
        var executor = NewExecutor();
        var config = new DockerExecutorConfig("homelab/toolbox:latest", new[] { "ansible-profile" });
        var handle = await executor.StartAsync(Request(config, new Dictionary<string, string?>
        {
            ["docker_host"] = "192.168.178.200",
            ["registry_token"] = "s3cr3t",
            ["spec"] = "{\"cores\":4}"
        }));
        Assert.StartsWith("docker-", handle.RunId);

        var status = await WaitUntilTerminalAsync(executor, handle.RunId);
        Assert.Equal(RunbookRunState.Succeeded, status.State);

        var calls = Calls();
        Assert.Equal("run", Arg(calls, 1));
        Assert.Equal("--rm", Arg(calls, 2));
        Assert.DoesNotContain(calls, l => l.Contains("ssh://") || l == "ARGV[1]=-H");

        // Values only ever travel through the environment — never through argv.
        var argv = calls.Where(l => l.StartsWith("ARGV[")).ToList();
        Assert.Contains("ARGV[" + (argv.FindIndex(l => l.EndsWith("=LODGE_PARAM_REGISTRY_TOKEN")) + 1) + "]=LODGE_PARAM_REGISTRY_TOKEN", argv);
        Assert.DoesNotContain(argv, l => l.Contains("s3cr3t"));
        Assert.Contains("LODGE_PARAM_REGISTRY_TOKEN=s3cr3t", calls);
        Assert.Contains("LODGE_PARAM_DOCKER_HOST=192.168.178.200", calls);
        Assert.Contains("LODGE_KIND_CODE=homelab", calls);
        Assert.Contains("LODGE_INSTANCE_CODE=personal", calls);
        Assert.Contains("LODGE_RUNBOOK_REF=homelab-ops/probe", calls);

        // The whole map, with JSON-shaped values embedded structured.
        var paramsJson = calls.Single(l => l.StartsWith("LODGE_PARAMS_JSON=", StringComparison.Ordinal))["LODGE_PARAMS_JSON=".Length..];
        using var doc = JsonDocument.Parse(paramsJson);
        Assert.Equal(4, doc.RootElement.GetProperty("spec").GetProperty("cores").GetInt32());
        Assert.Equal("192.168.178.200", doc.RootElement.GetProperty("docker_host").GetString());

        Assert.Equal("homelab/toolbox:latest", argv[^2].Split('=', 2)[1]);
        Assert.Equal("ansible-profile", argv[^1].Split('=', 2)[1]);
    }

    [Fact]
    public async Task Entrypoint_head_becomes_the_flag_and_its_tail_leads_the_command()
    {
        var executor = NewExecutor();
        var config = new DockerExecutorConfig("alpine:3.20", new[] { "echo $LODGE_PARAM_X" }, new[] { "/bin/sh", "-c" });
        var handle = await executor.StartAsync(Request(config));
        await WaitUntilTerminalAsync(executor, handle.RunId);

        var argv = Calls().Where(l => l.StartsWith("ARGV[")).Select(l => l.Split('=', 2)[1]).ToList();
        var flag = argv.IndexOf("--entrypoint");
        Assert.Equal("/bin/sh", argv[flag + 1]);
        Assert.Equal(new[] { "alpine:3.20", "-c", "echo $LODGE_PARAM_X" }, argv[^3..]);
    }

    [Fact]
    public async Task Reports_failure_on_a_nonzero_exit_code()
    {
        var executor = NewExecutor(runExit: 7);
        var handle = await executor.StartAsync(Request(new DockerExecutorConfig("alpine:3.20", Array.Empty<string>())));
        var status = await WaitUntilTerminalAsync(executor, handle.RunId);

        Assert.Equal(RunbookRunState.Failed, status.State);
        Assert.Contains("exit 7", status.Message);
    }

    [Fact]
    public async Task Throws_when_the_request_carries_no_docker_config()
    {
        var executor = NewExecutor();
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.StartAsync(Request(null)));
    }

    [Fact]
    public async Task Build_playbook_is_built_once_then_served_from_cache()
    {
        var build = WritePlaybook(args: new Dictionary<string, string> { ["ALPINE_VERSION"] = "3.20" });
        var config = new DockerExecutorConfig(null, Array.Empty<string>(), Build: build);
        var executor = NewExecutor();

        var first = await WaitUntilTerminalAsync(executor, (await executor.StartAsync(Request(config))).RunId);
        var second = await WaitUntilTerminalAsync(executor, (await executor.StartAsync(Request(config))).RunId);
        Assert.Equal(RunbookRunState.Succeeded, first.State);
        Assert.Equal(RunbookRunState.Succeeded, second.State);

        var calls = Calls();
        var expectedTag = $"lodge-playbook/homelab/playbooks/probe:{build.Fingerprint![..16]}";
        var buildCall = Assert.Single(calls, l => l.StartsWith("CALL build ", StringComparison.Ordinal));
        Assert.Contains($"--tag {expectedTag}", buildCall);
        Assert.Contains("--build-arg ALPINE_VERSION=3.20", buildCall);
        Assert.Contains($"--label lodge.fingerprint={build.Fingerprint}", buildCall);
        Assert.EndsWith(Path.Combine(RepoRoot, "inventory", "homelab", "playbooks", "probe"), buildCall);
        Assert.Equal(2, calls.Count(l => l.StartsWith("CALL run ", StringComparison.Ordinal) && l.Contains(expectedTag)));
    }

    [Fact]
    public async Task Build_playbook_changed_since_emission_is_refused_without_touching_docker()
    {
        var build = WritePlaybook("echo approved");
        WritePlaybook("echo something else entirely");
        var executor = NewExecutor();

        var status = await WaitUntilTerminalAsync(executor,
            (await executor.StartAsync(Request(new DockerExecutorConfig(null, Array.Empty<string>(), Build: build)))).RunId);

        Assert.Equal(RunbookRunState.Failed, status.State);
        Assert.Contains("changed since this action was emitted", status.Message);
        Assert.Empty(Calls());
    }

    [Fact]
    public async Task Build_playbook_without_a_fingerprint_is_refused()
    {
        var build = WritePlaybook() with { Fingerprint = null };
        var executor = NewExecutor();

        var status = await WaitUntilTerminalAsync(executor,
            (await executor.StartAsync(Request(new DockerExecutorConfig(null, Array.Empty<string>(), Build: build)))).RunId);

        Assert.Equal(RunbookRunState.Failed, status.State);
        Assert.Contains("could not be resolved", status.Message);
        Assert.Empty(Calls());
    }

    [Fact]
    public async Task A_failed_build_fails_the_run_and_never_runs_the_container()
    {
        var build = WritePlaybook();
        var executor = NewExecutor(buildExit: 1);

        var status = await WaitUntilTerminalAsync(executor,
            (await executor.StartAsync(Request(new DockerExecutorConfig(null, Array.Empty<string>(), Build: build)))).RunId);

        Assert.Equal(RunbookRunState.Failed, status.State);
        Assert.Contains("building", status.Message);
        Assert.DoesNotContain(Calls(), l => l.StartsWith("CALL run ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Terminal_status_is_recovered_from_the_sidecar_after_executor_recreation()
    {
        var logDir = Path.Combine(_scratchDir, "logs-sidecar");
        var executor = NewExecutor(logDir: logDir);
        var handle = await executor.StartAsync(Request(new DockerExecutorConfig("alpine:3.20", Array.Empty<string>())));
        await WaitUntilTerminalAsync(executor, handle.RunId);

        // Simulate a server restart: a brand-new executor instance, same log dir, has no
        // in-memory record of this run — status must come from the sidecar file.
        var recreated = NewExecutor(logDir: logDir);
        var status = await recreated.GetStatusAsync(handle.RunId);
        Assert.Equal(RunbookRunState.Succeeded, status.State);
    }

    [Fact]
    public async Task Run_log_can_be_tailed_by_offset()
    {
        var executor = NewExecutor();
        var handle = await executor.StartAsync(Request(new DockerExecutorConfig("alpine:3.20", Array.Empty<string>())));
        await WaitUntilTerminalAsync(executor, handle.RunId);

        var logPath = Path.Combine(_scratchDir, "logs", $"{handle.RunId}.log");
        File.WriteAllText(logPath, "first\n");
        var first = await executor.ReadLogAsync(handle.RunId, 0, 1024);
        Assert.Equal("first\n", first!.Text);

        File.AppendAllText(logPath, "second\n");
        var next = await executor.ReadLogAsync(handle.RunId, first.NextOffset, 1024);
        Assert.Equal("second\n", next!.Text);
        Assert.Equal(new FileInfo(logPath).Length, next.NextOffset);
    }

    [Fact]
    public async Task Run_log_chunks_never_split_a_utf8_character()
    {
        var executor = NewExecutor();
        var handle = await executor.StartAsync(Request(new DockerExecutorConfig("alpine:3.20", Array.Empty<string>())));
        await WaitUntilTerminalAsync(executor, handle.RunId);
        File.WriteAllText(Path.Combine(_scratchDir, "logs", $"{handle.RunId}.log"), "aè");   // 'è' is 2 bytes

        var head = await executor.ReadLogAsync(handle.RunId, 0, maxBytes: 2);
        Assert.Equal("a", head!.Text);
        Assert.Equal(1, head.NextOffset);
        var tail = await executor.ReadLogAsync(handle.RunId, head.NextOffset, maxBytes: 2);
        Assert.Equal("è", tail!.Text);
    }

    [Theory]
    [InlineData("docker-../../etc/passwd")]
    [InlineData("shell-0123456789abcdef0123456789abcdef")]
    [InlineData("docker-nope")]
    public async Task Run_log_rejects_foreign_or_malformed_run_ids(string runId)
    {
        var executor = NewExecutor();
        Assert.Null(await executor.ReadLogAsync(runId, 0, 1024));
    }
}
