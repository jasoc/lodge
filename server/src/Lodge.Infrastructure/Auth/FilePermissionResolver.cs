using Lodge.Core.Abstractions;
using Lodge.Infrastructure.Git;
using Lodge.Infrastructure.Reconciliation;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lodge.Infrastructure.Auth;

/// <summary>
/// File-backed <see cref="IPermissionResolver"/>. Reads every
/// <c>inventory/{kind}/runbook-permissions.yaml</c> under the repo root and builds a
/// runbook → allowed-groups map, cached in memory. Admins bypass; otherwise a user may
/// run a runbook when their groups intersect its allowed groups. This file is intended
/// to be the single source later consumed by Octopus/Terraform, so the same rules are
/// enforced in both places.
/// </summary>
public sealed class FilePermissionResolver : IPermissionResolver, ICacheInvalidatable
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly string _repoRoot;
    private readonly object _gate = new();
    private Lazy<IReadOnlyDictionary<string, HashSet<string>>> _permissions;

    public FilePermissionResolver(IOptions<GitSnapshotOptions> options)
    {
        _repoRoot = string.IsNullOrWhiteSpace(options.Value.RepoRoot)
            ? Directory.GetCurrentDirectory()
            : options.Value.RepoRoot;
        _permissions = new Lazy<IReadOnlyDictionary<string, HashSet<string>>>(Load);
    }

    /// <summary>Drops the cached permissions map so the next check re-reads from disk.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _permissions = new Lazy<IReadOnlyDictionary<string, HashSet<string>>>(Load);
        }
    }

    public bool CanRun(AuthenticatedUser user, string runbookRef)
    {
        if (user.IsAdmin)
        {
            return true;
        }

        var permissions = _permissions;
        if (!permissions.Value.TryGetValue(runbookRef, out var allowed) || allowed.Count == 0)
        {
            // No explicit grant means no access for non-admins (fail closed).
            return false;
        }

        return user.Groups.Any(allowed.Contains);
    }

    private IReadOnlyDictionary<string, HashSet<string>> Load()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var inventoryDir = Path.Combine(_repoRoot, "inventory");
        if (!Directory.Exists(inventoryDir))
        {
            return map;
        }

        foreach (var kindDir in Directory.EnumerateDirectories(inventoryDir))
        {
            var file = Path.Combine(kindDir, "runbook-permissions.yaml");
            if (!File.Exists(file))
            {
                continue;
            }

            var dto = Deserializer.Deserialize<PermissionsDto>(File.ReadAllText(file));
            foreach (var entry in dto?.Permissions ?? new List<PermissionEntryDto>())
            {
                if (string.IsNullOrWhiteSpace(entry.Runbook))
                {
                    continue;
                }
                map[entry.Runbook] = new HashSet<string>(
                    entry.AllowedGroups ?? new List<string>(), StringComparer.Ordinal);
            }
        }

        return map;
    }

    private sealed class PermissionsDto
    {
        public List<PermissionEntryDto>? Permissions { get; set; }
    }

    private sealed class PermissionEntryDto
    {
        public string? Runbook { get; set; }
        public List<string>? AllowedGroups { get; set; }
    }
}
