using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Lodge.Core.Catalog.Documents;

// gen-schema: writes the JSON Schemas of the YAML files the catalog loader reads
// (capability files and a kind's kind.yaml) from the loader's own document classes in
// Lodge.Core, so the schemas can't drift from the code: descriptions come from their
// [Description] attributes, enums from [AllowedValues], patterns from [RegularExpression],
// bounds from [Range], required keys from [JsonRequired].
//
// Usage: gen-schema <output-dir>      (scripts/gen-schema.sh runs it on schemas/)
//
// The output is deterministic, so CI regenerates it and fails on any diff. The schemas
// are for editor support: `lodge validate` keeps using the loader for the semantic errors.

if (args.Length != 1 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("Usage: gen-schema <output-dir>");
    return args.Length == 0 ? 1 : 0;
}

var outputDir = args[0];
Directory.CreateDirectory(outputDir);

Write(outputDir, "capability.schema.json", typeof(CapabilityDocument), "Lodge capability");
Write(outputDir, "kind.schema.json", typeof(KindDocument), "Lodge kind manifest");
return 0;

static void Write(string outputDir, string fileName, Type type, string title)
{
    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var exporter = new JsonSchemaExporterOptions
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = Transform
    };

    var schema = (JsonObject)JsonSchemaExporter.GetJsonSchemaAsNode(options, type, exporter);

    // Header first, then the exported body, so the file reads top-down.
    var ordered = new JsonObject
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["$id"] = $"https://lodge.dev/schemas/{fileName}",
        ["title"] = title
    };
    var body = schema.ToArray();
    foreach (var (key, value) in body)
    {
        schema.Remove(key);
        ordered[key] = value;
    }

    var json = ordered.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
    File.WriteAllText(Path.Combine(outputDir, fileName), json.ReplaceLineEndings("\n") + "\n");
    Console.WriteLine($"wrote {Path.Combine(outputDir, fileName)}");
}

static JsonNode Transform(JsonSchemaExporterContext context, JsonNode node)
{
    if (node is not JsonObject schema)
    {
        return node;
    }

    var attributes = context.PropertyInfo?.AttributeProvider;
    T? Attr<T>() where T : Attribute => (T?)attributes?.GetCustomAttributes(typeof(T), inherit: false).FirstOrDefault();

    // Every key is optional-looking YAML: a null value (`key:`) is fine, so "null" is noise
    // in the type — except for an input, where `name: ~` is how a default is dropped.
    if (context.TypeInfo.Type == typeof(InputDocument))
    {
        schema["type"] = new JsonArray("object", "null");
    }
    else
    {
        StripNull(schema);
    }

    // Class-level description (the object itself) or property-level one.
    var description = Attr<DescriptionAttribute>()?.Description
        ?? (context.PropertyInfo is null ? context.TypeInfo.Type.GetCustomAttribute<DescriptionAttribute>()?.Description : null);
    if (description is not null)
    {
        schema["description"] = description;
    }

    if (Attr<AllowedValuesAttribute>() is { } allowed)
    {
        schema["enum"] = new JsonArray(allowed.Values.Select(v => (JsonNode?)JsonValue.Create(v?.ToString())).ToArray());
    }
    if (Attr<RegularExpressionAttribute>() is { } pattern)
    {
        schema["pattern"] = pattern.Pattern;
    }
    if (Attr<RangeAttribute>() is { } range)
    {
        var integer = schema["type"]?.GetValue<string>() == "integer";
        JsonNode? Bound(object value) => integer
            ? JsonValue.Create(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture))
            : JsonValue.Create(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
        schema["minimum"] = Bound(range.Minimum);
        if (Convert.ToDouble(range.Maximum, System.Globalization.CultureInfo.InvariantCulture) < int.MaxValue)
        {
            schema["maximum"] = Bound(range.Maximum);
        }
    }
    if (Attr<ScalarValueAttribute>() is not null)
    {
        var scalar = new JsonObject { ["type"] = new JsonArray("string", "number", "boolean", "null") };
        if (schema["additionalProperties"] is not null)
        {
            schema["additionalProperties"] = scalar;
        }
        else
        {
            schema.Remove("type");
            foreach (var (key, value) in scalar.ToArray())
            {
                scalar.Remove(key);
                schema[key] = value;
            }
        }
    }

    // A container block is an image XOR a build; an executor names its block.
    if (context.TypeInfo.Type == typeof(ContainerDocument))
    {
        schema["oneOf"] = new JsonArray(
            new JsonObject { ["required"] = new JsonArray("image") },
            new JsonObject { ["required"] = new JsonArray("build") });
    }
    if (context.TypeInfo.Type == typeof(ActionDocument) && context.PropertyInfo is null)
    {
        schema["allOf"] = new JsonArray(
            ExecutorNeedsBlock("container"),
            ExecutorNeedsBlock("http"));
    }

    return schema;
}

static JsonObject ExecutorNeedsBlock(string executor) => new()
{
    ["if"] = new JsonObject
    {
        ["properties"] = new JsonObject { ["executor"] = new JsonObject { ["const"] = executor } },
        ["required"] = new JsonArray("executor")
    },
    ["then"] = new JsonObject { ["required"] = new JsonArray(executor) }
};

static void StripNull(JsonObject schema)
{
    if (schema["type"] is JsonArray types)
    {
        var kept = types.Select(t => t?.GetValue<string>()).Where(t => t != "null").ToList();
        schema["type"] = kept.Count == 1 ? JsonValue.Create(kept[0]) : new JsonArray(kept.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
    }
}
