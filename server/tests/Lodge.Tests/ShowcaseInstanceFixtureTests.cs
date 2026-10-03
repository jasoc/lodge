using System.Text.Json;
using Lodge.Core.Catalog;
using Lodge.Core.Diff;
using Lodge.Core.Domain.Enums;
using Lodge.Core.Reconciliation;
using Xunit;

namespace Lodge.Tests;

/// <summary>
/// Loads the real <c>inventory/homelab/</c> example — the inventory shipped with Lodge
/// itself, the same one a fresh clone reconciles on first boot — capabilities and the
/// "lab" instance's files, merged exactly the way the runtime loader does, and reconciles
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

    private static (CapabilityCatalog Catalog, string MergedYaml) LoadHomelabLab()
    {
        var repoRoot = RepoRoot();

        var capsDir = Path.Combine(repoRoot, "inventory", "homelab", "capabilities");
        var generic = Directory.EnumerateFiles(capsDir, "*.yaml")
            .Select(f => CapabilityCatalogLoader.LoadCapability(File.ReadAllText(f), Path.GetFileName(f)))
            .ToList();

        var catalog = CapabilityCatalogLoader.Merge("homelab", generic, Array.Empty<CapabilityDefinition>());

        var instanceDir = Path.Combine(repoRoot, "inventory", "homelab", "instances", "lab");
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
            "homelab", "lab", YamlFlattener.Parse(mergedYaml), catalog,
            Array.Empty<SucceededRecord>(), Array.Empty<LiveActionRow>()));

    [Fact]
    public void Homelab_lab_instance_loads_and_reconciles_without_validation_errors()
    {
        var (catalog, mergedYaml) = LoadHomelabLab();
        var result = Reconcile(catalog, mergedYaml);

        Assert.Empty(result.ValidationErrors);
        Assert.NotEmpty(result.Capabilities);
    }

    [Fact]
    public void Vms_are_never_destroyed_without_confirmation()
    {
        var (catalog, _) = LoadHomelabLab();

        var rules = catalog.Capabilities.Single(c => c.Code == "virtual_machines").Signals.Single().Rules;
        Assert.Equal(ActionPolicy.MANUAL_REQUIRED, rules.Single(r => r.Trigger == SignalTrigger.DELETE).Actions.Single().Policy);
    }

    [Fact]
    public void Every_listed_vm_gets_a_create_action_carrying_its_name_and_whole_spec_on_first_reconcile()
    {
        var (catalog, mergedYaml) = LoadHomelabLab();
        var result = Reconcile(catalog, mergedYaml);

        // Whatever VMs the shipped list currently holds (possibly none) — not a fixed set.
        var root = YamlFlattener.Parse(mergedYaml) as IDictionary<object, object>;
        var vms = root?["virtual_machines"] as IDictionary<object, object>;
        var listed = vms?.Keys.Select(k => k.ToString()!).Order().ToList() ?? new List<string>();
        var creates = result.ToCreate.Where(a => a.Identity.ActionKey == "create_vm").ToList();
        Assert.Equal(listed, creates.Select(a => a.Identity.ItemKey!).Order());
        Assert.All(creates, a =>
        {
            Assert.Equal(a.Identity.ItemKey, a.ResolvedInputs["vm_name"]);
            using var spec = JsonDocument.Parse(a.ResolvedInputs["spec"]!);
            Assert.True(spec.RootElement.GetProperty("cpu_cores").GetInt32() >= 1);
            Assert.Equal(ExecutorKind.Docker, a.ExecutorKind);
            Assert.Equal("playbooks/terraform-vm", a.DockerConfig!.Build!.Context);
        });
    }
}
