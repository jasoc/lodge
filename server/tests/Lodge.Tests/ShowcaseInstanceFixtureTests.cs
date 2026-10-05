using System.Text.Json;
using Lodge.Core.Catalog;
using Lodge.Core.Diff;
using Lodge.Core.Domain.Enums;
using Lodge.Core.Reconciliation;
using Lodge.Infrastructure.Execution;
using Lodge.Infrastructure.Git;
using Lodge.Infrastructure.Reconciliation;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lodge.Tests;

/// <summary>
/// Loads the real <c>inventory/homelab/</c> example — the inventory shipped with Lodge
/// itself, the same one a fresh clone reconciles on first boot — through the real file
/// catalog provider (so every playbook build context must resolve and get a fingerprint,
/// and every listed compose file must exist) and the "lab" instance's files, merged
/// exactly the way the runtime loader does, and reconciles it end-to-end. Catches drift
/// between the shipped example and the real catalog loader/schema on every test run.
/// Assertions hold for whatever VMs/records/stacks the example currently lists.
/// </summary>
public class ShowcaseInstanceFixtureTests
{
    private const string VmSignal = "proxmox.virtual_machines";
    private const string StackSignal = "proxmox.virtual_machines.*.compose";

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "inventory")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("repo root not found — expected to run under the Lodge repo tree");
    }

    private static async Task<(CapabilityCatalog Catalog, string MergedYaml)> LoadHomelabLabAsync()
    {
        var repoRoot = RepoRoot();
        var git = Options.Create(new GitSnapshotOptions { RepoRoot = repoRoot });
        var catalog = await new FileCapabilityCatalogProvider(git, new PlaybookContextResolver(repoRoot)).GetCatalogAsync("homelab", "lab");
        Assert.Empty(catalog.Errors);

        var instanceDir = Path.Combine(repoRoot, "inventory", "homelab", "instances", "lab");
        var files = Directory.EnumerateFiles(instanceDir, "*.yaml")
            .Where(f => !string.Equals(Path.GetFileName(f), "overrides.yaml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path: f, Content: File.ReadAllText(f)))
            .ToList();

        var (mergedYaml, error) = InstanceYamlMerger.Merge(files);
        Assert.Null(error);

        return (catalog.Catalog, mergedYaml!);
    }

    private static ReconciliationResult Reconcile(CapabilityCatalog catalog, string mergedYaml, IReadOnlyList<SucceededRecord>? history = null)
        => Reconciler.Reconcile(new ReconciliationInput(
            "homelab", "lab", YamlFlattener.Parse(mergedYaml), catalog,
            history ?? Array.Empty<SucceededRecord>(), Array.Empty<LiveActionRow>()));

    private static IEnumerable<ActionTemplate> AllTemplates(CapabilityCatalog catalog)
        => catalog.Capabilities.SelectMany(c => c.Signals).SelectMany(s => s.Rules).SelectMany(r => r.Actions);

    private static IEnumerable<ActionTemplate> ContainerTemplates(CapabilityCatalog catalog)
        => AllTemplates(catalog).Where(a => a.ExecutorKind == ExecutorKind.Container);

    [Fact]
    public async Task Homelab_lab_instance_loads_and_reconciles_without_validation_errors()
    {
        var (catalog, mergedYaml) = await LoadHomelabLabAsync();
        var result = Reconcile(catalog, mergedYaml);

        Assert.Empty(result.ValidationErrors);
        Assert.All(ContainerTemplates(catalog), a => Assert.NotNull(a.Container!.Build!.Fingerprint));
        // The playbooks are mocks: no action needs a secret, and every playbook shares _base.
        Assert.All(AllTemplates(catalog), a => Assert.DoesNotContain(a.Inputs, i => i.Kind == RuleInputKind.Secret));
        Assert.All(ContainerTemplates(catalog), a =>
            Assert.Equal("playbooks/_base", a.Container!.Build!.AdditionalContexts!["base"]));
    }

    [Fact]
    public async Task The_playbooks_that_run_as_root_relax_the_restrictive_container_profile_explicitly_and_only_that()
    {
        var (catalog, _) = await LoadHomelabLabAsync();

        // Terraform/Ansible/compose run as root and write to /root: relaxed explicitly, and only that.
        Assert.All(ContainerTemplates(catalog), a =>
            Assert.Equal(new ContainerSecurity(User: "image", ReadOnlyRootfs: false), a.Container!.Security));
    }

    [Fact]
    public async Task Nothing_that_creates_or_destroys_infrastructure_runs_without_confirmation()
    {
        var (catalog, _) = await LoadHomelabLabAsync();

        var templates = AllTemplates(catalog).ToDictionary(t => t.Key);

        // Edits are one MANUAL action; creations and destructions are an AUTO action behind a
        // MANUAL `executor: none` gate, however many AUTO steps sit in between.
        var manual = new[]
        {
            "apply_vm_changes", "update_image", "update_record", "update_stack",
            "approve_vm", "approve_destroy_vm", "approve_image", "approve_delete_image",
            "approve_record", "approve_delete_record", "approve_remove_stack"
        };
        var gated = new[]
        {
            "apply_vm", "destroy_vm", "download_image", "delete_image",
            "create_record", "delete_record", "deploy_stack", "remove_stack"
        };
        Assert.All(manual, key => Assert.Equal(ActionPolicy.MANUAL_REQUIRED, templates[key].Policy));
        Assert.All(templates.Values.Where(t => t.Key.StartsWith("approve_", StringComparison.Ordinal)),
            t => Assert.Equal(ExecutorKind.None, t.ExecutorKind));
        Assert.All(gated, key =>
        {
            Assert.Equal(ActionPolicy.AUTO, templates[key].Policy);
            Assert.True(BehindAGate(templates[key]), $"'{key}' is AUTO but no MANUAL gate stands in front of it");
        });

        bool BehindAGate(ActionTemplate t)
            => t.DependsOn
                .Select(d => templates[d[(d.LastIndexOf('.') + 1)..]])
                .Any(d => d.Policy == ActionPolicy.MANUAL_REQUIRED || BehindAGate(d));
    }

    [Fact]
    public async Task The_long_running_optional_actions_sleep_two_minutes_and_wait_for_nobody()
    {
        var (catalog, _) = await LoadHomelabLabAsync();

        var longRunning = AllTemplates(catalog).Where(t => t.Inputs.Any(i => i.Name == "sleep_seconds")).ToList();
        Assert.NotEmpty(longRunning);
        Assert.All(longRunning, t =>
        {
            Assert.Equal(ActionPolicy.OPTIONAL, t.Policy);
            Assert.Empty(t.DependsOn);
            Assert.Equal("120", t.Inputs.Single(i => i.Name == "sleep_seconds").Value);
        });
    }

    [Fact]
    public async Task Every_terraform_unit_is_named_after_its_item_path_and_gets_its_item_as_inputs()
    {
        var (catalog, mergedYaml) = await LoadHomelabLabAsync();
        var result = Reconcile(catalog, mergedYaml);

        var applies = result.ToCreate.Where(a => a.ResolvedInputs.ContainsKey("module") && a.ResolvedInputs.ContainsKey("inputs")).ToList();
        Assert.NotEmpty(applies);
        Assert.All(applies, a =>
        {
            Assert.StartsWith(a.Identity.SignalPath.Split('*')[0].TrimEnd('.'), a.ResolvedInputs["unit"]);
            Assert.EndsWith("." + a.Identity.ItemKey!.Split('/').Last(), a.ResolvedInputs["unit"]);
            using var inputs = JsonDocument.Parse(a.ResolvedInputs["inputs"]!);
            Assert.False(inputs.RootElement.TryGetProperty("compose", out _));
            Assert.Equal("playbooks/terraform", a.ContainerConfig!.Build!.Context);
        });
        Assert.All(applies.Where(a => a.ResolvedInputs["module"] == "cloudflare-dns-record"),
            a => Assert.Matches("^[0-9a-f]{32}$", a.ResolvedInputs["zone_id"]!));
    }

    [Fact]
    public async Task The_first_cycle_emits_every_vms_whole_chain_blocked_on_its_direct_dependency()
    {
        var (catalog, mergedYaml) = await LoadHomelabLabAsync();
        var created = Reconcile(catalog, mergedYaml).ToCreate;

        // Only the human gate is free; each VM step waits for the one before it.
        var approvals = created.Where(a => a.Identity.ActionKey == "approve_vm").ToList();
        Assert.NotEmpty(approvals);
        Assert.All(approvals, a => Assert.False(a.Blocked));
        var chain = new[] { "approve_vm", "apply_vm", "configure_vm", "verify_vm", "register_vm" };
        for (var i = 1; i < chain.Length; i++)
        {
            Assert.All(created.Where(a => a.Identity.ActionKey == chain[i]), c =>
                Assert.Equal(new[] { new ActionIdentity(VmSignal, c.Identity.ItemKey, chain[i - 1]) }, c.BlockedBy));
        }

        var deploys = created.Where(a => a.Identity.ActionKey == "deploy_stack").ToList();
        Assert.NotEmpty(deploys);
        Assert.All(deploys, d =>
            Assert.Equal(new[] { new ActionIdentity(VmSignal, d.Identity.ItemKey!.Split('/')[0], "configure_vm") }, d.BlockedBy));
    }

    [Fact]
    public async Task After_terraform_and_ansible_every_listed_compose_file_deploys_onto_its_vm()
    {
        var (catalog, mergedYaml) = await LoadHomelabLabAsync();
        var t = DateTimeOffset.UtcNow;
        var vmApplies = Reconcile(catalog, mergedYaml).ToCreate.Where(a => a.Identity.ActionKey == "apply_vm").ToList();
        var history = vmApplies.Select(a => new SucceededRecord(Guid.NewGuid(), a.Identity, SignalTrigger.ADD, a.DesiredValueJson, t, false)).ToList();

        var configures = Reconcile(catalog, mergedYaml, history).ToCreate.Where(a => a.Identity.ActionKey == "configure_vm").ToList();
        Assert.Equal(vmApplies.Count, configures.Count);
        history.AddRange(configures.Select(a => new SucceededRecord(Guid.NewGuid(), a.Identity, SignalTrigger.ADD, a.DesiredValueJson, t.AddMinutes(1), false)));

        var deploys = Reconcile(catalog, mergedYaml, history).ToCreate.Where(a => a.Identity.ActionKey == "deploy_stack").ToList();
        Assert.NotEmpty(deploys);
        Assert.All(deploys, d =>
        {
            Assert.Equal(StackSignal, d.Identity.SignalPath);
            var (vm, file) = (d.Identity.ItemKey!.Split('/')[0], d.Identity.ItemKey![(d.Identity.ItemKey.IndexOf('/') + 1)..]);
            Assert.Equal(file, d.ResolvedInputs["file"]);
            Assert.Equal(vm, d.ResolvedInputs["vm_name"]);
            Assert.Contains("services:", d.ResolvedInputs["content"]);
            Assert.Matches(@"^\d+\.\d+\.\d+\.\d+/\d+$", d.ResolvedInputs["host"]!);
            Assert.True(File.Exists(Path.Combine(RepoRoot(), "inventory", "homelab", "stacks", file)));
        });
        Assert.All(deploys, d => Assert.DoesNotContain('/', d.ResolvedInputs["file"]!));
    }
}
