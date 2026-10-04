using System.Text.RegularExpressions;

namespace Lodge.Core.Catalog;

/// <summary>One instance inventory file as read from an inventory tree.</summary>
public sealed record InventoryFile(string InstanceCode, string RepoRelativePath, string Content);

/// <summary>
/// Where things live in an inventory tree (<c>{repoRoot}/inventory/{kind}/...</c>): the one
/// place the server's local inventory source and <c>lodge validate</c> agree on which
/// folders are kinds and which files are instance data, so they can never drift apart.
/// </summary>
public static partial class InventoryLayout
{
    public const string KindManifestFileName = "kind.yaml";

    /// <summary>Capability rule overrides of one instance — not inventory data.</summary>
    public const string OverridesFileName = "overrides.yaml";

    /// <summary>Lowercase letters, digits, '-' and '_', starting with a letter or digit; fits the 64-char key.</summary>
    public static bool IsValidKindCode(string code) => code.Length <= 64 && KindCodePattern().IsMatch(code);

    public static bool IsOverridesFile(string path)
        => string.Equals(Path.GetFileName(path), OverridesFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The valid kind codes of <c>{repoRoot}/inventory</c>, ordinal.</summary>
    public static IReadOnlyList<string> ListKinds(string repoRoot)
    {
        var inventoryDir = Path.Combine(repoRoot, "inventory");
        if (!Directory.Exists(inventoryDir))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateDirectories(inventoryDir)
            .OrderBy(d => d, StringComparer.Ordinal)
            .Select(Path.GetFileName)
            .Where(code => code is not null && IsValidKindCode(code))
            .Select(code => code!)
            .ToList();
    }

    /// <summary>
    /// Every instance data file of a kind: each <c>*.yaml</c> directly inside
    /// <c>inventory/{kind}/instances/{instance}/</c> except the overrides file, with content.
    /// </summary>
    public static IReadOnlyList<InventoryFile> ReadInstanceFiles(string repoRoot, string kindCode, CancellationToken cancellationToken = default)
    {
        var instancesDir = Path.Combine(repoRoot, "inventory", kindCode, "instances");
        if (!Directory.Exists(instancesDir))
        {
            return Array.Empty<InventoryFile>();
        }

        var files = new List<InventoryFile>();
        foreach (var instanceDir in Directory.EnumerateDirectories(instancesDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var instanceCode = Path.GetFileName(instanceDir);
            foreach (var path in Directory.EnumerateFiles(instanceDir, "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
            {
                if (IsOverridesFile(path))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                files.Add(new InventoryFile(
                    instanceCode,
                    $"inventory/{kindCode}/instances/{instanceCode}/{Path.GetFileName(path)}",
                    File.ReadAllText(path)));
            }
        }

        return files;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial Regex KindCodePattern();
}
