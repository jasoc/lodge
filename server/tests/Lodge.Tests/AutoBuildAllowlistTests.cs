using System.Runtime.Versioning;
using Lodge.Core.Catalog;
using Lodge.Core.Domain.Enums;
using Xunit;

namespace Lodge.Tests;

/// <summary>An AUTO action runs unattended; a container build runs a Dockerfile. Together they need the operator's say-so.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class AutoBuildAllowlistTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lodge-autobuild-test-").FullName;

    public AutoBuildAllowlistTests()
    {
        Write("inventory/demo/playbooks/tf/Dockerfile", "FROM alpine:3.21\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Capability(string policy, string container = "build: { context: playbooks/tf }") => $$"""
        capability: vms
        signals:
          - path: vms
            kind: keyed_collection
            rules:
              - on: add
                actions:
                  - key: create
                    policy: {{policy}}
                    executor: container
                    container: { {{container}} }
        """;

    private CatalogLoadResult Load(string yaml, params string[] allow)
    {
        Write("inventory/demo/capabilities/vms.yaml", yaml);
        return new InventoryCatalogLoader(_root, new PlaybookContextResolver(_root), new AutoBuildAllowlist(allow)).LoadCatalog("demo", "lab");
    }

    private static ActionTemplate Create(CatalogLoadResult r) => r.Catalog.Capabilities[0].Signals[0].Rules[0].Actions[0];

    [Fact]
    public void An_auto_build_that_is_not_allowlisted_is_reported_and_treated_as_manual()
    {
        var result = Load(Capability("AUTO"));

        var error = Assert.Single(result.Errors);
        Assert.Contains("vms.yaml, action 'create'", error);
        Assert.Contains("DockerExecutor__AutoBuildAllowlist__<n>=demo/playbooks/tf", error);
        Assert.Equal(ActionPolicy.MANUAL_REQUIRED, Create(result).Policy);
    }

    [Theory]
    [InlineData("demo/playbooks/tf")]
    [InlineData("/demo/playbooks/tf/")]
    [InlineData("demo/./playbooks/tf")]
    public void An_allowlisted_playbook_stays_auto(string entry)
    {
        var result = Load(Capability("AUTO"), entry);

        Assert.Empty(result.Errors);
        Assert.Equal(ActionPolicy.AUTO, Create(result).Policy);
    }

    [Theory]
    [InlineData("other/playbooks/tf")]     // another kind
    [InlineData("demo/playbooks")]         // a parent folder doesn't count
    [InlineData("demo/playbooks/tf2")]
    public void Other_entries_do_not_allow_it(string entry)
        => Assert.Equal(ActionPolicy.MANUAL_REQUIRED, Create(Load(Capability("AUTO"), entry)).Policy);

    [Fact]
    public void A_pinned_entry_allows_exactly_that_content()
    {
        var fingerprint = Create(Load(Capability("AUTO"))).Container!.Build!.Fingerprint!;

        Assert.Equal(ActionPolicy.AUTO, Create(Load(Capability("AUTO"), $"demo/playbooks/tf@{fingerprint[..12]}")).Policy);

        Write("inventory/demo/playbooks/tf/run.sh", "echo changed\n");
        var edited = Load(Capability("AUTO"), $"demo/playbooks/tf@{fingerprint[..12]}");
        Assert.Equal(ActionPolicy.MANUAL_REQUIRED, Create(edited).Policy);   // the folder changed: the pin no longer matches
        Assert.Contains("pin this content: demo/playbooks/tf@", Assert.Single(edited.Errors));

        Assert.Equal(ActionPolicy.MANUAL_REQUIRED, Create(Load(Capability("AUTO"), "demo/playbooks/tf@abc")).Policy);   // a too-short pin is no pin
    }

    [Theory]
    [InlineData("MANUAL_REQUIRED")]
    [InlineData("OPTIONAL")]
    public void Builds_that_a_human_confirms_are_unaffected(string policy)
    {
        var result = Load(Capability(policy));

        Assert.Empty(result.Errors);
        Assert.Equal(Enum.Parse<ActionPolicy>(policy), Create(result).Policy);
    }

    [Fact]
    public void An_auto_action_with_a_ready_made_image_is_unaffected()
    {
        var result = Load(Capability("AUTO", "image: \"alpine:3.21\""));

        Assert.Empty(result.Errors);
        Assert.Equal(ActionPolicy.AUTO, Create(result).Policy);
    }

    [Fact]
    public void An_instance_override_is_held_to_the_same_rule()
    {
        Load(Capability("MANUAL_REQUIRED"));
        Write("inventory/demo/instances/lab/overrides.yaml", Capability("AUTO"));

        var result = new InventoryCatalogLoader(_root, new PlaybookContextResolver(_root)).LoadCatalog("demo", "lab");

        Assert.Contains(result.Errors, e => e.Contains("overrides.yaml"));
        Assert.All(result.Catalog.Capabilities.SelectMany(c => c.Signals).SelectMany(s => s.Rules).SelectMany(r => r.Actions),
            a => Assert.NotEqual(ActionPolicy.AUTO, a.Policy));
    }
}
