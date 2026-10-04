using Lodge.Validation;
using Xunit;

namespace Lodge.Tests;

/// <summary>
/// The generated JSON Schemas (schemas/capability.schema.json, schemas/kind.schema.json) against
/// the inventory the repo ships and against deliberately wrong files. CI separately fails when
/// regenerating the schemas changes anything; these prove the output is usable and agrees with
/// the loader on what a good file looks like.
/// </summary>
public sealed class GeneratedSchemaTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "schemas")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static IReadOnlyList<SchemaViolation> Check(string schema, string yaml)
        => InstanceSchemaValidator.ValidateAgainst(Path.Combine(RepoRoot(), "schemas", schema), null, yaml);

    public static IEnumerable<object[]> ShippedCapabilityFiles()
        => Directory.EnumerateFiles(Path.Combine(RepoRoot(), "inventory"), "*.yaml", SearchOption.AllDirectories)
            .Where(f => f.Replace('\\', '/').Contains("/capabilities/"))
            .Select(f => new object[] { Path.GetRelativePath(RepoRoot(), f) });

    [Theory]
    [MemberData(nameof(ShippedCapabilityFiles))]
    public void Shipped_capability_files_satisfy_the_capability_schema(string relativePath)
        => Assert.Empty(Check("capability.schema.json", File.ReadAllText(Path.Combine(RepoRoot(), relativePath))));

    [Fact]
    public void The_shipped_kind_manifest_satisfies_the_kind_schema()
        => Assert.Empty(Check("kind.schema.json", File.ReadAllText(Path.Combine(RepoRoot(), "inventory", "homelab", "kind.yaml"))));

    private const string Valid = """
        capability: c
        signals:
          - path: features.x
            rules:
              - when: true
                actions:
                  - key: k
                    executor: container
                    container: { image: "alpine:3.21" }
                    inputs: { a: { const: 5 }, pass_pat: ~ }
        """;

    [Fact]
    public void A_minimal_correct_file_is_valid_including_scalar_consts_and_dropped_defaults()
        => Assert.Empty(Check("capability.schema.json", Valid));

    [Theory]
    [InlineData("container: { image: \"alpine:3.21\" }", "container: { image: \"alpine:3.21\", timeout_second: 5 }")]   // a typo'd key
    [InlineData("container: { image: \"alpine:3.21\" }", "container: { image: \"alpine:3.21\", timeout_seconds: 0 }")]
    [InlineData("container: { image: \"alpine:3.21\" }", "container: { image: \"alpine:3.21\", resources: { pids: 0 } }")]
    [InlineData("container: { image: \"alpine:3.21\" }", "container: { image: \"alpine:3.21\", resources: { memory: lots } }")]
    [InlineData("container: { image: \"alpine:3.21\" }", "container: { image: \"alpine:3.21\", security: { user: \"root; id\" } }")]
    [InlineData("container: { image: \"alpine:3.21\" }", "container: { image: \"alpine:3.21\", build: { context: x } }")]   // image XOR build
    [InlineData("container: { image: \"alpine:3.21\" }", "container: { }")]
    [InlineData("executor: container", "executor: docker")]
    [InlineData("executor: container\n            container: { image: \"alpine:3.21\" }", "executor: container")]   // block missing
    [InlineData("- key: k", "- label: no key")]
    public void The_schema_catches_what_the_loader_would_reject(string from, string to)
        => Assert.NotEmpty(Check("capability.schema.json", Valid.Replace(from, to)));

    [Fact]
    public void A_capability_without_its_code_is_invalid()
        => Assert.NotEmpty(Check("capability.schema.json", Valid.Replace("capability: c\n", "")));

    [Fact]
    public void An_unknown_kind_manifest_key_is_invalid()
        => Assert.NotEmpty(Check("kind.schema.json", "name: x\ndefualts: {}\n"));
}
