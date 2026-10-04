using Lodge.Core.Abstractions;
using Lodge.Core.Domain.Enums;

namespace Lodge.Infrastructure.Execution;

/// <summary>
/// The registered <see cref="IRunbookExecutor"/> — dispatches each action to the executor
/// it explicitly declares via <see cref="RunbookExecutionRequest.ExecutorKind"/>; status and
/// log reads route by the run id's prefix (<c>docker-</c>, <c>http-</c>).
/// </summary>
public sealed class CompositeRunbookExecutor : IRunbookExecutor, IRunbookLogReader
{
    private readonly DockerRunbookExecutor _docker;
    private readonly HttpRunbookExecutor _http;

    public CompositeRunbookExecutor(DockerRunbookExecutor docker, HttpRunbookExecutor http)
    {
        _docker = docker;
        _http = http;
    }

    public Task<RunbookRunHandle> StartAsync(RunbookExecutionRequest request, CancellationToken cancellationToken = default)
        => request.ExecutorKind switch
        {
            ExecutorKind.Docker => _docker.StartAsync(request, cancellationToken),
            ExecutorKind.Http => _http.StartAsync(request, cancellationToken),
            _ => throw new NotSupportedException($"Executor '{request.ExecutorKind}' is not supported.")
        };

    public Task<RunbookRunStatus> GetStatusAsync(string runId, CancellationToken cancellationToken = default)
        => IsHttp(runId) ? _http.GetStatusAsync(runId, cancellationToken) : _docker.GetStatusAsync(runId, cancellationToken);

    public Task<RunbookLogChunk?> ReadLogAsync(string runId, long offset, int maxBytes, CancellationToken cancellationToken = default)
        => IsHttp(runId) ? _http.ReadLogAsync(runId, offset, maxBytes, cancellationToken) : _docker.ReadLogAsync(runId, offset, maxBytes, cancellationToken);

    private static bool IsHttp(string runId) => runId.StartsWith(HttpRunbookExecutor.RunIdPrefix, StringComparison.Ordinal);
}
