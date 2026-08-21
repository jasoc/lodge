using Lodge.Core.Abstractions;

namespace Lodge.Infrastructure.Execution;

/// <summary>
/// The registered <see cref="IRunbookExecutor"/> — dispatches each runbook to whichever
/// concrete executor actually handles it, so a capability catalog can freely mix local
/// shell scripts and external webhooks. A runbook is a webhook target only if it's listed
/// in <c>WebhookExecutor:Runbooks</c>; everything else goes to the shell executor, which
/// itself falls back to treating the <c>runbook</c> value as a literal command when it's
/// not in its own alias map either — so an unconfigured deployment still works.
/// </summary>
public sealed class CompositeRunbookExecutor : IRunbookExecutor
{
    private readonly WebhookRunbookExecutor _webhook;
    private readonly ShellCommandRunbookExecutor _shell;

    public CompositeRunbookExecutor(WebhookRunbookExecutor webhook, ShellCommandRunbookExecutor shell)
    {
        _webhook = webhook;
        _shell = shell;
    }

    public Task<RunbookRunHandle> StartAsync(RunbookExecutionRequest request, CancellationToken cancellationToken = default)
        => _webhook.CanHandle(request.RunbookRef)
            ? _webhook.StartAsync(request, cancellationToken)
            : _shell.StartAsync(request, cancellationToken);

    public Task<RunbookRunStatus> GetStatusAsync(string runId, CancellationToken cancellationToken = default)
        => WebhookRunbookExecutor.IsWebhookRunId(runId)
            ? _webhook.GetStatusAsync(runId, cancellationToken)
            : _shell.GetStatusAsync(runId, cancellationToken);

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
