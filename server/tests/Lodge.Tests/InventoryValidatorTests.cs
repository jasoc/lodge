using System.Runtime.Versioning;
using System.Text.Json;
using Lodge.Validation;
using Xunit;

namespace Lodge.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class InventoryValidatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lodge-validate-test-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private const string Capability = """
        capability: vms
        title: "VMs"
        signals:
          - path: vms
            kind: keyed_collection
            rules:
              - on: add
                actions:
                  - key: create
                    executor: container
                    container:
                      build: { context: playbooks/tf }
                    inputs:
                      name: { from: key }
        """;

    private void ValidInventory()
    {
        Write("inventory/demo/capabilities/vms.yaml", Capability);
        Write("inventory/demo/playbooks/tf/Dockerfile", "FROM alpine:3.20\n");
        Write("inventory/demo/instances/lab/instance.yaml", "vms:\n  a: { cores: 2 }\n");
    }

    private IReadOnlyList<Diagnostic> Validate() => InventoryValidator.Validate(_root);

    [Fact]
    public void The_shipped_example_inventory_is_valid()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "inventory")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.Empty(InventoryValidator.Validate(dir!));
    }

    [Fact]
    public void A_correct_inventory_has_no_diagnostics()
    {
        ValidInventory();
        Assert.Empty(Validate());
    }

    [Fact]
    public void An_empty_tree_is_reported()
    {
        var d = Assert.Single(Validate());
        Assert.Contains("no kind folders", d.Message);
    }

    [Fact]
    public void A_broken_capability_file_is_attributed_to_that_file()
    {
        ValidInventory();
        Write("inventory/demo/capabilities/broken.yaml", "capability: x\nsignals:\n  - path: a\n    kind: nonsense\n");

        var d = Assert.Single(Validate());
        Assert.Equal("catalog", d.Check);
        Assert.Equal("inventory/demo/capabilities/broken.yaml", d.File);
        Assert.Equal("demo", d.Kind);
        Assert.Contains("unknown kind 'nonsense'", d.Message);
    }

    [Fact]
    public void A_missing_playbook_folder_is_an_error()
    {
        ValidInventory();
        Directory.Delete(Path.Combine(_root, "inventory/demo/playbooks/tf"), recursive: true);

        var d = Assert.Single(Validate());
        Assert.Equal("inventory/demo/capabilities/vms.yaml", d.File);
        Assert.Contains("does not exist", d.Message);
    }

    [Fact]
    public void A_missing_files_folder_is_an_error()
    {
        ValidInventory();
        Write("inventory/demo/capabilities/stacks.yaml", """
            capability: stacks
            signals:
              - path: stacks
                kind: scalar_list
                files: stacks
                rules:
                  - on: add
                    actions:
                      - { key: deploy, executor: http, http: { url: "https://x.test" } }
            """);
        Write("inventory/demo/instances/lab/stacks.yaml", "stacks: []\n");

        var d = Assert.Single(Validate());
        Assert.Equal("inventory/demo/capabilities/stacks.yaml", d.File);
        Assert.Contains("files folder 'inventory/demo/stacks' does not exist", d.Message);
    }

    [Fact]
    public void A_top_level_key_in_two_instance_files_is_a_yaml_error_on_the_second_file()
    {
        ValidInventory();
        Write("inventory/demo/instances/lab/more.yaml", "vms:\n  b: {}\n");

        var d = Assert.Single(Validate());
        Assert.Equal("yaml", d.Check);
        Assert.Equal("inventory/demo/instances/lab/more.yaml", d.File);
        Assert.Equal("lab", d.Instance);
    }

    [Fact]
    public void Unparseable_instance_yaml_is_a_yaml_error()
    {
        ValidInventory();
        Write("inventory/demo/instances/lab/instance.yaml", "vms: [unclosed\n");

        var d = Assert.Single(Validate());
        Assert.Equal("yaml", d.Check);
    }

    [Fact]
    public void The_kind_schema_is_checked_when_present()
    {
        ValidInventory();
        Write("inventory/demo/instance.schema.json", """
            { "type": "object", "properties": { "vms": { "type": "object",
              "additionalProperties": { "type": "object", "properties": { "cores": { "type": "string" } } } } } }
            """);

        var d = Assert.Single(Validate());
        Assert.Equal("schema", d.Check);
        Assert.Equal("inventory/demo/instances/lab", d.File);
        Assert.Contains("/vms/a/cores", d.Message);
    }

    [Fact]
    public void The_shared_schemas_folder_and_base_ref_also_work()
    {
        ValidInventory();
        Write("schemas/common/instance.base.schema.json",
            """{ "$id": "https://x.test/base.json", "type": "object", "required": ["display_name"] }""");
        Write("schemas/kinds/demo.instance.schema.json",
            """{ "$id": "https://x.test/demo.json", "allOf": [ { "$ref": "https://x.test/base.json" } ] }""");

        var d = Assert.Single(Validate());
        Assert.Equal("schema", d.Check);
    }

    [Fact]
    public void A_first_cycle_validation_error_surfaces_from_the_reconciler()
    {
        ValidInventory();
        Write("inventory/demo/instances/lab/instance.yaml", "vms:\n  - name: a\n");

        var d = Assert.Single(Validate());
        Assert.Equal("reconcile", d.Check);
        Assert.Contains("found an array", d.Message);
    }

    [Fact]
    public void Formats_text_json_and_github()
    {
        ValidInventory();
        Write("inventory/demo/capabilities/broken.yaml", "capability: x\nsignals:\n  - path: a\n    kind: nonsense\n");
        var diagnostics = Validate();

        Assert.Contains("error: inventory/demo/capabilities/broken.yaml [demo] (catalog):", DiagnosticFormatter.Format(diagnostics, ReportFormat.Text));
        Assert.StartsWith("::error file=inventory/demo/capabilities/broken.yaml,title=lodge validate (catalog)::",
            DiagnosticFormatter.Format(diagnostics, ReportFormat.Github));

        using var json = JsonDocument.Parse(DiagnosticFormatter.Format(diagnostics, ReportFormat.Json));
        Assert.False(json.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal("catalog", json.RootElement.GetProperty("diagnostics")[0].GetProperty("check").GetString());
        Assert.Equal("OK: inventory is valid.\n", DiagnosticFormatter.Format(Array.Empty<Diagnostic>(), ReportFormat.Text).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Github_annotations_escape_newlines_and_property_separators()
    {
        var d = new Diagnostic(DiagnosticSeverity.Error, "yaml", "a,b:c.yaml", null, null, "line1\nline2 100%");
        Assert.Equal("::error file=a%2Cb%3Ac.yaml,title=lodge validate (yaml)::line1%0Aline2 100%25" + Environment.NewLine,
            DiagnosticFormatter.Format(new[] { d }, ReportFormat.Github));
    }
}
