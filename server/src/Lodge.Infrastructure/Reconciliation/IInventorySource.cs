namespace Lodge.Infrastructure.Reconciliation;

/// <summary>One instance inventory file as read from the source of truth.</summary>
public sealed record InstanceFile(string InstanceCode, string RepoRelativePath, string Content);

/// <summary>
/// A kind as declared by the inventory: one <c>inventory/{code}/</c> folder.
/// <see cref="Name"/> comes from the optional <c>inventory/{code}/kind.yaml</c>
/// (<c>name: ...</c>); null when that file or field is absent.
/// </summary>
public sealed record KindDescriptor(string Code, string? Name);

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

    /// <summary>
    /// Every kind folder under <c>inventory/</c> — the inventory, not the database, decides
    /// which kinds exist. Folders whose name isn't a valid kind code are skipped.
    /// </summary>
    Task<IReadOnlyList<KindDescriptor>> GetKindsAsync(CancellationToken cancellationToken = default);

    /// <summary>All instance inventory files for a kind, with content.</summary>
    Task<IReadOnlyList<InstanceFile>> GetInstanceFilesAsync(string kindCode, CancellationToken cancellationToken = default);
}
