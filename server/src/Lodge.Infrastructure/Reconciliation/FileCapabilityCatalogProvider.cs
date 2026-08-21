using System.Collections.Concurrent;
using Lodge.Core.Catalog;
using Lodge.Infrastructure.Git;
using Microsoft.Extensions.Options;

namespace Lodge.Infrastructure.Reconciliation;

/// <summary>
/// File-backed <see cref="ICapabilityCatalogProvider"/>. Generic capability files live
/// under <c>inventory/{kind}/capabilities/*.yaml</c>; a per-instance additive override
/// file at <c>inventory/{kind}/instances/{instance}/overrides.yaml</c> — a single
/// well-known filename, since that same instance folder also holds the instance's inventory
/// data files (instance.yaml, features.yaml, ...), which are not capability rules and are
/// read by <see cref="IInventorySource"/> instead. Parsed files are cached until
/// <see cref="Invalidate"/> (called once per cycle).
/// </summary>
public sealed class FileCapabilityCatalogProvider : ICapabilityCatalogProvider, ICacheInvalidatable
{
    private sealed record LoadedDefinitions(IReadOnlyList<CapabilityDefinition> Definitions, IReadOnlyList<string> Errors);

    private readonly string _repoRoot;
    private readonly ConcurrentDictionary<string, LoadedDefinitions> _cache = new(StringComparer.OrdinalIgnoreCase);

    public FileCapabilityCatalogProvider(IOptions<GitSnapshotOptions> options)
    {
        _repoRoot = string.IsNullOrWhiteSpace(options.Value.RepoRoot)
            ? Directory.GetCurrentDirectory()
            : options.Value.RepoRoot;
    }

    public void Invalidate() => _cache.Clear();

    public async Task<CatalogLoadResult> GetCatalogAsync(string kindCode, string instanceCode, CancellationToken cancellationToken = default)
    {
        var generic = await LoadDirectoryAsync(
            $"generic:{kindCode}",
            Path.Combine(_repoRoot, "inventory", kindCode, "capabilities"),
            cancellationToken);
        var overrides = await LoadFileAsync(
            $"instance:{kindCode}/{instanceCode}",
            Path.Combine(_repoRoot, "inventory", kindCode, "instances", instanceCode, "overrides.yaml"),
            cancellationToken);

        var errors = generic.Errors.Concat(overrides.Errors).ToList();
        try
        {
            var catalog = CapabilityCatalogLoader.Merge(kindCode, generic.Definitions, overrides.Definitions);
            return new CatalogLoadResult(catalog, errors);
        }
        catch (CatalogFormatException ex)
        {
            // A broken override merge falls back to the generic catalog rather than
            // taking the instance's whole governance view down.
            errors.Add($"instance override merge for '{instanceCode}': {ex.Message}");
            var catalog = CapabilityCatalogLoader.Merge(kindCode, generic.Definitions, Array.Empty<CapabilityDefinition>());
            return new CatalogLoadResult(catalog, errors);
        }
    }

    private async Task<LoadedDefinitions> LoadDirectoryAsync(string cacheKey, string directory, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var definitions = new List<CapabilityDefinition>();
        var errors = new List<string>();

        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var yaml = await File.ReadAllTextAsync(file, cancellationToken);
                    definitions.Add(CapabilityCatalogLoader.LoadCapability(yaml, Path.GetFileName(file)));
                }
                catch (CatalogFormatException ex)
                {
                    errors.Add(ex.Message);
                }
            }
        }

        var loaded = new LoadedDefinitions(definitions, errors);
        _cache[cacheKey] = loaded;
        return loaded;
    }

    private async Task<LoadedDefinitions> LoadFileAsync(string cacheKey, string filePath, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var definitions = new List<CapabilityDefinition>();
        var errors = new List<string>();

        if (File.Exists(filePath))
        {
            try
            {
                var yaml = await File.ReadAllTextAsync(filePath, cancellationToken);
                definitions.Add(CapabilityCatalogLoader.LoadCapability(yaml, Path.GetFileName(filePath)));
            }
            catch (CatalogFormatException ex)
            {
                errors.Add(ex.Message);
            }
        }

        var loaded = new LoadedDefinitions(definitions, errors);
        _cache[cacheKey] = loaded;
        return loaded;
    }
}
