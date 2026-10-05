using Lodge.Core.Abstractions;
using Lodge.Core.Domain.Enums;

namespace Lodge.Infrastructure.Execution;

/// <summary>
/// The registered <see cref="IRunbookExecutor"/> — dispatches each action to the executor
/// it explicitly declares via <see cref="RunbookExecutionRequest.ExecutorKind"/>; status and
/// log reads route by the run id's prefix (<c>docker-</c>, <c>http-</c>, <c>none-</c>).
/// </summary>
public sealed class CompositeRunbookExecutor : IRunbookExecutor, IRunbookLogReader, IRunCompletionSource, IRunbookCanceller
{
    private readonly ContainerRunbookExecutor _container;
    private readonly HttpRunbookExecutor _http;
    private readonly NoneRunbookExecutor _none;

    public CompositeRunbookExecutor(ContainerRunbookExecutor container, HttpRunbookExecutor http, NoneRunbookExecutor? none = null)
    {
        _container = container;
        _http = http;
        _none = none ?? new NoneRunbookExecutor();
    }

    public event Action<string>? RunCompleted
    {
        add { _container.RunCompleted += value; _http.RunCompleted += value; _none.RunCompleted += value; }
        remove { _container.RunCompleted -= value; _http.RunCompleted -= value; _none.RunCompleted -= value; }
    }

    public string AllocateRunId(ExecutorKind kind)
        => kind switch
        {
            ExecutorKind.Container => _container.AllocateRunId(kind),
            ExecutorKind.Http => _http.AllocateRunId(kind),
            ExecutorKind.None => _none.AllocateRunId(kind),
            _ => throw new NotSupportedException($"Executor '{kind}' is not supported.")
        };

    public Task<RunbookRunHandle> StartAsync(RunbookExecutionRequest request, CancellationToken cancellationToken = default)
        => request.ExecutorKind switch
        {
            ExecutorKind.Container => _container.StartAsync(request, cancellationToken),
            ExecutorKind.Http => _http.StartAsync(request, cancellationToken),
            ExecutorKind.None => _none.StartAsync(request, cancellationToken),
            _ => throw new NotSupportedException($"Executor '{request.ExecutorKind}' is not supported.")
        };

    public Task<RunbookRunStatus> GetStatusAsync(string runId, CancellationToken cancellationToken = default)
        => IsNone(runId) ? _none.GetStatusAsync(runId, cancellationToken)
         : IsHttp(runId) ? _http.GetStatusAsync(runId, cancellationToken)
         : _container.GetStatusAsync(runId, cancellationToken);

    public Task<bool> StopAsync(string runId, CancellationToken cancellationToken = default)
        => IsNone(runId) ? Task.FromResult(false)
         : IsHttp(runId) ? _http.StopAsync(runId, cancellationToken)
         : _container.StopAsync(runId, cancellationToken);

    public Task<RunbookLogChunk?> ReadLogAsync(string runId, long offset, int maxBytes, CancellationToken cancellationToken = default)
        => IsNone(runId) ? Task.FromResult<RunbookLogChunk?>(null)
         : IsHttp(runId) ? _http.ReadLogAsync(runId, offset, maxBytes, cancellationToken)
         : _container.ReadLogAsync(runId, offset, maxBytes, cancellationToken);

    private static bool IsHttp(string runId) => runId.StartsWith(HttpRunbookExecutor.RunIdPrefix, StringComparison.Ordinal);

    private static bool IsNone(string runId) => runId.StartsWith(NoneRunbookExecutor.RunIdPrefix, StringComparison.Ordinal);
}
