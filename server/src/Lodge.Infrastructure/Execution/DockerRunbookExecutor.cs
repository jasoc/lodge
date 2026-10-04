using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lodge.Core.Abstractions;
using Lodge.Core.Catalog;
using Microsoft.Extensions.Options;

namespace Lodge.Infrastructure.Execution;

/// <summary>
/// Runs an action inside a container via the docker CLI against the local daemon (the
/// host's socket mounted into Lodge's container, or whatever <c>DOCKER_HOST</c> points
/// at) — for tooling Lodge's own minimal server container doesn't bundle (ansible,
/// terraform, ...). What runs comes from the catalog-declared <see
/// cref="DockerExecutorConfig"/>: either a ready-made image, or a playbook folder of the
/// inventory that is built once and cached under a content-addressed tag
/// (<c>lodge-playbook/&lt;kind&gt;/&lt;context&gt;:&lt;fingerprint&gt;</c>).
///
/// Parameter contract (identical for both variants, runtime only — never build args):
/// <list type="bullet">
/// <item><c>LODGE_PARAM_&lt;NAME&gt;</c> per parameter;</item>
/// <item><c>LODGE_PARAMS_JSON</c>, the whole map as one JSON object (values that are
/// themselves JSON objects/arrays, like <c>from: item</c>, are embedded structured);</item>
/// <item><c>LODGE_ACTION_ID</c>, <c>LODGE_KIND_CODE</c>, <c>LODGE_INSTANCE_CODE</c>,
/// <c>LODGE_ACTION_REF</c>.</item>
/// </list>
/// Values travel as <c>-e NAME</c> (name only) with the value in the docker CLI's own
/// environment, so resolved secrets never appear in any process argv.
///
/// A <c>build</c> action carries the fingerprint it was emitted (and approved) with; the
/// folder is re-fingerprinted right before building, and a mismatch fails the run
/// instead of building different code — the next reconciliation cycle then supersedes it
/// with a fresh action to confirm.
///
/// Run-state tracking: an in-memory run
/// dict for the common case plus a JSON sidecar file so a run survives being asked about
/// after a restart in a *degraded but honest* way — outcome reported as unknown rather
/// than guessed.
/// </summary>
public sealed class DockerRunbookExecutor : IRunbookExecutor, IRunbookLogReader
{
    private readonly DockerExecutorOptions _options;
    private readonly PlaybookContextResolver _playbooks;
    private readonly ConcurrentDictionary<string, RunState> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _buildLocks = new(StringComparer.Ordinal);

    public DockerRunbookExecutor(IOptions<DockerExecutorOptions> options, PlaybookContextResolver playbooks)
    {
        _options = options.Value;
        _playbooks = playbooks;
        Directory.CreateDirectory(_options.LogDirectory);
    }

    public Task<RunbookRunHandle> StartAsync(RunbookExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.DockerConfig is null)
        {
            throw new InvalidOperationException(
                $"Action '{request.ActionRef}' is routed to the Docker executor but carries no DockerExecutorConfig — " +
                "this is a catalog/persistence bug, not a user error (the loader guarantees a docker config for 'executor: docker').");
        }

        var runId = $"docker-{Guid.NewGuid():N}";
        var state = new RunState(
            request.ActionRef,
            Path.Combine(_options.LogDirectory, $"{runId}.log"),
            Path.Combine(_options.LogDirectory, $"{runId}.json"),
            DateTimeOffset.UtcNow);
        WriteSidecar(state, outcome: null, completedAt: null);

        _runs[runId] = state;
        state.Completion = Task.Run(() => RunPipelineAsync(request, request.DockerConfig, state));

