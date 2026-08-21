namespace Lodge.Infrastructure.Reconciliation;

/// <summary>One instance inventory file as read from the source of truth.</summary>
public sealed record InstanceFile(string InstanceCode, string RepoRelativePath, string Content);

/// <summary>
/// Where instance inventory YAML comes from. The reconciliation loop does a cheap
/// <see cref="GetHeadAsync"/> check every tick and only re-fetches file contents when
/// the head moved. Implementations: local working tree (dev/demo) or the GitHub API.
/// Lodge never writes to the source.
/// </summary>
public interface IInventorySource
{
    /// <summary>
    /// Opaque cursor for "has anything changed": the branch head SHA on GitHub, a
    /// content hash over the instance files in local mode.
    /// </summary>
    Task<string> GetHeadAsync(string kindCode, CancellationToken cancellationToken = default);

    /// <summary>All instance inventory files for a kind, with content.</summary>
    Task<IReadOnlyList<InstanceFile>> GetInstanceFilesAsync(string kindCode, CancellationToken cancellationToken = default);
}
