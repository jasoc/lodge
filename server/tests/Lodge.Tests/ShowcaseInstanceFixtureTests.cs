using Lodge.Core.Catalog;
using Lodge.Core.Diff;
using Lodge.Core.Domain.Enums;
using Lodge.Core.Reconciliation;
using Xunit;

namespace Lodge.Tests;

/// <summary>
/// Loads the real <c>inventory/acme/</c> example — the fixture shipped with Lodge itself,
/// the same one a fresh clone reconciles on first boot — capabilities and the "demo"
/// instance's split files, merged exactly the way the runtime loader does, and reconciles
/// it end-to-end. Catches drift between the shipped example and the real catalog
/// loader/schema on every test run, not just by eyeballing it after `./scripts/run.sh`.
/// </summary>
public class ShowcaseInstanceFixtureTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "inventory")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("repo root not found — expected to run under the Lodge repo tree");
    }

    private static (CapabilityCatalog Catalog, string MergedYaml) LoadAcmeDemo()
    {
        var repoRoot = RepoRoot();

        var capsDir = Path.Combine(repoRoot, "inventory", "acme", "capabilities");
        var generic = Directory.EnumerateFiles(capsDir, "*.yaml")
            .Select(f => CapabilityCatalogLoader.LoadCapability(File.ReadAllText(f), Path.GetFileName(f)))
            .ToList();

        var catalog = CapabilityCatalogLoader.Merge("acme", generic, Array.Empty<CapabilityDefinition>());

        var instanceDir = Path.Combine(repoRoot, "inventory", "acme", "instances", "demo");
        var files = Directory.EnumerateFiles(instanceDir, "*.yaml")
            .Where(f => !string.Equals(Path.GetFileName(f), "overrides.yaml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path: f, Content: File.ReadAllText(f)))
            .ToList();

        var (mergedYaml, error) = InstanceYamlMerger.Merge(files);
        Assert.Null(error);

        return (catalog, mergedYaml!);
    }

    private static ReconciliationResult Reconcile(CapabilityCatalog catalog, string mergedYaml)
        => Reconciler.Reconcile(new ReconciliationInput(
            "acme", "demo", YamlFlattener.Parse(mergedYaml), catalog,
            Array.Empty<SucceededRecord>(), Array.Empty<LiveActionRow>()));

    [Fact]
    public void Acme_demo_instance_loads_and_reconciles_without_validation_errors()
    {
        var (catalog, mergedYaml) = LoadAcmeDemo();
        var result = Reconcile(catalog, mergedYaml);

        Assert.Empty(result.ValidationErrors);
        Assert.NotEmpty(result.Capabilities);
    }

    [Fact]
    public void Acme_demo_capability_declares_both_policies_across_its_state_rules()
    {
        var (catalog, _) = LoadAcmeDemo();

        var services = catalog.Capabilities.Single(c => c.Code == "services");
        var templates = services.Signals.Single().Rules.SelectMany(r => r.Actions).ToList();
        // when: true -> AUTO provision; when: false -> MANUAL_REQUIRED decommission —
        // never auto-tear-down something without a human looking first.
        Assert.Contains(templates, a => a.Policy == ActionPolicy.AUTO);
        Assert.Contains(templates, a => a.Policy == ActionPolicy.MANUAL_REQUIRED);
    }

    [Fact]
    public void Acme_demo_instance_web_service_is_enabled_and_satisfied_on_first_reconcile()
    {
        var (catalog, mergedYaml) = LoadAcmeDemo();
        var result = Reconcile(catalog, mergedYaml);

        var services = result.Capabilities.Single(c => c.Code == "services");
        // Nothing has ever succeeded yet (no history passed in), so the AUTO provision
        // action is required — this is exactly what a fresh clone's first reconcile does.
        Assert.Contains(result.ToCreate, a => a.Identity == new ActionIdentity("services.web.enabled", null, "provision_service"));
    }
}
