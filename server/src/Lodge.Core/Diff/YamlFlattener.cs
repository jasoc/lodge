using System.Text.Json;
using YamlDotNet.Serialization;

namespace Lodge.Core.Diff;

/// <summary>
/// Parses inventory YAML and flattens it into a map of dotted leaf-paths to
/// JSON-encoded values (e.g. "features.sso_login" -> "false"). Sequences use
/// index segments (e.g. "quirks.0"). This canonical form is what the semantic
/// diff engine compares.
/// </summary>
public static class YamlFlattener
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// Flatten YAML text into ordered dotted-path -> JSON value pairs.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Flatten(string yaml)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(yaml))
        {
            return map;
        }

        var root = Parse(yaml);
        FlattenNode(string.Empty, root, map);
        return map;
    }

    /// <summary>Parse YAML text into the raw object graph (dict/list/scalar), or null when blank.</summary>
    public static object? Parse(string? yaml)
        => string.IsNullOrWhiteSpace(yaml) ? null : Deserializer.Deserialize<object?>(yaml);

    private static void FlattenNode(string prefix, object? node, IDictionary<string, string> map)
    {
        switch (node)
        {
            case null:
                Emit(prefix, null, map);
                break;

            case IDictionary<object, object> dict:
                if (dict.Count == 0)
                {
                    Emit(prefix, dict, map);
                    break;
                }
                foreach (var kvp in dict)
                {
                    var key = Convert.ToString(kvp.Key) ?? string.Empty;
                    var childPrefix = prefix.Length == 0 ? key : $"{prefix}.{key}";
                    FlattenNode(childPrefix, kvp.Value, map);
                }
                break;

            case IList<object> list:
                if (list.Count == 0)
                {
                    Emit(prefix, list, map);
                    break;
                }
                for (var i = 0; i < list.Count; i++)
                {
                    var childPrefix = prefix.Length == 0 ? i.ToString() : $"{prefix}.{i}";
                    FlattenNode(childPrefix, list[i], map);
                }
                break;

            default:
                Emit(prefix, node, map);
                break;
        }
    }

    private static void Emit(string path, object? value, IDictionary<string, string> map)
    {
        if (path.Length == 0)
        {
            return;
        }

        map[path] = ToJsonValue(value);
    }

    /// <summary>
    /// Convert a scalar into a canonical JSON literal, inferring bool/number so
    /// that YAML "false" compares as the JSON boolean false, not the string.
    /// </summary>
    public static string ToJsonValue(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case bool b:
                return b ? "true" : "false";
            case IDictionary<object, object>:
                return "{}";
            case IList<object>:
                return "[]";
        }

        var s = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

        if (bool.TryParse(s, out var parsedBool))
        {
            return parsedBool ? "true" : "false";
        }

        if (long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedLong))
        {
            return parsedLong.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedDouble))
        {
            return JsonSerializer.Serialize(parsedDouble, JsonOptions);
        }

        return JsonSerializer.Serialize(s, JsonOptions);
    }

    /// <summary>
    /// Serialize a whole parsed YAML node (scalar, dict, or list, at any depth) into a
    /// deterministic JSON string — object keys sorted ordinally. Used to pass a whole
    /// collection item (e.g. one entry under a dynamic map) as a single value, unlike
    /// <see cref="ToJsonValue"/> which collapses nested dicts/lists to "{}"/"[]".
    /// </summary>
    public static string ToCanonicalJson(object? node)
    {
        switch (node)
        {
            case IDictionary<object, object> dict:
                var props = dict
                    .Select(kv => (Key: Convert.ToString(kv.Key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, kv.Value))
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => $"{JsonSerializer.Serialize(p.Key, JsonOptions)}:{ToCanonicalJson(p.Value)}");
                return "{" + string.Join(",", props) + "}";

            case IList<object> list:
                return "[" + string.Join(",", list.Select(ToCanonicalJson)) + "]";

            default:
                return ToJsonValue(node);
        }
    }
}
