using System.Collections.Concurrent;
using Lodge.Core.Catalog;
using Lodge.Infrastructure.Execution;
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
/// <see cref="Invalidate"/> (called once per cycle). Every <c>docker.build</c> action is
/// stamped here with its playbook folder's content fingerprint, so the reconciler sees a
/// changed playbook as a changed executor config — the same way it sees changed desired
/// state — and an approval never silently carries over to different playbook code.
/// </summary>
public sealed class FileCapabilityCatalogProvider : ICapabilityCatalogProvider, ICacheInvalidatable
{
    private sealed record LoadedDefinitions(IReadOnlyList<CapabilityDefinition> Definitions, IReadOnlyList<string> Errors);

    private readonly string _repoRoot;
    private readonly PlaybookContextResolver _playbooks;
    private readonly ConcurrentDictionary<string, LoadedDefinitions> _cache = new(StringComparer.OrdinalIgnoreCase);

    public FileCapabilityCatalogProvider(IOptions<GitSnapshotOptions> options, PlaybookContextResolver playbooks)
    {
        _playbooks = playbooks;
        _repoRoot = string.IsNullOrWhiteSpace(options.Value.RepoRoot)
            ? Directory.GetCurrentDirectory()
            : options.Value.RepoRoot;
    }

    public void Invalidate() => _cache.Clear();

    public async Task<CatalogLoadResult> GetCatalogAsync(string kindCode, string instanceCode, CancellationToken cancellationToken = default)
    {
        var generic = await LoadDirectoryAsync(
            kindCode,
            $"generic:{kindCode}",
            Path.Combine(_repoRoot, "inventory", kindCode, "capabilities"),
            cancellationToken);
        var overrides = await LoadFileAsync(
            kindCode,
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

    public async Task<CatalogLoadResult> GetGenericCatalogAsync(string kindCode, CancellationToken cancellationToken = default)
    {
        var generic = await LoadDirectoryAsync(
            kindCode,
            $"generic:{kindCode}",
            Path.Combine(_repoRoot, "inventory", kindCode, "capabilities"),
            cancellationToken);

        var catalog = CapabilityCatalogLoader.Merge(kindCode, generic.Definitions, Array.Empty<CapabilityDefinition>());
        return new CatalogLoadResult(catalog, generic.Errors);
    }

    private async Task<LoadedDefinitions> LoadDirectoryAsync(string kindCode, string cacheKey, string directory, CancellationToken cancellationToken)
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
                    var definition = CapabilityCatalogLoader.LoadCapability(yaml, Path.GetFileName(file));
                    StampPlaybookFingerprints(kindCode, definition, errors);
                    definitions.Add(definition);
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

    private async Task<LoadedDefinitions> LoadFileAsync(string kindCode, string cacheKey, string filePath, CancellationToken cancellationToken)
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
                var definition = CapabilityCatalogLoader.LoadCapability(yaml, Path.GetFileName(filePath));
                StampPlaybookFingerprints(kindCode, definition, errors);
                definitions.Add(definition);
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

    /// <summary>
    /// A context that can't be fingerprinted (missing folder, no Dockerfile, path escape)
    /// surfaces as a validation error and leaves the fingerprint null: the action is still
    /// shown, but the executor refuses to build it until the folder is fixed — at which
    /// point the fingerprint appears, the config changes, and the row is re-queued.
    /// </summary>
    private void StampPlaybookFingerprints(string kindCode, CapabilityDefinition definition, List<string> errors)
    {
        foreach (var signal in definition.Signals)
        {
            foreach (var action in signal.Rules.SelectMany(r => r.Actions))
            {
                if (action.Docker?.Build is not { } build)
                {
                    continue;
                }

                try
                {
                    var fingerprint = _playbooks.ComputeFingerprint(kindCode, build);
                    action.Docker = action.Docker with { Build = build with { Fingerprint = fingerprint } };
                }
                catch (Exception ex) when (ex is PlaybookContextException or IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{definition.SourceFile ?? definition.Code}, action '{action.Key}': {ex.Message}");
                }
            }
        }
    }
}
