using Lodge.Core.Catalog;
using Lodge.Core.Diff;
using Lodge.Core.Reconciliation;
using Xunit;

namespace Lodge.Tests;

public class ViewEvaluatorTests
{
    private static string? Json(System.Text.Json.Nodes.JsonNode? node) => node?.ToJsonString();

    private static CapabilityDefinition View() => CapabilityCatalogLoader.LoadCapability("""
        capability: vm_sizes
        title: "VM sizes"
        signals:
          - path: proxmox.virtual_machines.*.cores
            label: CPU
          - path: proxmox.virtual_machines.*.memory_mb
            label: RAM (MB)
          - path: proxmox.node
        """);

    [Fact]
    public void Per_item_signals_become_one_table_sorted_by_key_and_plain_signals_single_values()
    {
        var root = YamlFlattener.Parse("""
            proxmox:
              node: pve
              virtual_machines:
                zeta: { cores: 2, memory_mb: 2048 }
                alpha: { cores: 4 }
            """);

        var data = ViewEvaluator.Evaluate(View(), root);

        var table = Assert.Single(data.Tables);
        Assert.Equal("proxmox.virtual_machines", table.Collection);
        Assert.Equal(new[] { "CPU", "RAM (MB)" }, table.Columns.Select(c => c.Label));
        Assert.Equal(new[] { "alpha", "zeta" }, table.Rows.Select(r => r.Key));
        Assert.Equal(new string?[] { "4", null }, table.Rows[0].Values.Select(Json));
        Assert.Equal(new string?[] { "2", "2048" }, table.Rows[1].Values.Select(Json));

        var value = Assert.Single(data.Values);
        Assert.Equal("node", value.Label);
        Assert.Equal("\"pve\"", Json(value.Value));
    }

    [Fact]
    public void Values_keep_their_shape_as_typed_json()
    {
        var view = CapabilityCatalogLoader.LoadCapability("""
            capability: stacks_view
            signals:
              - path: vms.*.compose
              - path: vms.*.disks
              - path: vms.*.on
            """);
        var data = ViewEvaluator.Evaluate(view, YamlFlattener.Parse("""
            vms:
              a:
                compose: [x.yml, y.yml]
                disks: [{ size: 10, bus: scsi }]
                on: true
            """));

        Assert.Equal(new[] { "[\"x.yml\",\"y.yml\"]", "[{\"bus\":\"scsi\",\"size\":10}]", "true" },
            data.Tables[0].Rows[0].Values.Select(Json));
    }

    [Fact]
    public void A_missing_collection_is_an_empty_table()
    {
        var data = ViewEvaluator.Evaluate(View(), YamlFlattener.Parse("other: 1"));

        Assert.Empty(Assert.Single(data.Tables).Rows);
        Assert.Null(Assert.Single(data.Values).Value);
    }

    [Fact]
    public void Views_are_never_reconciled()
    {
        var catalog = CapabilityCatalogLoader.Merge("homelab", new[] { View() }, Array.Empty<CapabilityDefinition>());

        var result = Reconciler.Reconcile(new ReconciliationInput(
            "homelab", "lab", YamlFlattener.Parse("proxmox:\n  virtual_machines:\n    a: { cores: 1 }"), catalog,
            Array.Empty<SucceededRecord>(), Array.Empty<LiveActionRow>()));

        Assert.Empty(result.Capabilities);
        Assert.Empty(result.ToCreate);
        Assert.Empty(result.ValidationErrors);
    }
}
