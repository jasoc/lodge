namespace Lodge.Infrastructure.Git;

/// <summary>
/// Minimal GitHub REST surface Lodge needs to pull inventory: read a branch head,
/// list files changed between two commits, and read file content at a ref. Kept as an
/// abstraction so the polling delta logic can be unit-tested with a fake client.
/// </summary>
public interface IGitHubClient
{
    /// <summary>Current head commit SHA of a branch.</summary>
    Task<string> GetBranchHeadShaAsync(string branch, CancellationToken cancellationToken = default);

    /// <summary>Repo-relative paths changed between two commits (base..head).</summary>
    Task<IReadOnlyList<string>> GetChangedFilesAsync(string baseSha, string headSha, CancellationToken cancellationToken = default);

    /// <summary>File content at a ref (Contents API), or null when absent.</summary>
    Task<string?> GetFileContentAsync(string path, string gitRef, CancellationToken cancellationToken = default);

    /// <summary>Repo-relative paths of the <c>.yaml</c> files directly under a directory at a ref (Contents API).</summary>
    Task<IReadOnlyList<string>> ListDirectoryAsync(string path, string gitRef, CancellationToken cancellationToken = default);

    /// <summary>Repo-relative paths of the subdirectories directly under a directory at a ref (Contents API).</summary>
    Task<IReadOnlyList<string>> ListSubdirectoriesAsync(string path, string gitRef, CancellationToken cancellationToken = default);
}
