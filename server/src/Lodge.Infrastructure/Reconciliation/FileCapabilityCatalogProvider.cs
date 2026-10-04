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
/// <see cref="Invalidate"/> (called once per cycle). The kind's <c>kind.yaml</c>
/// <c>defaults</c> are merged into every action of both. Every <c>container.build</c> action is
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

        var (defaults, defaultsError) = await LoadKindDefaultsAsync(kindCode, cancellationToken);
        if (defaultsError is not null)
        {
            errors.Add(defaultsError);
        }

        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var yaml = await File.ReadAllTextAsync(file, cancellationToken);
                    var definition = CapabilityCatalogLoader.LoadCapability(yaml, Path.GetFileName(file), defaults);
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
            // A broken kind.yaml is already reported with the generic catalog.
            var (defaults, _) = await LoadKindDefaultsAsync(kindCode, cancellationToken);
            try
            {
                var yaml = await File.ReadAllTextAsync(filePath, cancellationToken);
                var definition = CapabilityCatalogLoader.LoadCapability(yaml, Path.GetFileName(filePath), defaults);
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
    /// The <c>defaults</c> of <c>inventory/{kind}/kind.yaml</c>. A missing manifest means
    /// no defaults; a broken one is an error and also no defaults — its actions then lack
    /// those inputs and fail on their own, loudly, rather than the whole catalog going dark.
    /// </summary>
    private async Task<(KindDefaults Defaults, string? Error)> LoadKindDefaultsAsync(string kindCode, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_repoRoot, "inventory", kindCode, KindManifest.FileName);
        if (!File.Exists(path))
        {
            return (KindDefaults.None, null);
        }

        try
        {
            var yaml = await File.ReadAllTextAsync(path, cancellationToken);
            return (CapabilityCatalogLoader.LoadKindDefaults(yaml, KindManifest.FileName), null);
        }
        catch (CatalogFormatException ex)
        {
            return (KindDefaults.None, ex.Message);
        }
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
            if (signal.Files is not null)
            {
                signal.FileIndex = LoadFileIndex(kindCode, signal.Files, definition, errors);
            }

            foreach (var action in signal.Rules.SelectMany(r => r.Actions))
            {
                if (action.Container?.Build is not { } build)
                {
                    continue;
                }

                try
                {
                    var fingerprint = _playbooks.ComputeFingerprint(kindCode, build);
                    action.Container = action.Container with { Build = build with { Fingerprint = fingerprint } };
                }
                catch (Exception ex) when (ex is PlaybookContextException or IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{definition.SourceFile ?? definition.Code}, action '{action.Key}': {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Every file under <c>inventory/{kind}/{folder}</c> (relative '/'-separated path →
    /// text content) for a file-backed list signal. A missing folder is an error, not an
    /// empty index, so a typo can't silently look like "remove every file".
    /// </summary>
    private IReadOnlyDictionary<string, string> LoadFileIndex(
        string kindCode, string folder, CapabilityDefinition definition, List<string> errors)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        var kindRoot = Path.GetFullPath(Path.Combine(_repoRoot, "inventory", kindCode));
        var root = Path.GetFullPath(Path.Combine(kindRoot, folder));
        if (!root.StartsWith(kindRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !Directory.Exists(root))
        {
            errors.Add($"{definition.SourceFile ?? definition.Code}: files folder 'inventory/{kindCode}/{folder}' does not exist.");
            return index;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || info.Name.StartsWith('.'))
            {
                continue; // no symlinks out of the inventory, no dotfiles
            }
            index[Path.GetRelativePath(root, path).Replace('\\', '/')] = File.ReadAllText(path);
        }
        return index;
    }
}
