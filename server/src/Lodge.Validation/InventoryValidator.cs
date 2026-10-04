using System.Text.RegularExpressions;
using Lodge.Core.Catalog;
using Lodge.Core.Diff;
using Lodge.Core.Reconciliation;

namespace Lodge.Validation;

public enum DiagnosticSeverity
{
    Error
}

/// <summary>
/// One problem found in an inventory. <see cref="File"/> is repo-relative with '/'
/// separators and points at the best-known culprit (the capability file named in the
/// message, else the instance's folder, else the kind's); <see cref="Check"/> names the
/// step that found it: <c>yaml</c>, <c>catalog</c>, <c>schema</c> or <c>reconcile</c>.
/// </summary>
public sealed record Diagnostic(
    DiagnosticSeverity Severity, string Check, string File, string? Kind, string? Instance, string Message);

/// <summary>
/// Validates an inventory tree the way the server will read it, offline: instance YAML
/// merged with <see cref="InstanceYamlMerger"/>, the capability catalog via
/// <see cref="InventoryCatalogLoader"/> (which also proves every playbook and <c>files:</c>
/// folder exists), the kind's JSON Schema when it has one, and finally
/// <see cref="Reconciler.Reconcile"/> with an empty history — a first-boot dry run that
/// surfaces everything the engine would report on its first cycle. Nothing is written,
/// executed or fetched.
/// </summary>
public static partial class InventoryValidator
{
    /// <param name="repoRoot">The folder holding <c>inventory/</c>.</param>
    /// <param name="kinds">Only these kinds; null for every kind folder.</param>
    /// <param name="autoBuildAllowlist">
    /// The playbooks AUTO actions may build — mirror the server's <c>DockerExecutor__AutoBuildAllowlist</c>
    /// so CI fails on exactly what the server would downgrade. Null allows none.
    /// </param>
    public static IReadOnlyList<Diagnostic> Validate(
        string repoRoot, IReadOnlyCollection<string>? kinds = null, AutoBuildAllowlist? autoBuildAllowlist = null)
    {
        var diagnostics = new List<Diagnostic>();
        var loader = new InventoryCatalogLoader(repoRoot, new PlaybookContextResolver(repoRoot), autoBuildAllowlist);

        var kindCodes = InventoryLayout.ListKinds(repoRoot);
        if (kinds is not null)
        {
            foreach (var missing in kinds.Where(k => !kindCodes.Contains(k, StringComparer.Ordinal)))
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "yaml", $"inventory/{missing}", missing, null,
                    $"kind '{missing}' has no folder under inventory/."));
            }
            kindCodes = kindCodes.Where(k => kinds.Contains(k, StringComparer.Ordinal)).ToList();
        }
        if (kindCodes.Count == 0 && kinds is null)
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "yaml", "inventory", null, null,
                "no kind folders found: expected inventory/<kind>/ directories."));
        }

        foreach (var kindCode in kindCodes)
        {
            ValidateKind(repoRoot, kindCode, loader, diagnostics);
        }

        return diagnostics;
    }

    private static void ValidateKind(string repoRoot, string kindCode, InventoryCatalogLoader loader, List<Diagnostic> diagnostics)
    {
        var kindDir = $"inventory/{kindCode}";
        var generic = loader.LoadGeneric(kindCode);
        foreach (var error in generic.Errors)
        {
            diagnostics.Add(CatalogDiagnostic(repoRoot, kindCode, null, error));
        }

        // Instances: the folders with data files, plus ones that only carry overrides.
        var filesByInstance = InventoryLayout.ReadInstanceFiles(repoRoot, kindCode)
            .GroupBy(f => f.InstanceCode, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        if (filesByInstance.Count == 0)
        {
            // Still prove the generic catalog is consistent with itself.
            try
            {
                InventoryCatalogLoader.CombineGeneric(kindCode, generic);
            }
            catch (CatalogFormatException ex)
            {
                diagnostics.Add(CatalogDiagnostic(repoRoot, kindCode, null, ex.Message));
            }
        }

        foreach (var (instanceCode, files) in filesByInstance)
        {
            var instanceDir = $"{kindDir}/instances/{instanceCode}";

            var (mergedYaml, mergeError) = MergeSafely(files);
            if (mergeError is not null)
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "yaml", MergeErrorFile(files, mergeError, instanceDir),
                    kindCode, instanceCode, mergeError));
                continue;
            }

            var overrides = loader.LoadOverrides(kindCode, instanceCode);
            foreach (var error in overrides.Errors)
            {
                diagnostics.Add(CatalogDiagnostic(repoRoot, kindCode, instanceCode, error));
            }

            CatalogLoadResult catalog;
            try
            {
                catalog = InventoryCatalogLoader.Combine(kindCode, instanceCode, generic, new LoadedDefinitions(overrides.Definitions, Array.Empty<string>()));
            }
            catch (CatalogFormatException ex)
            {
                diagnostics.Add(CatalogDiagnostic(repoRoot, kindCode, instanceCode, ex.Message));
                continue;
            }
            // Combine's own fallback message (a broken override merge) is not in either list.
            foreach (var error in catalog.Errors.Except(generic.Errors).Except(overrides.Errors))
            {
                diagnostics.Add(CatalogDiagnostic(repoRoot, kindCode, instanceCode, error));
            }

            foreach (var violation in InstanceSchemaValidator.Validate(repoRoot, kindCode, mergedYaml!))
            {
                var at = violation.Location.Length == 0 ? "" : $"{violation.Location}: ";
                diagnostics.Add(new(DiagnosticSeverity.Error, "schema", instanceDir, kindCode, instanceCode, at + violation.Message));
            }

            var result = Reconciler.Reconcile(new ReconciliationInput(
                kindCode, instanceCode, YamlFlattener.Parse(mergedYaml!), catalog.Catalog,
                Array.Empty<SucceededRecord>(), Array.Empty<LiveActionRow>()));
            foreach (var error in result.ValidationErrors)
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "reconcile", instanceDir, kindCode, instanceCode, error));
            }
        }
    }

    private static (string? Yaml, string? Error) MergeSafely(IReadOnlyList<InventoryFile> files)
    {
        try
        {
            return InstanceYamlMerger.Merge(files.Select(f => (f.RepoRelativePath, f.Content)).ToList());
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException)
        {
            return (null, $"invalid YAML — {ex.Message}");
        }
    }

    /// <summary>The merge error names the offending file when it knows it; otherwise the instance folder.</summary>
    private static string MergeErrorFile(IReadOnlyList<InventoryFile> files, string error, string instanceDir)
        => files.Select(f => f.RepoRelativePath).FirstOrDefault(p => error.StartsWith(p, StringComparison.Ordinal)) ?? instanceDir;

    private static Diagnostic CatalogDiagnostic(string repoRoot, string kindCode, string? instanceCode, string message)
    {
        var kindDir = $"inventory/{kindCode}";
        string file = kindDir;
        if (FileNamePrefix().Match(message) is { Success: true } m)
        {
            var name = m.Groups[1].Value;
            var candidates = new List<string> { $"{kindDir}/capabilities/{name}", $"{kindDir}/{name}" };
            if (instanceCode is not null)
            {
                candidates.Insert(0, $"{kindDir}/instances/{instanceCode}/{name}");
            }
            file = candidates.FirstOrDefault(c => File.Exists(Path.Combine(repoRoot, c))) ?? file;
        }
        else if (instanceCode is not null && message.StartsWith("instance override merge", StringComparison.Ordinal))
        {
            file = $"{kindDir}/instances/{instanceCode}/{InventoryLayout.OverridesFileName}";
        }

        return new Diagnostic(DiagnosticSeverity.Error, "catalog", file, kindCode, instanceCode, message);
    }

    /// <summary>Catalog errors start with the file they came from: <c>virtual_machines.yaml (capability 'x'): ...</c> or <c>kind.yaml: ...</c>.</summary>
    [GeneratedRegex(@"^([A-Za-z0-9_.\-]+\.ya?ml)\b")]
    private static partial Regex FileNamePrefix();
}
