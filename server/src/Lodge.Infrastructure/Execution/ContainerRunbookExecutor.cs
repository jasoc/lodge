using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lodge.Core.Abstractions;
using Lodge.Core.Catalog;
using Lodge.Core.Domain.Enums;
using Microsoft.Extensions.Options;

namespace Lodge.Infrastructure.Execution;

/// <summary>
/// Runs an action inside a container — for tooling Lodge's own minimal server container
/// doesn't bundle (ansible, terraform, ...). What runs comes from the catalog-declared <see
/// cref="ContainerExecutorConfig"/>: either a ready-made image, or a playbook folder of the
/// inventory that is built once and cached under a content-addressed tag
/// (<c>lodge-playbook/&lt;kind&gt;/&lt;context&gt;:&lt;fingerprint&gt;</c>). Where it runs is
/// the injected runtime — an <see cref="IImageBuilder"/> and an <see
/// cref="IContainerRunner"/> (Docker today); everything else, approval included, lives here
/// and doesn't change with the runtime.
///
/// Parameter contract (identical for both variants, runtime only — never build args):
/// <list type="bullet">
/// <item><c>LODGE_PARAM_&lt;NAME&gt;</c> per parameter;</item>
/// <item><c>LODGE_PARAMS_JSON</c>, the whole map as one JSON object (values that are
/// themselves JSON objects/arrays, like <c>from: item</c>, are embedded structured);</item>
/// <item><c>LODGE_ACTION_ID</c>, <c>LODGE_KIND_CODE</c>, <c>LODGE_INSTANCE_CODE</c>,
/// <c>LODGE_ACTION_REF</c>.</item>
/// </list>
/// The runner must keep every value out of any process argv (Docker: <c>-e NAME</c> with
/// the value in the CLI's own environment), since resolved secrets are among them.
///
/// A <c>build</c> action carries the fingerprint it was emitted (and approved) with; the
/// folder is re-fingerprinted right before building, and a mismatch fails the run
/// instead of building different code — the next reconciliation cycle then supersedes it
/// with a fresh action to confirm.
///
/// Run-state tracking: an in-memory run dict for the common case plus a JSON sidecar file,
/// written before anything is launched. After a restart a run without a recorded outcome
/// is reattached: its container, found by its <see cref="ContainerLabels.RunId"/> label,
/// is followed to completion and its real exit code collected. Only a run whose container
/// is gone reports its outcome as unknown, rather than guessed; a run id with no sidecar
/// never started. Starting a run id that already has a sidecar never launches it again.
/// </summary>
public sealed class ContainerRunbookExecutor : IRunbookExecutor, IRunbookLogReader
{
    private readonly DockerExecutorOptions _options;
    private readonly PlaybookContextResolver _playbooks;
    private readonly IImageBuilder _builder;
    private readonly IContainerRunner _runner;
    private readonly ConcurrentDictionary<string, RunState> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _buildLocks = new(StringComparer.Ordinal);

    public ContainerRunbookExecutor(
        IOptions<DockerExecutorOptions> options, PlaybookContextResolver playbooks, IImageBuilder builder, IContainerRunner runner)
    {
        _options = options.Value;
        _playbooks = playbooks;
        _builder = builder;
        _runner = runner;
        Directory.CreateDirectory(_options.LogDirectory);
    }

    // "docker-" predates other runtimes; kept so earlier runs' logs stay readable.
    private const string RunIdPrefix = "docker-";

    public string AllocateRunId(ExecutorKind kind) => $"{RunIdPrefix}{Guid.NewGuid():N}";

    public Task<RunbookRunHandle> StartAsync(RunbookExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ContainerConfig is null)
        {
            throw new InvalidOperationException(
                $"Action '{request.ActionRef}' is routed to the container executor but carries no ContainerExecutorConfig — " +
                "this is a catalog/persistence bug, not a user error (the loader guarantees a container config for 'executor: container').");
        }

        var runId = request.RunId ?? AllocateRunId(ExecutorKind.Container);
        RunLogFile.EnsureValidRunId(RunIdPrefix, runId);
        var handle = new RunbookRunHandle(runId, RunbookRunState.Running);

