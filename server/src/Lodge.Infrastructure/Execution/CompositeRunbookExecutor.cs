using Lodge.Core.Abstractions;
using Lodge.Core.Domain.Enums;

namespace Lodge.Infrastructure.Execution;

/// <summary>
/// The registered <see cref="IRunbookExecutor"/> — dispatches each runbook to whichever
/// concrete executor its action explicitly declares via <see
/// cref="RunbookExecutionRequest.ExecutorKind"/>, so a capability catalog can freely mix
/// local shell scripts, external webhooks, and containerized runs. Dispatch is never
/// inferred from the runbook string — an action that reaches here with a reserved,
/// not-yet-implemented kind (<see cref="ExecutorKind.Octopus"/>/<see
/// cref="ExecutorKind.Kubernetes"/>) fails loudly rather than silently falling back to
/// shell, since misrouting a governed operation is a safety bug, not a graceful default.
/// </summary>
public sealed class CompositeRunbookExecutor : IRunbookExecutor, IRunbookLogReader
{
    private readonly WebhookRunbookExecutor _webhook;
    private readonly ShellCommandRunbookExecutor _shell;
    private readonly DockerRunbookExecutor _docker;

    public CompositeRunbookExecutor(WebhookRunbookExecutor webhook, ShellCommandRunbookExecutor shell, DockerRunbookExecutor docker)
    {
        _webhook = webhook;
        _shell = shell;
        _docker = docker;
    }

    public Task<RunbookRunHandle> StartAsync(RunbookExecutionRequest request, CancellationToken cancellationToken = default)
        => request.ExecutorKind switch
        {
            ExecutorKind.Shell => _shell.StartAsync(request, cancellationToken),
            ExecutorKind.Webhook => _webhook.StartAsync(request, cancellationToken),
            ExecutorKind.Docker => _docker.StartAsync(request, cancellationToken),
            _ => throw new NotSupportedException($"Executor '{request.ExecutorKind}' is not yet implemented.")
        };

    public Task<RunbookRunStatus> GetStatusAsync(string runId, CancellationToken cancellationToken = default)
        => WebhookRunbookExecutor.IsWebhookRunId(runId) ? _webhook.GetStatusAsync(runId, cancellationToken)
         : runId.StartsWith("docker-", StringComparison.Ordinal) ? _docker.GetStatusAsync(runId, cancellationToken)
         : _shell.GetStatusAsync(runId, cancellationToken);

    /// <summary>Webhook runs have no local log — their output lives in the target system.</summary>
    public Task<RunbookLogChunk?> ReadLogAsync(string runId, long offset, int maxBytes, CancellationToken cancellationToken = default)
        => WebhookRunbookExecutor.IsWebhookRunId(runId) ? Task.FromResult<RunbookLogChunk?>(null)
         : runId.StartsWith("docker-", StringComparison.Ordinal) ? _docker.ReadLogAsync(runId, offset, maxBytes, cancellationToken)
         : _shell.ReadLogAsync(runId, offset, maxBytes, cancellationToken);

    /// <summary>Used by the push-callback endpoint to record a status an external system
    /// reports for a webhook run it can't answer synchronously about.</summary>
    public bool TryReportWebhookStatus(string runId, RunbookRunState state, string? message)
    {
        if (!WebhookRunbookExecutor.IsWebhookRunId(runId))
        {
            return false;
        }
        _webhook.ReportStatus(runId, state, message);
        return true;
    }
}
