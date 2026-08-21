using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lodge.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Lodge.Infrastructure.Execution;

/// <summary>
/// Runs a runbook by calling an external HTTP webhook — for notifying something outside
/// Lodge's own reach (a Slack/Discord webhook, an n8n flow, a webhook-triggered CI job)
/// rather than executing a local script. A <c>runbook</c> is a webhook target only if it
/// has an entry in <see cref="WebhookExecutorOptions.Runbooks"/>; anything else is not
/// this executor's concern (see <see cref="CompositeRunbookExecutor"/>, which is what's
/// actually registered as <see cref="IRunbookExecutor"/>).
///
/// Two status models, chosen per target: if <see cref="WebhookTarget.StatusUrlTemplate"/>
/// is unset (the default), the initial POST's own JSON response body is expected to
/// report the terminal state synchronously — simplest, and right for anything that
/// finishes within one HTTP call. If set, the POST only means "accepted" and
/// <see cref="GetStatusAsync"/> polls that URL instead — for something that can't answer
/// synchronously. Either way, an external system can also push its own status update to
/// <c>POST /internal/executions/{runId}/status</c> at any time.
/// </summary>
public sealed class WebhookRunbookExecutor
{
    private readonly HttpClient _http;
    private readonly WebhookExecutorOptions _options;
    private readonly ConcurrentDictionary<string, RunState> _runs = new(StringComparer.Ordinal);

    public WebhookRunbookExecutor(HttpClient http, IOptions<WebhookExecutorOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public bool CanHandle(string runbookRef) => _options.Runbooks.ContainsKey(runbookRef);

    public static bool IsWebhookRunId(string runId) => runId.StartsWith("webhook-", StringComparison.Ordinal);

    public async Task<RunbookRunHandle> StartAsync(RunbookExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var target = _options.Runbooks[request.RunbookRef];
        var runId = $"webhook-{Guid.NewGuid():N}";

        var payload = new WebhookPayload(runId, request.KindCode, request.InstanceCode, request.RunbookRef, request.ActionId, request.Parameters);
        var response = await _http.PostAsJsonAsync(target.Url, payload, JsonOptions, cancellationToken);

        _runs[runId] = new RunState(request.RunbookRef, target);

        if (target.StatusUrlTemplate is not null)
        {
            // This system can't answer synchronously — accepted, poll for the real state.
            return new RunbookRunHandle(runId, RunbookRunState.Running);
        }

        response.EnsureSuccessStatusCode();
        var reported = await response.Content.ReadFromJsonAsync<WebhookStatusPayload>(JsonOptions, cancellationToken);
        var state = MapState(reported?.Status);
        _runs[runId] = _runs[runId] with { LastState = state, LastMessage = reported?.Message };
        return new RunbookRunHandle(runId, state);
    }

    public async Task<RunbookRunStatus> GetStatusAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(runId, out var run))
        {
            return new RunbookRunStatus(runId, RunbookRunState.Failed, "Unknown run.", DateTimeOffset.UtcNow);
        }

        if (run.Target.StatusUrlTemplate is { } template)
        {
            var url = template.Replace("{run_id}", Uri.EscapeDataString(runId));
            var reported = await _http.GetFromJsonAsync<WebhookStatusPayload>(url, JsonOptions, cancellationToken);
            var state = MapState(reported?.Status);
            _runs[runId] = run with { LastState = state, LastMessage = reported?.Message };
            return new RunbookRunStatus(runId, state, reported?.Message, DateTimeOffset.UtcNow);
        }

        // No status URL: whatever the initial POST reported (or a later push callback
        // updated) is all there is to know.
        return new RunbookRunStatus(runId, run.LastState, run.LastMessage, DateTimeOffset.UtcNow);
    }

    /// <summary>Records a status pushed by the external system itself, via the callback endpoint.</summary>
    public void ReportStatus(string runId, RunbookRunState state, string? message)
    {
        if (_runs.TryGetValue(runId, out var run))
        {
            _runs[runId] = run with { LastState = state, LastMessage = message };
        }
    }

    private static RunbookRunState MapState(string? status) => status?.ToLowerInvariant() switch
    {
        "succeeded" or "success" => RunbookRunState.Succeeded,
        "failed" or "failure" or "error" => RunbookRunState.Failed,
        _ => RunbookRunState.Running
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private sealed record RunState(string RunbookRef, WebhookTarget Target, RunbookRunState LastState = RunbookRunState.Running, string? LastMessage = null);

    private sealed record WebhookPayload(
        string RunId, string KindCode, string InstanceCode, string RunbookRef, Guid ActionId, IReadOnlyDictionary<string, string?> Parameters);

    private sealed record WebhookStatusPayload(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("message")] string? Message);
}
