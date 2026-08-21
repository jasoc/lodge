using System.Text.Json.Nodes;
using Lodge.Core.Diff;
using Json.Schema;
using YamlDotNet.RepresentationModel;

// validate-schema: validates an inventory instance document against the shared base JSON
// Schema plus a kind-specific schema. Exits non-zero on any violation, so the PR
// workflow fails on invalid inventory.
//
// Usage:
//   validate-schema <instance.yaml|instance-dir> <base.schema.json> <kind.schema.json>
//
// A single file is validated as-is (back-compat). A directory is treated as one
// instance's split-file folder: every *.yaml file in it except overrides.yaml (capability
// rule overrides, not inventory data) is shallow-merged via the same InstanceYamlMerger
// the runtime loader uses, so the validated document can never drift from what the
// reconciler actually evaluates.

if (args.Length is < 3 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage: validate-schema <instance.yaml|instance-dir> <base.schema.json> <kind.schema.json>");
    return args.Length == 0 ? 2 : 0;
}

var instancePath = args[0];
var baseSchemaPath = args[1];
var kindSchemaPath = args[2];

if (!File.Exists(instancePath) && !Directory.Exists(instancePath))
{
    Console.Error.WriteLine($"File not found: {instancePath}");
    return 2;
}
foreach (var p in new[] { baseSchemaPath, kindSchemaPath })
{
    if (!File.Exists(p))
    {
        Console.Error.WriteLine($"File not found: {p}");
        return 2;
    }
}

string yamlText;
if (Directory.Exists(instancePath))
{
    var files = Directory.EnumerateFiles(instancePath, "*.yaml")
        .Where(f => !string.Equals(Path.GetFileName(f), "overrides.yaml", StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f, StringComparer.Ordinal)
        .Select(f => (Path: f, Content: File.ReadAllText(f)))
        .ToList();

    var (mergedYaml, mergeError) = InstanceYamlMerger.Merge(files);
    if (mergeError is not null)
    {
        Console.Error.WriteLine($"Failed to merge instance folder '{instancePath}': {mergeError}");
        return 2;
    }
    yamlText = mergedYaml!;
}
else
{
    yamlText = File.ReadAllText(instancePath);
}

JsonNode? instance;
try
{
    instance = YamlToJson.Convert(yamlText);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to parse YAML '{instancePath}': {ex.Message}");
    return 2;
}

// Register the base schema by its $id so the kind schema's $ref resolves.
var baseSchema = JsonSchema.FromText(File.ReadAllText(baseSchemaPath));
SchemaRegistry.Global.Register(baseSchema);

var kindSchema = JsonSchema.FromText(File.ReadAllText(kindSchemaPath));

var options = new EvaluationOptions
{
    OutputFormat = OutputFormat.List
};

using var doc = System.Text.Json.JsonDocument.Parse(instance?.ToJsonString() ?? "null");
var result = kindSchema.Evaluate(doc.RootElement, options);

if (result.IsValid)
{
    Console.WriteLine($"VALID: {instancePath}");
    return 0;
}

Console.Error.WriteLine($"INVALID: {instancePath}");
foreach (var detail in result.Details ?? Enumerable.Empty<EvaluationResults>())
{
    if (detail.Errors is null || detail.Errors.Count == 0)
    {
        continue;
    }
    foreach (var error in detail.Errors)
    {
        Console.Error.WriteLine($"  {detail.InstanceLocation}: {error.Value}");
    }
}
return 1;

/// <summary>
/// Converts a YAML document into a <see cref="JsonNode"/>, inferring bool/number
/// scalars so schema type checks behave as expected.
/// </summary>
internal static class YamlToJson
{
    public static JsonNode? Convert(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);

        if (stream.Documents.Count == 0)
        {
            return null;
        }

        return ConvertNode(stream.Documents[0].RootNode);
    }

    private static JsonNode? ConvertNode(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode map:
                var obj = new JsonObject();
                foreach (var entry in map.Children)
                {
                    var key = ((YamlScalarNode)entry.Key).Value ?? string.Empty;
                    obj[key] = ConvertNode(entry.Value);
                }
                return obj;

            case YamlSequenceNode seq:
                var arr = new JsonArray();
                foreach (var item in seq.Children)
                {
                    arr.Add(ConvertNode(item));
                }
                return arr;

            case YamlScalarNode scalar:
                return ConvertScalar(scalar);

            default:
                return null;
        }
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        // Quoted scalars are always strings.
        if (scalar.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted)
        {
            return JsonValue.Create(scalar.Value ?? string.Empty);
        }

        var value = scalar.Value;
        if (value is null || value.Length == 0 || value is "null" or "~")
        {
            return null;
        }

        if (bool.TryParse(value, out var b))
        {
            return JsonValue.Create(b);
        }

        if (long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l))
        {
            return JsonValue.Create(l);
        }

        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            return JsonValue.Create(d);
        }

        return JsonValue.Create(value);
    }
}
