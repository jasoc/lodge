using System.Collections.Concurrent;
using Lodge.Core.Catalog;
using Lodge.Infrastructure.Execution;
using Lodge.Infrastructure.Git;
using Microsoft.Extensions.Options;

namespace Lodge.Infrastructure.Reconciliation;

/// <summary>
/// File-backed <see cref="ICapabilityCatalogProvider"/>: <see cref="InventoryCatalogLoader"/>
/// (the same code <c>lodge validate</c> runs) over the local working tree, with the parsed
/// files cached until <see cref="Invalidate"/> (called once per cycle). See the loader for
/// the layout, defaults, overrides and fingerprinting rules.
/// </summary>
public sealed class FileCapabilityCatalogProvider : ICapabilityCatalogProvider, ICacheInvalidatable
{
    private readonly InventoryCatalogLoader _loader;
    private readonly ConcurrentDictionary<string, LoadedDefinitions> _cache = new(StringComparer.OrdinalIgnoreCase);

    public FileCapabilityCatalogProvider(
        IOptions<GitSnapshotOptions> options, PlaybookContextResolver playbooks, IOptions<DockerExecutorOptions> docker)
    {
        _loader = new InventoryCatalogLoader(
            options.Value.RepoRoot, playbooks, new AutoBuildAllowlist(docker.Value.AutoBuildAllowlist));
    }

    public void Invalidate() => _cache.Clear();

    public Task<CatalogLoadResult> GetCatalogAsync(string kindCode, string instanceCode, CancellationToken cancellationToken = default)
    {
        var generic = _cache.GetOrAdd($"generic:{kindCode}", _ => _loader.LoadGeneric(kindCode, cancellationToken));
        var overrides = _cache.GetOrAdd($"instance:{kindCode}/{instanceCode}", _ => _loader.LoadOverrides(kindCode, instanceCode));
        return Task.FromResult(InventoryCatalogLoader.Combine(kindCode, instanceCode, generic, overrides));
    }

    public Task<CatalogLoadResult> GetGenericCatalogAsync(string kindCode, CancellationToken cancellationToken = default)
    {
        var generic = _cache.GetOrAdd($"generic:{kindCode}", _ => _loader.LoadGeneric(kindCode, cancellationToken));
        return Task.FromResult(InventoryCatalogLoader.CombineGeneric(kindCode, generic));
    }
}
