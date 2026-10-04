namespace Lodge.Core.Catalog;

/// <summary>
/// The rule catalog for one kind+instance, plus any files that failed to load.
/// Broken files are skipped (surfaced as validation errors) so one bad YAML never takes
/// the whole catalog down.
/// </summary>
public sealed record CatalogLoadResult(CapabilityCatalog Catalog, IReadOnlyList<string> Errors);

/// <summary>Capability definitions loaded from one folder or file, with the files that failed.</summary>
public sealed record LoadedDefinitions(IReadOnlyList<CapabilityDefinition> Definitions, IReadOnlyList<string> Errors);

/// <summary>
/// Builds a kind's capability catalog from an inventory tree on disk: generic capability
/// files under <c>inventory/{kind}/capabilities/*.yaml</c>, a per-instance additive override
/// file at <c>inventory/{kind}/instances/{instance}/overrides.yaml</c>, and the kind's
/// <c>kind.yaml</c> <c>defaults</c> merged into every action of both. Every
/// <c>container.build</c> action is stamped with its playbook folder's content fingerprint
/// (so the reconciler sees a changed playbook as a changed executor config), and every
/// file-backed list gets its file index. Shared by the server (which adds caching) and
/// <c>lodge validate</c>, so both accept and reject exactly the same inventories.
/// </summary>
public sealed class InventoryCatalogLoader
{
    private readonly string _repoRoot;
    private readonly PlaybookContextResolver _playbooks;

    /// <param name="repoRoot">The folder holding <c>inventory/</c>; blank means the current directory.</param>
    public InventoryCatalogLoader(string? repoRoot, PlaybookContextResolver playbooks)
    {
        _repoRoot = string.IsNullOrWhiteSpace(repoRoot) ? Directory.GetCurrentDirectory() : repoRoot;
        _playbooks = playbooks;
    }

    public LoadedDefinitions LoadGeneric(string kindCode, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(_repoRoot, "inventory", kindCode, "capabilities");
        var definitions = new List<CapabilityDefinition>();
        var errors = new List<string>();

        var (defaults, defaultsError) = LoadKindDefaults(kindCode);
        if (defaultsError is not null)
        {
            errors.Add(defaultsError);
        }

        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                LoadFile(kindCode, file, defaults, definitions, errors);
            }
        }

        return new LoadedDefinitions(definitions, errors);
    }

    public LoadedDefinitions LoadOverrides(string kindCode, string instanceCode)
    {
        var filePath = Path.Combine(_repoRoot, "inventory", kindCode, "instances", instanceCode, InventoryLayout.OverridesFileName);
        var definitions = new List<CapabilityDefinition>();
        var errors = new List<string>();

        if (File.Exists(filePath))
        {
            // A broken kind.yaml is already reported with the generic catalog.
            var (defaults, _) = LoadKindDefaults(kindCode);
            LoadFile(kindCode, filePath, defaults, definitions, errors);
        }

        return new LoadedDefinitions(definitions, errors);
    }

    /// <summary>
    /// Merges the generic definitions with an instance's overrides. A broken override merge
    /// falls back to the generic catalog (and an error) rather than taking the instance's
    /// whole governance view down.
    /// </summary>
    public static CatalogLoadResult Combine(
        string kindCode, string instanceCode, LoadedDefinitions generic, LoadedDefinitions overrides)
    {
        var errors = generic.Errors.Concat(overrides.Errors).ToList();
        try
        {
            return new CatalogLoadResult(
                CapabilityCatalogLoader.Merge(kindCode, generic.Definitions, overrides.Definitions), errors);
        }
        catch (CatalogFormatException ex)
        {
            errors.Add($"instance override merge for '{instanceCode}': {ex.Message}");
            return new CatalogLoadResult(
                CapabilityCatalogLoader.Merge(kindCode, generic.Definitions, Array.Empty<CapabilityDefinition>()), errors);
        }
    }

    /// <summary>The generic catalog alone; throws <see cref="CatalogFormatException"/> when the merged generic files are inconsistent.</summary>
    public static CatalogLoadResult CombineGeneric(string kindCode, LoadedDefinitions generic)
        => new(CapabilityCatalogLoader.Merge(kindCode, generic.Definitions, Array.Empty<CapabilityDefinition>()), generic.Errors);

    public CatalogLoadResult LoadCatalog(string kindCode, string instanceCode)
        => Combine(kindCode, instanceCode, LoadGeneric(kindCode), LoadOverrides(kindCode, instanceCode));

    private void LoadFile(
        string kindCode, string file, KindDefaults defaults, List<CapabilityDefinition> definitions, List<string> errors)
    {
        try
        {
            var definition = CapabilityCatalogLoader.LoadCapability(File.ReadAllText(file), Path.GetFileName(file), defaults);
            StampPlaybookFingerprints(kindCode, definition, errors);
            definitions.Add(definition);
        }
        catch (CatalogFormatException ex)
        {
            errors.Add(ex.Message);
        }
    }

    /// <summary>
    /// The <c>defaults</c> of <c>inventory/{kind}/kind.yaml</c>. A missing manifest means
    /// no defaults; a broken one is an error and also no defaults — its actions then lack
    /// those inputs and fail on their own, loudly, rather than the whole catalog going dark.
    /// </summary>
    private (KindDefaults Defaults, string? Error) LoadKindDefaults(string kindCode)
    {
        var path = Path.Combine(_repoRoot, "inventory", kindCode, InventoryLayout.KindManifestFileName);
        if (!File.Exists(path))
        {
            return (KindDefaults.None, null);
        }

        try
        {
            return (CapabilityCatalogLoader.LoadKindDefaults(File.ReadAllText(path), InventoryLayout.KindManifestFileName), null);
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
