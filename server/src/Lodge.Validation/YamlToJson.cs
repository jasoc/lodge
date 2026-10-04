using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace Lodge.Validation;

/// <summary>
/// Converts a YAML document into a <see cref="JsonNode"/>, inferring bool/number
/// scalars (quoted ones stay strings) so schema type checks behave as expected.
/// </summary>
public static class YamlToJson
{
    public static JsonNode? Convert(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);

        return stream.Documents.Count == 0 ? null : ConvertNode(stream.Documents[0].RootNode);
    }

    private static JsonNode? ConvertNode(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode map:
                var obj = new JsonObject();
                foreach (var entry in map.Children)
                {
                    obj[((YamlScalarNode)entry.Key).Value ?? string.Empty] = ConvertNode(entry.Value);
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
