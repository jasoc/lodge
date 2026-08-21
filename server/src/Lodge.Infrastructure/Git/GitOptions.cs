namespace Lodge.Infrastructure.Git;

/// <summary>
/// Selects and configures how Lodge reads inventory. In <c>Local</c> mode (dev/demo,
/// default) it reads the local working tree. In <c>GitHub</c> mode it reads via the
/// GitHub REST API. Either way the same reconciliation loop drives everything.
/// Lodge never writes to Git.
/// </summary>
public sealed class GitOptions
{
    public const string SectionName = "Git";

    /// <summary><c>Local</c> or <c>GitHub</c>.</summary>
    public string Provider { get; set; } = "Local";

    public GitHubOptions GitHub { get; set; } = new();

    /// <summary>
    /// Template for the "golden path" link shown on capability cards. Placeholders:
    /// <c>{owner}</c>, <c>{repo}</c>, <c>{branch}</c>, <c>{path}</c>.
    /// </summary>
    public string GoldenPathUrlTemplate { get; set; } =
        "https://github.com/{owner}/{repo}/blob/{branch}/{path}";

    /// <summary>
    /// Minimum gap between two manually-triggered reconciliation cycles (button/API).
    /// Repeated clicks inside this window reuse the last cycle's result instead of
    /// starting a new one. Never applies to the background loop.
    /// </summary>
    public int MinManualSyncIntervalSeconds { get; set; } = 5;

    public bool IsGitHub => string.Equals(Provider, "GitHub", StringComparison.OrdinalIgnoreCase);
}

/// <summary>GitHub connection settings for <c>GitHub</c> provider mode.</summary>
public sealed class GitHubOptions
{
    public string Owner { get; set; } = string.Empty;

    public string Repo { get; set; } = string.Empty;

    public string Branch { get; set; } = "main";

    /// <summary>Opaque reference resolved via <c>ISecretProvider</c>; never the token itself.</summary>
    public string TokenSecretRef { get; set; } = string.Empty;

    /// <summary>Polling interval in seconds. Kept modest so GitHub is not hammered.</summary>
    public int PollSeconds { get; set; } = 30;
}
