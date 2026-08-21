using Lodge.Infrastructure.Git;
using Microsoft.Extensions.Options;

namespace Lodge.Infrastructure.Reconciliation;

/// <summary>
/// Reads instance inventory from GitHub. The head cursor is the tracked branch's head
/// commit SHA (one cheap API call per tick); file contents are fetched only when the
/// loop sees the head move.
/// </summary>
public sealed class GitHubInventorySource : IInventorySource
{
    private readonly IGitHubClient _client;
    private readonly GitOptions _options;

    public GitHubInventorySource(IGitHubClient client, IOptions<GitOptions> options)
    {
        _client = client;
        _options = options.Value;
    }

    public Task<string> GetHeadAsync(string kindCode, CancellationToken cancellationToken = default)
        => _client.GetBranchHeadShaAsync(_options.GitHub.Branch, cancellationToken);

    public async Task<IReadOnlyList<InstanceFile>> GetInstanceFilesAsync(string kindCode, CancellationToken cancellationToken = default)
    {
        var instancesDir = $"inventory/{kindCode}/instances";
        var head = await _client.GetBranchHeadShaAsync(_options.GitHub.Branch, cancellationToken);
        var instanceDirs = await _client.ListSubdirectoriesAsync(instancesDir, head, cancellationToken);

        var files = new List<InstanceFile>();
        foreach (var instanceDir in instanceDirs.OrderBy(d => d, StringComparer.Ordinal))
        {
            var instanceCode = instanceDir.Split('/').Last();
            var paths = await _client.ListDirectoryAsync(instanceDir, head, cancellationToken);
            foreach (var path in paths.OrderBy(p => p, StringComparer.Ordinal))
            {
                if (path.EndsWith("/overrides.yaml", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // capability rule overrides, not inventory data — read by ICapabilityCatalogProvider
                }

                var content = await _client.GetFileContentAsync(path, head, cancellationToken);
                if (content is not null)
                {
                    files.Add(new InstanceFile(instanceCode, path, content));
                }
            }
        }

        return files;
    }
}