        return Task.FromResult(new RunbookRunHandle(runId, RunbookRunState.Running));
    }

    public Task<RunbookRunStatus> GetStatusAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (_runs.TryGetValue(runId, out var state))
        {
            if (state.Completion is not { IsCompleted: true } completion)
            {
                return Task.FromResult(new RunbookRunStatus(runId, RunbookRunState.Running,
                    $"Action '{state.ActionRef}': {state.Phase}.", DateTimeOffset.UtcNow));
            }

            var outcome = completion.Result;
            return Task.FromResult(new RunbookRunStatus(runId,
                outcome.Succeeded ? RunbookRunState.Succeeded : RunbookRunState.Failed,
                outcome.Message, state.CompletedAt ?? DateTimeOffset.UtcNow));
        }

        return Task.FromResult(StatusFromSidecar(runId));
    }

    public Task<RunbookLogChunk?> ReadLogAsync(string runId, long offset, int maxBytes, CancellationToken cancellationToken = default)
        => RunLogFile.ReadAsync(_options.LogDirectory, "docker-", runId, offset, maxBytes, cancellationToken);

    private async Task<RunOutcome> RunPipelineAsync(RunbookExecutionRequest request, DockerExecutorConfig config, RunState state)
    {
        RunOutcome outcome;
        try
        {
            var image = config.Image;
            if (config.Build is { } build)
            {
                state.Phase = "building playbook image";
                var built = await EnsurePlaybookImageAsync(request.KindCode, build, state);
                if (built.Failure is not null)
                {
                    outcome = built.Failure;
                    return Finish(state, outcome);
                }
                image = built.Image;
            }

            state.Phase = "running";
            var exitCode = await RunDockerAsync(BuildRunArgs(request, config, image!, out var environment), environment, state.LogPath);
            outcome = exitCode == 0
                ? new RunOutcome(true, 0, $"Action '{state.ActionRef}' completed (exit 0). Log: {state.LogPath}")
                : new RunOutcome(false, exitCode, $"Action '{state.ActionRef}' failed (exit {exitCode}). Log: {state.LogPath}");
        }
        catch (Exception ex)
        {
            AppendLog(state.LogPath, $"[lodge] {ex.Message}");
            outcome = new RunOutcome(false, null, $"Action '{state.ActionRef}' failed: {ex.Message}");
        }

        return Finish(state, outcome);
    }

    private RunOutcome Finish(RunState state, RunOutcome outcome)
    {
        state.CompletedAt = DateTimeOffset.UtcNow;
        WriteSidecar(state, outcome, state.CompletedAt);
        return outcome;
    }

    /// <summary>
    /// Verifies the playbook folder still matches the fingerprint the action was emitted
    /// with, then returns its cached image — building it first if this daemon doesn't
    /// have that tag yet. Builds of the same tag are serialized so two actions sharing a
    /// playbook never build it twice concurrently.
    /// </summary>
    private async Task<(string? Image, RunOutcome? Failure)> EnsurePlaybookImageAsync(string kindCode, DockerBuildConfig build, RunState state)
    {
        var where = $"playbook 'inventory/{kindCode}/{build.Context}'";
        if (build.Fingerprint is null)
        {
            return (null, Fail(state, $"{where} could not be resolved when this action was emitted (see the cycle's validation errors). " +
                "Fix the folder; the next reconciliation cycle re-queues the action."));
        }

        string current;
        try
        {
            current = _playbooks.ComputeFingerprint(kindCode, build);
        }
        catch (PlaybookContextException ex)
        {
            return (null, Fail(state, $"{where}: {ex.Message}"));
        }

        if (!string.Equals(current, build.Fingerprint, StringComparison.Ordinal))
        {
            return (null, Fail(state,
                $"{where} changed since this action was emitted (approved {Short(build.Fingerprint)}, now {Short(current)}). " +
                "Refusing to run unapproved code; the next reconciliation cycle supersedes this action with a fresh one to confirm."));
        }

        var repository = ImageRepository(kindCode, build.Context);
        var image = $"{repository}:{Short(build.Fingerprint)}";

        var gate = _buildLocks.GetOrAdd(image, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (await RunDockerAsync(new[] { "image", "inspect", "--format", "{{.Id}}", image }, null, logPath: null) == 0)
            {
                AppendLog(state.LogPath, $"[lodge] using cached image {image}");
                return (image, null);
            }

            AppendLog(state.LogPath, $"[lodge] building {image} from inventory/{kindCode}/{build.Context}");
            var contextDir = _playbooks.ResolveContextDirectory(kindCode, build);
            var args = new List<string>
            {
                "build",
                "--tag", image,
                "--label", "lodge.managed=true",
                "--label", $"lodge.playbook={kindCode}/{build.Context}",
                "--label", $"lodge.fingerprint={build.Fingerprint}",
                "--file", _playbooks.ResolveDockerfile(contextDir, build)
            };
            if (build.Target is not null)
            {
                args.Add("--target");
                args.Add(build.Target);
            }
            foreach (var (name, value) in build.Args ?? new Dictionary<string, string>())
            {
                args.Add("--build-arg");
                args.Add($"{name}={value}");
            }
            args.Add(contextDir);

            var exitCode = await RunDockerAsync(args, null, state.LogPath);
            if (exitCode != 0)
            {
                return (null, new RunOutcome(false, exitCode,
                    $"Action '{state.ActionRef}': building {image} failed (exit {exitCode}). Log: {state.LogPath}"));
            }

            await PruneOldImagesAsync(kindCode, build.Context, keep: image, state.LogPath);
            return (image, null);
        }
        finally
        {
            gate.Release();
        }
    }

    private RunOutcome Fail(RunState state, string message)
    {
        AppendLog(state.LogPath, $"[lodge] {message}");
        return new RunOutcome(false, null, $"Action '{state.ActionRef}' refused: {message}");
    }

    /// <summary>Best-effort: keeps the newest <see cref="DockerExecutorOptions.KeepImagesPerPlaybook"/> images of one playbook, never the one just built.</summary>
    private async Task PruneOldImagesAsync(string kindCode, string context, string keep, string logPath)
    {
        if (_options.KeepImagesPerPlaybook <= 0)
        {
            return;
        }

        var listing = new StringBuilder();
        var exit = await RunDockerAsync(
            new[] { "image", "ls", "--filter", $"label=lodge.playbook={kindCode}/{context}", "--format", "{{.Repository}}:{{.Tag}}" },
            null, logPath: null, capture: listing);
        if (exit != 0)
        {
            return;
        }

        // `docker image ls` lists newest first.
        var stale = listing.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => tag != keep && !tag.EndsWith(":<none>", StringComparison.Ordinal))
            .Skip(Math.Max(0, _options.KeepImagesPerPlaybook - 1))
            .ToList();
        foreach (var tag in stale)
        {
            // An image still used by a running container just fails to delete — fine.
            if (await RunDockerAsync(new[] { "image", "rm", tag }, null, logPath: null) == 0)
            {
                AppendLog(logPath, $"[lodge] pruned old playbook image {tag}");
            }
        }
    }

    private List<string> BuildRunArgs(
        RunbookExecutionRequest request, DockerExecutorConfig config, string image, out Dictionary<string, string> environment)
    {
        // Static env from the catalog first; LODGE_* names are reserved (the loader
        // rejects them there), so inputs can never be shadowed.
        environment = new Dictionary<string, string>(config.Env ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            ["LODGE_ACTION_ID"] = request.ActionId.ToString(),
            ["LODGE_KIND_CODE"] = request.KindCode,
            ["LODGE_INSTANCE_CODE"] = request.InstanceCode,
            ["LODGE_ACTION_REF"] = request.ActionRef
        };

        var paramsJson = new JsonObject();
        foreach (var (key, value) in request.Parameters)
        {
            environment[ParamEnvName(key)] = value ?? string.Empty;
            paramsJson[key] = ToJsonNode(value);
        }
        environment["LODGE_PARAMS_JSON"] = paramsJson.ToJsonString();

        var args = new List<string> { "run", "--rm", "--label", "lodge.managed=true", "--label", $"lodge.action_id={request.ActionId}" };
        foreach (var name in environment.Keys)
        {
            args.Add("-e");
            args.Add(name);
        }
        foreach (var mount in config.Mounts ?? Array.Empty<DockerMount>())
        {
            args.Add("--mount");
            args.Add(ResolveMount(mount));
        }

        // `--entrypoint` only takes the executable; any further entrypoint elements are
        // ordinary leading argv, exactly how docker itself composes ENTRYPOINT + CMD.
        var command = config.Command;
        if (config.Entrypoint is { Count: > 0 } entrypoint)
        {
            args.Add("--entrypoint");
            args.Add(entrypoint[0]);
            command = entrypoint.Skip(1).Concat(config.Command).ToList();
        }

        args.Add(image);
        args.AddRange(command);
        return args;
    }

    /// <summary>
    /// Turns a catalog mount alias into a <c>--mount</c> spec via <see
    /// cref="DockerExecutorOptions.Mounts"/>. An alias the operator didn't configure fails
    /// the run — the inventory can only reach what the server explicitly exposes.
    /// </summary>
    private string ResolveMount(DockerMount mount)
    {
        if (!_options.Mounts.TryGetValue(mount.Alias, out var source) || string.IsNullOrWhiteSpace(source))
        {
            throw new InvalidOperationException(
                $"mount alias '{mount.Alias}' is not configured — add DockerExecutor:Mounts:{mount.Alias} " +
                "(a host path, or volume:<name>) to the server configuration.");
        }

        const string volumePrefix = "volume:";
        var spec = source.StartsWith(volumePrefix, StringComparison.Ordinal)
            ? $"type=volume,source={source[volumePrefix.Length..]},target={mount.Target}"
            : $"type=bind,source={source},target={mount.Target}";
        return mount.ReadOnly ? spec + ",readonly" : spec;
    }

    private static string ParamEnvName(string key)
        => "LODGE_PARAM_" + new string(key.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>Strings stay strings, except ones that are themselves a JSON object/array (e.g. <c>from: item</c>), which are embedded structured.</summary>
    private static JsonNode? ToJsonNode(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                return JsonNode.Parse(value);
            }
            catch (JsonException)
            {
                // Not JSON after all — keep it as the plain string it is.
            }
        }

        return JsonValue.Create(value);
    }

    /// <summary>
    /// Runs the docker CLI to completion. With a <paramref name="logPath"/>, both streams
    /// are appended to it (serialized so lines never interleave mid-write); otherwise they
    /// are discarded, or collected into <paramref name="capture"/> (stdout only).
    /// </summary>
    private async Task<int> RunDockerAsync(
        IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment, string? logPath, StringBuilder? capture = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.DockerBinaryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{_options.DockerBinaryPath}'.");

        var logLock = new object();
        var stdout = capture is not null
            ? CaptureAsync(process.StandardOutput, capture)
            : PipeAsync(process.StandardOutput.BaseStream, logPath, logLock);
        var stderr = PipeAsync(process.StandardError.BaseStream, logPath, logLock);

        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode;
    }

    private static async Task CaptureAsync(StreamReader reader, StringBuilder capture)
        => capture.Append(await reader.ReadToEndAsync());

    private static async Task PipeAsync(Stream source, string? logPath, object logLock)
    {
        var buffer = new byte[4096];
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            if (logPath is null)
            {
                continue;
            }
            lock (logLock)
            {
                using var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                fs.Write(buffer, 0, read);
            }
        }
    }

    private static void AppendLog(string logPath, string line)
        => File.AppendAllText(logPath, line + Environment.NewLine);

    /// <summary><c>lodge-playbook/&lt;kind&gt;/&lt;context path&gt;</c>, each segment squeezed into docker's repository-name grammar.</summary>
    private static string ImageRepository(string kindCode, string context)
    {
        static string Segment(string raw)
        {
            var chars = raw.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-' ? c : '-');
            var cleaned = new string(chars.ToArray()).Trim('.', '_', '-');
            return cleaned.Length == 0 ? "x" : cleaned;
        }

        var segments = new[] { "lodge-playbook", Segment(kindCode) }
            .Concat(context.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Segment));
        return string.Join('/', segments);
    }

    private static string Short(string fingerprint) => fingerprint.Length > 16 ? fingerprint[..16] : fingerprint;

    private RunbookRunStatus StatusFromSidecar(string runId)
    {
        var sidecarPath = Path.Combine(_options.LogDirectory, $"{runId}.json");
        if (!File.Exists(sidecarPath))
        {
            return new RunbookRunStatus(runId, RunbookRunState.Failed, "Unknown run.", DateTimeOffset.UtcNow);
        }

        var sidecar = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(sidecarPath))!;
        if (sidecar.CompletedAt is { } completedAt)
        {
            // ExitCode 0 also covers sidecars written before the Succeeded flag existed.
            return sidecar.Succeeded || sidecar.ExitCode == 0
                ? new RunbookRunStatus(runId, RunbookRunState.Succeeded, sidecar.Message, completedAt)
                : new RunbookRunStatus(runId, RunbookRunState.Failed, sidecar.Message, completedAt);
        }

        // After a restart the container is no longer our child process: its exit
        // code isn't reliably recoverable, so report the outcome as unknown rather than
        // guessing.
        return new RunbookRunStatus(runId, RunbookRunState.Failed,
            $"Action '{sidecar.ActionRef}' outcome unknown: the server restarted while this run was in progress.",
            DateTimeOffset.UtcNow);
    }

    private static void WriteSidecar(RunState state, RunOutcome? outcome, DateTimeOffset? completedAt)
    {
        var sidecar = new Sidecar(state.ActionRef, state.StartedAt, completedAt,
            outcome?.Succeeded ?? false, outcome?.ExitCode, outcome?.Message);
        File.WriteAllText(state.SidecarPath, JsonSerializer.Serialize(sidecar));
    }

    private sealed class RunState(string actionRef, string logPath, string sidecarPath, DateTimeOffset startedAt)
    {
        public string ActionRef { get; } = actionRef;
        public string LogPath { get; } = logPath;
        public string SidecarPath { get; } = sidecarPath;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public volatile string Phase = "starting";
        public DateTimeOffset? CompletedAt { get; set; }
        public Task<RunOutcome>? Completion { get; set; }
    }

    private sealed record RunOutcome(bool Succeeded, int? ExitCode, string Message);

    private sealed record Sidecar(
        string ActionRef, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, bool Succeeded, int? ExitCode, string? Message);
}
