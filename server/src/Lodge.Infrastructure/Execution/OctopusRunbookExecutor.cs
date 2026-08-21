using Lodge.Core.Abstractions;

namespace Lodge.Infrastructure.Execution;

/// <summary>
/// Octopus Deploy runbook executor — SCAFFOLD ONLY. The method bodies are intentionally
/// empty (throw <see cref="NotImplementedException"/>) and are to be filled with the
/// Octopus.Client calls that start a runbook run and query its task state:
/// <list type="bullet">
///   <item><description><c>StartAsync</c>: resolve project + runbook by <see cref="RunbookExecutionRequest.RunbookRef"/>,
///   create a runbook run for the instance's environment, return the server task id as the run handle.</description></item>
///   <item><description><c>GetStatusAsync</c>: fetch the server task and map its state
///   (Queued/Executing/Success/Failed) to <see cref="RunbookRunState"/>.</description></item>
/// </list>
/// The Octopus server URL and API key are resolved via <see cref="ISecretProvider"/> —
/// never embedded here. This class is registered only when the executor is configured
/// for Octopus; the POC uses <see cref="MockRunbookExecutor"/>.
/// </summary>
public sealed class OctopusRunbookExecutor : IRunbookExecutor
{
    private readonly ISecretProvider _secrets;

    public OctopusRunbookExecutor(ISecretProvider secrets)
    {
        _secrets = secrets;
    }

    public Task<RunbookRunHandle> StartAsync(RunbookExecutionRequest request, CancellationToken cancellationToken = default)
    {
        // TODO: wire Octopus.Client — create runbook run and return the server task id.
        throw new NotImplementedException("OctopusRunbookExecutor.StartAsync is not implemented yet.");
    }

    public Task<RunbookRunStatus> GetStatusAsync(string runId, CancellationToken cancellationToken = default)
    {
        // TODO: wire Octopus.Client — read the server task and map its state.
        throw new NotImplementedException("OctopusRunbookExecutor.GetStatusAsync is not implemented yet.");
    }
}
