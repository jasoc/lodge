namespace Lodge.Infrastructure.Execution;

/// <summary>One webhook target, keyed by <c>runbook</c> value in the alias map.</summary>
public sealed class WebhookTarget
{
    /// <summary>URL the executor POSTs the run request to.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Optional. If set, <c>{run_id}</c> in this template is substituted and the result is
    /// polled for status instead of trusting the initial POST's response body. Use this
    /// for a system that can't answer synchronously.
    /// </summary>
    public string? StatusUrlTemplate { get; set; }
}

/// <summary>Configuration for <see cref="WebhookRunbookExecutor"/>.</summary>
public sealed class WebhookExecutorOptions
{
    public const string SectionName = "WebhookExecutor";

    /// <summary>Alias map from a capability's <c>runbook</c> value to the webhook target.
    /// A <c>runbook</c> with no entry here is not a webhook target — the shell executor
    /// (or whichever is registered) handles it instead.</summary>
    public Dictionary<string, WebhookTarget> Runbooks { get; set; } = new(StringComparer.Ordinal);
}
