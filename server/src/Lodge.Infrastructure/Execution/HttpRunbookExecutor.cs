using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lodge.Core.Abstractions;
using Lodge.Core.Catalog;
using Lodge.Core.Domain.Enums;
using Microsoft.Extensions.Options;

namespace Lodge.Infrastructure.Execution;

/// <summary>Configuration for <see cref="HttpRunbookExecutor"/>.</summary>
public sealed class HttpExecutorOptions
{
    public const string SectionName = "HttpExecutor";

    /// <summary>Directory for per-run logs (request sent, response received) and status sidecars.</summary>
    public string LogDirectory { get; set; } = "data/http-run-logs";
}

/// <summary>
/// Runs an action as one HTTP request, entirely described by the catalog's <see
/// cref="HttpExecutorConfig"/> — method, url, headers, query, body — with
/// <c>{{ name }}</c> references substituted from the action's parameters (inputs, prompts,
/// resolved secrets, <c>instance</c>/<c>kind</c>). Values are substituted as-is into the
/// url and headers, URL-encoded into query values, and JSON-encoded into a structured body.
/// A reference to an unknown parameter fails the run before anything is sent.
///
/// The run succeeds when the response status is in <c>expect_status</c> (any 2xx by
/// default). Its log shows what was sent and what came back, with every secret value
/// masked. Like the docker executor, an in-memory run dict plus a JSON sidecar lets a run
/// be asked about after a restart; a request in flight at restart reports as unknown.
/// </summary>
public sealed partial class HttpRunbookExecutor : IRunbookExecutor, IRunbookLogReader
{
    public const string RunIdPrefix = "http-";

    private const int MaxLoggedBodyChars = 64 * 1024;

    private readonly HttpClient _http;
    private readonly HttpExecutorOptions _options;
    private readonly ConcurrentDictionary<string, RunState> _runs = new(StringComparer.Ordinal);

    public HttpRunbookExecutor(HttpClient http, IOptions<HttpExecutorOptions> options)
    {
        _http = http;
        _http.Timeout = Timeout.InfiniteTimeSpan; // per-request timeouts come from the catalog
        _options = options.Value;
        Directory.CreateDirectory(_options.LogDirectory);
    }

    public Task<RunbookRunHandle> StartAsync(RunbookExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.HttpConfig is null)
        {
            throw new InvalidOperationException(
                $"Action '{request.ActionRef}' is routed to the HTTP executor but carries no HttpExecutorConfig — " +
                "a catalog/persistence bug (the loader guarantees an http block for 'executor: http').");
        }

        var runId = request.RunId ?? AllocateRunId(ExecutorKind.Http);
        RunLogFile.EnsureValidRunId(RunIdPrefix, runId);
        if (_runs.ContainsKey(runId) || File.Exists(Path.Combine(_options.LogDirectory, $"{runId}.json")))
        {
            // Already sent (or attempted) under this id: never send it twice — the
            // status comes from memory, or from the sidecar after a restart.
            return Task.FromResult(new RunbookRunHandle(runId, RunbookRunState.Running));
        }

        var state = new RunState(
            request.ActionRef,
            Path.Combine(_options.LogDirectory, $"{runId}.log"),
            Path.Combine(_options.LogDirectory, $"{runId}.json"),
            DateTimeOffset.UtcNow);
        WriteSidecar(state, outcome: null);

