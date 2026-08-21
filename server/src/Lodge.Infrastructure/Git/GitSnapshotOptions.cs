namespace Lodge.Infrastructure.Git;

/// <summary>
/// Configuration for reading inventory snapshots from the local Git repository.
/// </summary>
public sealed class GitSnapshotOptions
{
    public const string SectionName = "GitSnapshot";

    /// <summary>Absolute path to the repository working tree root.</summary>
    public string RepoRoot { get; set; } = string.Empty;
}
