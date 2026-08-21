using System.Security.Cryptography;
using System.Text;
using Lodge.Infrastructure.Git;
using Microsoft.Extensions.Options;

namespace Lodge.Infrastructure.Reconciliation;

/// <summary>
/// Reads instance inventory straight from the local working tree — each subdirectory of
/// <c>{RepoRoot}/inventory/{kind}/instances/</c> is one instance, and every <c>*.yaml</c>
/// file directly inside it contributes to that instance (merged by the caller via
/// <see cref="Lodge.Core.Diff.InstanceYamlMerger"/>). The head cursor is a SHA-256 over
/// every file's path and content, so any edit on disk moves it — no commits needed for
/// the dev/demo loop.
/// </summary>
public sealed class LocalInventorySource : IInventorySource
{
    private readonly string _repoRoot;

    public LocalInventorySource(IOptions<GitSnapshotOptions> options)
    {
        _repoRoot = string.IsNullOrWhiteSpace(options.Value.RepoRoot)
            ? Directory.GetCurrentDirectory()
            : options.Value.RepoRoot;
    }

    public async Task<string> GetHeadAsync(string kindCode, CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();
        foreach (var file in await GetInstanceFilesAsync(kindCode, cancellationToken))
        {
            builder.Append(file.RepoRelativePath).Append('\n').Append(file.Content).Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return $"local:{Convert.ToHexString(hash).ToLowerInvariant()[..24]}";
    }

    public async Task<IReadOnlyList<InstanceFile>> GetInstanceFilesAsync(string kindCode, CancellationToken cancellationToken = default)
    {
        var instancesDir = Path.Combine(_repoRoot, "inventory", kindCode, "instances");
        if (!Directory.Exists(instancesDir))
        {
            return Array.Empty<InstanceFile>();
        }

        var files = new List<InstanceFile>();
        foreach (var instanceDir in Directory.EnumerateDirectories(instancesDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var instanceCode = Path.GetFileName(instanceDir);
            foreach (var path in Directory.EnumerateFiles(instanceDir, "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
            {
                if (IsOverridesFile(path))
                {
                    continue; // capability rule overrides, not inventory data — read by ICapabilityCatalogProvider
                }

                cancellationToken.ThrowIfCancellationRequested();
                var content = await File.ReadAllTextAsync(path, cancellationToken);
                files.Add(new InstanceFile(
                    instanceCode,
                    $"inventory/{kindCode}/instances/{instanceCode}/{Path.GetFileName(path)}",
                    content));
            }
        }

        return files;
    }

    private static bool IsOverridesFile(string path)
        => string.Equals(Path.GetFileName(path), "overrides.yaml", StringComparison.OrdinalIgnoreCase);
}