        var state = new RunState(
            request.ActionRef,
            Path.Combine(_options.LogDirectory, $"{runId}.log"),
            Path.Combine(_options.LogDirectory, $"{runId}.json"),
            DateTimeOffset.UtcNow);
        lock (_runs)
        {
            // Already started under this id — by this process, or by one before a restart
            // (its sidecar is written before anything is launched): never launch it twice.
            if (_runs.ContainsKey(runId) || File.Exists(state.SidecarPath))
            {
                return Task.FromResult(handle);
            }
            WriteSidecar(state, outcome: null, completedAt: null);
            _runs[runId] = state;
        }

        state.Completion = Task.Run<RunOutcome?>(() => RunPipelineAsync(request, request.ContainerConfig, state, runId));
        return Task.FromResult(handle);
    }

    public Task<RunbookRunStatus> GetStatusAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(runId, out var state))
        {
            if (StatusFromSidecar(runId) is { } settled)
            {
                return Task.FromResult(settled);
            }
            state = BeginReattach(runId);
        }

        if (state.Completion is not { IsCompleted: true } completion)
        {
            return Task.FromResult(new RunbookRunStatus(runId, RunbookRunState.Running,
                $"Action '{state.ActionRef}': {state.Phase}.", DateTimeOffset.UtcNow));
        }

        if (completion.Result is not { } outcome)
        {
            // The runtime couldn't be asked (daemon unreachable): still running as far as
            // anyone knows — the next status read tries again.
            _runs.TryRemove(KeyValuePair.Create(runId, state));
            return Task.FromResult(new RunbookRunStatus(runId, RunbookRunState.Running,
                $"Action '{state.ActionRef}': {state.Phase}.", DateTimeOffset.UtcNow));
        }

        return Task.FromResult(new RunbookRunStatus(runId,
            outcome.Succeeded ? RunbookRunState.Succeeded : RunbookRunState.Failed,
            outcome.Message, state.CompletedAt ?? DateTimeOffset.UtcNow));
    }

    public Task<RunbookLogChunk?> ReadLogAsync(string runId, long offset, int maxBytes, CancellationToken cancellationToken = default)
        => RunLogFile.ReadAsync(_options.LogDirectory, "docker-", runId, offset, maxBytes, cancellationToken);

    private async Task<RunOutcome?> RunPipelineAsync(
        RunbookExecutionRequest request, ContainerExecutorConfig config, RunState state, string runId)
    {
        RunOutcome outcome;
        try
        {
            var image = config.Image;
            if (config.Build is { } build)
            {
                SetPhase(state, Phases.Building);
                var built = await EnsurePlaybookImageAsync(request.KindCode, build, state);
                if (built.Failure is not null)
                {
                    outcome = built.Failure;
                    return Finish(state, outcome);
                }
                image = built.Image;
            }

            var timeout = TimeoutFor(config);
            state.Deadline = timeout is { } limit ? DateTimeOffset.UtcNow + limit : null;
            SetPhase(state, Phases.Running);
            outcome = ExitOutcome(state, await _runner.RunAsync(RunSpec(request, config, image!, runId, timeout), state.LogPath), timeout);
        }
        catch (Exception ex)
        {
            AppendLog(state.LogPath, $"[lodge] {ex.Message}");
            outcome = new RunOutcome(false, null, $"Action '{state.ActionRef}' failed: {ex.Message}");
        }

        return Finish(state, outcome);
    }

    /// <summary>How long the run may last: the action's own <c>timeout_seconds</c>, else the server default; null (0) = no limit.</summary>
    private TimeSpan? TimeoutFor(ContainerExecutorConfig config)
    {
        var seconds = config.TimeoutSeconds ?? _options.DefaultTimeoutSeconds;
        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    private static RunOutcome ExitOutcome(RunState state, ContainerRunResult result, TimeSpan? timeout)
        => result.TimedOut
            ? new RunOutcome(false, result.ExitCode,
                $"Action '{state.ActionRef}' timed out{(timeout is { } t ? $" after {t.TotalSeconds:0} s" : "")} and was killed. Log: {state.LogPath}")
            : result.ExitCode == 0
                ? new RunOutcome(true, 0, $"Action '{state.ActionRef}' completed (exit 0). Log: {state.LogPath}")
                : new RunOutcome(false, result.ExitCode, $"Action '{state.ActionRef}' failed (exit {result.ExitCode}). Log: {state.LogPath}");

    private RunOutcome Finish(RunState state, RunOutcome outcome)
    {
        state.CompletedAt = DateTimeOffset.UtcNow;
        WriteSidecar(state, outcome, state.CompletedAt);
        return outcome;
    }

    private static void SetPhase(RunState state, string phase)
    {
        state.Phase = phase;
        WriteSidecar(state, outcome: null, completedAt: null);
    }

    /// <summary>
    /// Picks up a run this process didn't start — the server restarted while it was in
    /// flight: its container is followed to completion and its real exit code becomes the
    /// outcome. Registered once per run id, however many readers ask at the same time.
    /// </summary>
    private RunState BeginReattach(string runId)
    {
        var sidecar = ReadSidecar(runId)!;
        var state = new RunState(
            sidecar.ActionRef,
            Path.Combine(_options.LogDirectory, $"{runId}.log"),
            Path.Combine(_options.LogDirectory, $"{runId}.json"),
            sidecar.StartedAt) { Phase = "reattaching after a server restart" };

        lock (_runs)
        {
            if (_runs.TryGetValue(runId, out var existing))
            {
                return existing;
            }
            _runs[runId] = state;
        }

        var phaseBeforeRestart = sidecar.Phase;
        state.Deadline = sidecar.DeadlineAt;
        state.Completion = Task.Run(() => ReattachPipelineAsync(runId, state, phaseBeforeRestart));
        return state;
    }

    private async Task<RunOutcome?> ReattachPipelineAsync(string runId, RunState state, string? phaseBeforeRestart)
    {
        ContainerRunResult? result;
        try
        {
            result = await _runner.ReattachAsync(runId, state.LogPath, state.Deadline);
        }
        catch (Exception ex)
        {
            AppendLog(state.LogPath, $"[lodge] could not reattach yet: {ex.Message}");
            return null;
        }

        state.Phase = Phases.Running;
        if (result is { } ended)
        {
            return Finish(state, ExitOutcome(state, ended, state.Deadline is { } at ? at - state.StartedAt : null));
        }

        // No container carries this run id. Before the "running" phase none was ever
        // created (the restart hit the build); after it, its exit code is lost.
        var message = phaseBeforeRestart is Phases.Running
            ? $"Action '{state.ActionRef}' outcome unknown: the server restarted while this run was in progress and its container is gone."
            : $"Action '{state.ActionRef}' never ran: the server restarted before its container started. Retry the action.";
        AppendLog(state.LogPath, $"[lodge] {message}");
        return Finish(state, new RunOutcome(false, null, message));
    }

    /// <summary>
    /// Verifies the playbook folder still matches the fingerprint the action was emitted
    /// with, then returns its cached image — building it first if this daemon doesn't
    /// have that tag yet. Builds of the same tag are serialized so two actions sharing a
    /// playbook never build it twice concurrently.
    /// </summary>
    private async Task<(string? Image, RunOutcome? Failure)> EnsurePlaybookImageAsync(string kindCode, ContainerBuildConfig build, RunState state)
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
            if (await _builder.ExistsAsync(image))
            {
                AppendLog(state.LogPath, $"[lodge] using cached image {image}");
                return (image, null);
            }

            AppendLog(state.LogPath, $"[lodge] building {image} from inventory/{kindCode}/{build.Context}");
            var contextDir = _playbooks.ResolveContextDirectory(kindCode, build);
            var spec = new ImageBuildSpec(
                image,
                $"{kindCode}/{build.Context}",
                build.Fingerprint,
                contextDir,
                _playbooks.ResolveDockerfile(contextDir, build),
                build.Target,
                build.Args,
                _playbooks.ResolveAdditionalContexts(kindCode, build));

            var exitCode = await _builder.BuildAsync(spec, state.LogPath);
            if (exitCode != 0)
            {
                return (null, new RunOutcome(false, exitCode,
                    $"Action '{state.ActionRef}': building {image} failed (exit {exitCode}). Log: {state.LogPath}"));
            }

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

    private static ContainerRunSpec RunSpec(
        RunbookExecutionRequest request, ContainerExecutorConfig config, string image, string runId, TimeSpan? timeout)
    {
        // Static env from the catalog first; LODGE_* names are reserved (the loader
        // rejects them there), so inputs can never be shadowed.
        var environment = new Dictionary<string, string>(config.Env ?? new Dictionary<string, string>(), StringComparer.Ordinal)
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

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ContainerLabels.Managed] = "true",
            [ContainerLabels.ActionId] = request.ActionId.ToString(),
            [ContainerLabels.RunId] = runId
        };

        // The writable work dir of a read-only container, for scripts that need scratch space.
        environment["LODGE_WORK_DIR"] = DockerContainerRunner.WorkDir;

        return new ContainerRunSpec(
            runId, image, environment, config.Entrypoint, config.Command, labels,
            config.Resources, config.Security, config.Network, timeout,
            SecretMasker.For(request.SecretNames, request.Parameters));
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

    /// <summary>
    /// The settled status of a run this process isn't tracking: its recorded outcome, or
    /// "never started" when it has no sidecar at all. Null when the sidecar records no
    /// outcome yet — a run in flight across a restart, to reattach to.
    /// </summary>
    private RunbookRunStatus? StatusFromSidecar(string runId)
    {
        if (!runId.StartsWith(RunIdPrefix, StringComparison.Ordinal) || runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return new RunbookRunStatus(runId, RunbookRunState.Failed, "Unknown run.", DateTimeOffset.UtcNow);
        }

        var sidecar = ReadSidecar(runId);
        if (sidecar is null)
        {
            return new RunbookRunStatus(runId, RunbookRunState.Failed, RunLogFile.NeverStartedMessage(runId), DateTimeOffset.UtcNow);
        }

        if (sidecar.CompletedAt is { } completedAt)
        {
            // ExitCode 0 also covers sidecars written before the Succeeded flag existed.
            return sidecar.Succeeded || sidecar.ExitCode == 0
                ? new RunbookRunStatus(runId, RunbookRunState.Succeeded, sidecar.Message, completedAt)
                : new RunbookRunStatus(runId, RunbookRunState.Failed, sidecar.Message, completedAt);
        }

        return null;
    }

    private Sidecar? ReadSidecar(string runId)
    {
        var sidecarPath = Path.Combine(_options.LogDirectory, $"{runId}.json");
        return File.Exists(sidecarPath) ? JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(sidecarPath)) : null;
    }

    private static void WriteSidecar(RunState state, RunOutcome? outcome, DateTimeOffset? completedAt)
    {
        var sidecar = new Sidecar(state.ActionRef, state.StartedAt, completedAt,
            outcome?.Succeeded ?? false, outcome?.ExitCode, outcome?.Message, state.Phase, state.Deadline);
        lock (state)
        {
            File.WriteAllText(state.SidecarPath, JsonSerializer.Serialize(sidecar));
        }
    }

    /// <summary>Where a run is, as recorded in its sidecar — what a restart needs to tell "never ran" from "lost".</summary>
    private static class Phases
    {
        public const string Starting = "starting";
        public const string Building = "building playbook image";
        public const string Running = "running";
    }

    private sealed class RunState(string actionRef, string logPath, string sidecarPath, DateTimeOffset startedAt)
    {
        public string ActionRef { get; } = actionRef;
        public string LogPath { get; } = logPath;
        public string SidecarPath { get; } = sidecarPath;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public volatile string Phase = Phases.Starting;
        public DateTimeOffset? Deadline { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }

        /// <summary>The run's outcome; null when the runtime couldn't be asked and a later read should try again.</summary>
        public Task<RunOutcome?>? Completion { get; set; }
    }

    private sealed record RunOutcome(bool Succeeded, int? ExitCode, string Message);

    private sealed record Sidecar(
        string ActionRef, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, bool Succeeded, int? ExitCode, string? Message,
        string? Phase = null, DateTimeOffset? DeadlineAt = null);
}
