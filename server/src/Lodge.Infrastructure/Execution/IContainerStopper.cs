namespace Lodge.Infrastructure.Execution;

/// <summary>
/// Optional <see cref="IContainerRunner"/> capability: killing the container of a run on
/// demand, by its run id (so it also works for a run started before a server restart). A
/// no-op when nothing is running for that id.
/// </summary>
public interface IContainerStopper
{
    Task StopAsync(string runId, string logPath, CancellationToken cancellationToken = default);
}