        _runs[runId] = state;
        state.Completion = Task.Run(() => RunAsync(request, request.HttpConfig, state));
        return Task.FromResult(new RunbookRunHandle(runId, RunbookRunState.Running));
    }

    public string AllocateRunId(ExecutorKind kind) => $"{RunIdPrefix}{Guid.NewGuid():N}";

    public Task<RunbookRunStatus> GetStatusAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (_runs.TryGetValue(runId, out var state))
        {
            if (state.Completion is not { IsCompleted: true } completion)
            {
                return Task.FromResult(new RunbookRunStatus(runId, RunbookRunState.Running,
                    $"Action '{state.ActionRef}': waiting for the response.", DateTimeOffset.UtcNow));
            }

            var outcome = completion.Result;
            return Task.FromResult(new RunbookRunStatus(runId,
                outcome.Succeeded ? RunbookRunState.Succeeded : RunbookRunState.Failed,
                outcome.Message, outcome.CompletedAt));
        }

        return Task.FromResult(StatusFromSidecar(runId));
    }

    public Task<RunbookLogChunk?> ReadLogAsync(string runId, long offset, int maxBytes, CancellationToken cancellationToken = default)
        => RunLogFile.ReadAsync(_options.LogDirectory, RunIdPrefix, runId, offset, maxBytes, cancellationToken);

    private async Task<RunOutcome> RunAsync(RunbookExecutionRequest request, HttpExecutorConfig config, RunState state)
    {
        var mask = SecretMasker.For(request.SecretNames, request.Parameters);
        await using var log = new StreamWriter(state.LogPath, append: false, Encoding.UTF8) { AutoFlush = true };

        RunOutcome Done(bool succeeded, string message)
        {
            var outcome = new RunOutcome(succeeded, $"Action '{state.ActionRef}': {mask.Apply(message)}", DateTimeOffset.UtcNow);
            WriteSidecar(state, outcome);
            return outcome;
        }

        try
        {
            var parameters = request.Parameters;
            var url = BuildUrl(Substitute(config.Url, parameters), config.Query, parameters);
            using var message = new HttpRequestMessage(new HttpMethod(config.Method), url);

            string? bodyText = null;
            if (config.Body is not null)
            {
                bodyText = config.BodyIsJson ? SubstituteJson(config.Body, parameters) : Substitute(config.Body, parameters);
                message.Content = new StringContent(bodyText, Encoding.UTF8, config.BodyIsJson ? "application/json" : "text/plain");
            }

            foreach (var (name, rawValue) in config.Headers ?? new Dictionary<string, string>())
            {
                var value = Substitute(rawValue, parameters);
                if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) && message.Content is not null)
                {
                    message.Content.Headers.Remove(name);
                    message.Content.Headers.TryAddWithoutValidation(name, value);
                }
                else if (!message.Headers.TryAddWithoutValidation(name, value))
                {
                    throw new InvalidOperationException($"header '{name}' can't be set on a request.");
                }
            }

            await log.WriteLineAsync($"[lodge] {config.Method} {mask.Apply(url.ToString())}");
            var headerNames = message.Headers.Select(h => h.Key)
                .Concat(message.Content?.Headers.Select(h => h.Key) ?? Enumerable.Empty<string>());
            await log.WriteLineAsync($"[lodge] headers: {string.Join(", ", headerNames)}");
            if (bodyText is not null)
            {
                await log.WriteLineAsync("[lodge] body:");
                await log.WriteLineAsync(Truncate(mask.Apply(bodyText)));
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(config.TimeoutSeconds));
            var watch = Stopwatch.StartNew();
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token);
            var responseText = await response.Content.ReadAsStringAsync(timeout.Token);
            var status = (int)response.StatusCode;

            await log.WriteLineAsync($"[lodge] <- {status} {response.ReasonPhrase} ({watch.ElapsedMilliseconds} ms)");
            if (responseText.Length > 0)
            {
                await log.WriteLineAsync(Truncate(mask.Apply(responseText)));
            }

            var expected = config.ExpectStatus?.Contains(status) ?? status is >= 200 and < 300;
            return Done(expected, expected
                ? $"HTTP {status} {response.ReasonPhrase}."
                : $"HTTP {status} {response.ReasonPhrase} — expected {(config.ExpectStatus is null ? "2xx" : string.Join("/", config.ExpectStatus))}. See the log for the response.");
        }
        catch (OperationCanceledException)
        {
            await log.WriteLineAsync($"[lodge] no response within {config.TimeoutSeconds}s");
            return Done(false, $"timed out after {config.TimeoutSeconds}s.");
        }
        catch (Exception ex)
        {
            await log.WriteLineAsync($"[lodge] {mask.Apply(ex.Message)}");
            return Done(false, $"failed: {ex.Message}");
        }
    }

    private static Uri BuildUrl(string url, IReadOnlyDictionary<string, string>? query, IReadOnlyDictionary<string, string?> parameters)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"'{url}' is not an absolute http(s) URL.");
        }
        if (query is null || query.Count == 0)
        {
            return uri;
        }

        var pairs = query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(Substitute(q.Value, parameters))}");
        var builder = new UriBuilder(uri);
        var existing = builder.Query.TrimStart('?');
        builder.Query = string.Join('&', (existing.Length > 0 ? new[] { existing } : Array.Empty<string>()).Concat(pairs));
        return builder.Uri;
    }

    /// <summary>Replaces every <c>{{ name }}</c> with the parameter's value; an unknown name is an error.</summary>
    internal static string Substitute(string template, IReadOnlyDictionary<string, string?> parameters)
        => Reference().Replace(template, m => Lookup(m.Groups[1].Value, parameters) ?? string.Empty);

    /// <summary>
    /// Substitutes inside every string of a JSON template. A string that is exactly one
    /// reference takes the value's own JSON shape when it is an object or array (so
    /// <c>"{{ item }}"</c> embeds the item), and stays a string otherwise.
    /// </summary>
    internal static string SubstituteJson(string jsonTemplate, IReadOnlyDictionary<string, string?> parameters)
    {
        var root = JsonNode.Parse(jsonTemplate);
        return (Walk(root, parameters)?.ToJsonString() ?? "null");

        static JsonNode? Walk(JsonNode? node, IReadOnlyDictionary<string, string?> parameters)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var key in obj.Select(p => p.Key).ToList())
                    {
                        obj[key] = Walk(obj[key]?.DeepClone(), parameters);
                    }
                    return obj;
                case JsonArray array:
                    var items = array.Select(i => Walk(i?.DeepClone(), parameters)).ToList();
                    array.Clear();
                    items.ForEach(array.Add);
                    return array;
                case JsonValue value when value.TryGetValue<string>(out var text):
                    var whole = WholeReference().Match(text);
                    if (whole.Success)
                    {
                        var raw = Lookup(whole.Groups[1].Value, parameters);
                        if (raw is not null && raw.TrimStart() is ['{', ..] or ['[', ..])
                        {
                            try
                            {
                                return JsonNode.Parse(raw);
                            }
                            catch (JsonException)
                            {
                                // Not actually JSON: keep it a string.
                            }
                        }
                        return JsonValue.Create(raw);
                    }
                    return JsonValue.Create(Substitute(text, parameters));
                default:
                    return node;
            }
        }
    }

    private static string? Lookup(string name, IReadOnlyDictionary<string, string?> parameters)
        => parameters.TryGetValue(name, out var value)
            ? value
            : throw new InvalidOperationException(
                $"'{{{{ {name} }}}}' doesn't name a parameter of this action (known: {string.Join(", ", parameters.Keys.Order())}).");

    private static string Truncate(string text)
        => text.Length <= MaxLoggedBodyChars ? text : text[..MaxLoggedBodyChars] + $"\n[lodge] … {text.Length - MaxLoggedBodyChars} more characters";

    private RunbookRunStatus StatusFromSidecar(string runId)
    {
        if (!runId.StartsWith(RunIdPrefix, StringComparison.Ordinal) || runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return new RunbookRunStatus(runId, RunbookRunState.Failed, "Unknown run.", DateTimeOffset.UtcNow);
        }
        var sidecarPath = Path.Combine(_options.LogDirectory, $"{runId}.json");
        if (!File.Exists(sidecarPath))
        {
            return new RunbookRunStatus(runId, RunbookRunState.Failed, RunLogFile.NeverStartedMessage(runId), DateTimeOffset.UtcNow);
        }

        var sidecar = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(sidecarPath))!;
        if (sidecar.CompletedAt is { } completedAt)
        {
            return new RunbookRunStatus(runId,
                sidecar.Succeeded ? RunbookRunState.Succeeded : RunbookRunState.Failed, sidecar.Message, completedAt);
        }

        return new RunbookRunStatus(runId, RunbookRunState.Failed,
            $"Action '{sidecar.ActionRef}' outcome unknown: the server restarted while the request was in flight.",
            DateTimeOffset.UtcNow);
    }

    private static void WriteSidecar(RunState state, RunOutcome? outcome)
    {
        var sidecar = new Sidecar(state.ActionRef, state.StartedAt, outcome?.CompletedAt, outcome?.Succeeded ?? false, outcome?.Message);
        File.WriteAllText(state.SidecarPath, JsonSerializer.Serialize(sidecar));
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex Reference();

    [GeneratedRegex(@"^\s*\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}\s*$")]
    private static partial Regex WholeReference();

    private sealed class RunState(string actionRef, string logPath, string sidecarPath, DateTimeOffset startedAt)
    {
        public string ActionRef { get; } = actionRef;
        public string LogPath { get; } = logPath;
        public string SidecarPath { get; } = sidecarPath;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public Task<RunOutcome>? Completion { get; set; }
    }

    private sealed record RunOutcome(bool Succeeded, string Message, DateTimeOffset CompletedAt);

    private sealed record Sidecar(string ActionRef, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, bool Succeeded, string? Message);
}
