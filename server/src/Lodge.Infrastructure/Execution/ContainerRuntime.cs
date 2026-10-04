using Lodge.Core.Catalog;

namespace Lodge.Infrastructure.Execution;

/// <summary>
/// Builds playbook images for <see cref="ContainerRunbookExecutor"/> on one container
/// runtime. The executor owns everything runtime-independent — the approved-fingerprint
/// check, the image name, serializing builds of one tag — so an implementation only
/// answers "is this image there?" and "build it".
/// </summary>
public interface IImageBuilder
{
    /// <summary>Whether <paramref name="image"/> is already available to the runtime (the build cache hit).</summary>
    Task<bool> ExistsAsync(string image, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds <see cref="ImageBuildSpec.Image"/>, appending the build output to
    /// <paramref name="logPath"/>; the exit code of the build (0 = built). An implementation
    /// may prune older images of the same playbook afterwards, best-effort.
    /// </summary>
    Task<int> BuildAsync(ImageBuildSpec spec, string logPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// One image to build. <see cref="Playbook"/> (<c>kind/context</c>) identifies the
/// playbook across fingerprints, for labels and pruning; paths are absolute and already
/// confined to the kind folder by <see cref="PlaybookContextResolver"/>.
/// </summary>
public sealed record ImageBuildSpec(
    string Image,
    string Playbook,
    string Fingerprint,
    string ContextDirectory,
    string DockerfilePath,
    string? Target,
    IReadOnlyDictionary<string, string>? Args,
    IReadOnlyList<KeyValuePair<string, string>> AdditionalContexts);

/// <summary>
/// Runs one container to completion on one container runtime. A container outlives the
/// server process that started it: it is found again by its <see cref="ContainerLabels.RunId"/>
/// label, so a restart reattaches to it and reads its real exit code instead of guessing.
/// </summary>
public interface IContainerRunner
{
    /// <summary>
    /// Runs <paramref name="spec"/>, appending its output (secrets masked) to
    /// <paramref name="logPath"/>; returns how it ended. The container is labelled with
    /// <see cref="ContainerLabels"/>, killed when <see cref="ContainerRunSpec.Timeout"/>
    /// passes, and removed once its exit code is known.
    /// </summary>
    Task<ContainerRunResult> RunAsync(ContainerRunSpec spec, string logPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the container of <paramref name="runId"/> (running or already exited) and
    /// follows it to completion like <see cref="RunAsync"/> would have,
    /// killing it at <paramref name="deadline"/> if it has one; returns how it ended, or null
    /// when no container carries that run id (it never started, or its outcome was already
    /// collected). The resolved secrets died with the process that started the run, so the
    /// output from the reattach on can't be masked and is not written to the log.
    /// </summary>
    Task<ContainerRunResult?> ReattachAsync(
        string runId, string logPath, DateTimeOffset? deadline, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes (killing if need be) every Lodge-managed container whose run id isn't in the
    /// set <paramref name="liveRunIds"/> returns — leftovers of runs whose outcome is already
    /// recorded, or that the server lost track of. The set is read after the containers are
    /// listed, so a run launched meanwhile (its id is persisted before it starts) is never
    /// mistaken for an orphan. Returns how many were removed.
    /// </summary>
    Task<int> RemoveOrphansAsync(
        Func<CancellationToken, Task<IReadOnlySet<string>>> liveRunIds, CancellationToken cancellationToken = default);
}

/// <summary>How a container run ended: its exit code, and whether Lodge killed it for running past its timeout.</summary>
public sealed record ContainerRunResult(int ExitCode, bool TimedOut = false);

/// <summary>The labels every action container carries — how a runtime finds it again.</summary>
public static class ContainerLabels
{
    /// <summary>Marks a container (or a playbook image) as Lodge's.</summary>
    public const string Managed = "lodge.managed";

    /// <summary>The action the container runs.</summary>
    public const string ActionId = "lodge.action_id";

    /// <summary>The run the container is — the executor's run id, persisted on the action before launch.</summary>
    public const string RunId = "lodge.run_id";
}

/// <summary>
/// One container to run. <see cref="RunId"/> identifies it across restarts (it is also in
/// <see cref="Labels"/>). <see cref="Environment"/> holds every value — inputs and resolved
/// secrets included — and a runtime must pass it without ever putting a value in a process
/// argv. An empty <see cref="Command"/> keeps the image's CMD. Nothing of the host is ever
/// mounted: what a container needs it fetches itself (secrets) or gets as inputs.
/// <see cref="Resources"/>, <see cref="Security"/> (the relaxations of the runtime's
/// restrictive defaults) and <see cref="NetworkProfile"/> are the action's own;
/// <see cref="Timeout"/> is already resolved (null = no limit); <see cref="Secrets"/>
/// masks the resolved secret values out of the run log.
/// </summary>
public sealed record ContainerRunSpec(
    string RunId,
    string Image,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string>? Entrypoint,
    IReadOnlyList<string> Command,
    IReadOnlyDictionary<string, string> Labels,
    ContainerResources? Resources = null,
    ContainerSecurity? Security = null,
    string? NetworkProfile = null,
    TimeSpan? Timeout = null,
    SecretMasker? Secrets = null);

/// <summary>A container's <see cref="ContainerRunSpec.NetworkProfile"/> (or a security relaxation) that this server can't honor — the run fails before anything starts.</summary>
public sealed class ContainerPolicyException(string message) : Exception(message);
