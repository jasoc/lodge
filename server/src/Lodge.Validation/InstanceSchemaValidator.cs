using System.Text.Json;
using Json.Schema;

namespace Lodge.Validation;

/// <summary>
/// Checks a kind's merged instance YAML against its JSON Schema, when it has one. A kind's
/// schema is <c>inventory/{kind}/instance.schema.json</c> (next to the kind it describes) or,
/// for inventories that keep their schemas apart, <c>schemas/kinds/{kind}.instance.schema.json</c>
/// at the repo root; <c>schemas/common/instance.base.schema.json</c>, when present, is
/// registered so a kind schema can <c>$ref</c> it by <c>$id</c>. No schema means no check.
/// The schema is for shape (and editor support): semantic rules stay with the loader.
/// </summary>
public static class InstanceSchemaValidator
{
    private const string BaseSchemaRelativePath = "schemas/common/instance.base.schema.json";

    /// <summary>The kind's schema file, or null when it declares none.</summary>
    public static string? FindKindSchema(string repoRoot, string kindCode)
    {
        var local = Path.Combine(repoRoot, "inventory", kindCode, "instance.schema.json");
        if (File.Exists(local))
        {
            return local;
        }

        var shared = Path.Combine(repoRoot, "schemas", "kinds", $"{kindCode}.instance.schema.json");
        return File.Exists(shared) ? shared : null;
    }

    /// <summary>
    /// The violations of <paramref name="mergedYaml"/> against the kind's schema as
    /// (instance location, message) pairs — empty when valid or when the kind has no
    /// schema; a schema or YAML that can't be read is itself reported as a violation.
    /// </summary>
    public static IReadOnlyList<SchemaViolation> Validate(string repoRoot, string kindCode, string mergedYaml)
    {
        var schemaPath = FindKindSchema(repoRoot, kindCode);
        if (schemaPath is null)
        {
            return Array.Empty<SchemaViolation>();
        }

        var registry = new SchemaRegistry();
        var buildOptions = new BuildOptions { SchemaRegistry = registry };
        JsonSchema kindSchema;
        try
        {
            var basePath = Path.Combine(repoRoot, BaseSchemaRelativePath);
            if (File.Exists(basePath))
            {
                JsonSchema.FromText(File.ReadAllText(basePath), buildOptions);
            }
            kindSchema = JsonSchema.FromText(File.ReadAllText(schemaPath), buildOptions);
        }
        catch (Exception ex) when (ex is JsonException or JsonSchemaException or IOException)
        {
            return new[] { new SchemaViolation("", $"schema '{Path.GetFileName(schemaPath)}' is not usable: {ex.Message}") };
        }

        System.Text.Json.Nodes.JsonNode? instance;
        try
        {
            instance = YamlToJson.Convert(mergedYaml);
        }
        catch (Exception ex)
        {
            return new[] { new SchemaViolation("", $"instance YAML could not be parsed: {ex.Message}") };
        }

        using var doc = JsonDocument.Parse(instance?.ToJsonString() ?? "null");
        var result = kindSchema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid)
        {
            return Array.Empty<SchemaViolation>();
        }

        return (result.Details ?? Enumerable.Empty<EvaluationResults>())
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => new SchemaViolation(d.InstanceLocation.ToString(), e.Value)))
            .ToList();
    }
}

/// <summary>One schema violation: a JSON-pointer-style location in the instance, and what's wrong there.</summary>
public sealed record SchemaViolation(string Location, string Message);
