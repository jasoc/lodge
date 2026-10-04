using YamlDotNet.Serialization;

namespace Lodge.Core.Diff;

/// <summary>
/// Shallow-merges a instance's per-file YAML documents (<c>instance.yaml</c>,
/// <c>features.yaml</c>, <c>virtual_machines.yaml</c>, <c>past_history.yaml</c>, ...)
/// into the single top-level document the reconciler and schema validator expect. Merge
/// is by top-level key only — a key present in more than one file is a load error, never
/// silently overwritten. Shared by the runtime inventory loader and
/// <c>lodge validate</c> so merge semantics can never drift between the two.
/// </summary>
public static class InstanceYamlMerger
{
    private static readonly ISerializer Serializer = new SerializerBuilder().Build();

    public static (string? MergedYaml, string? Error) Merge(IReadOnlyList<(string Path, string Content)> files)
    {
        var merged = new Dictionary<object, object?>();

        foreach (var (path, content) in files)
        {
            var node = YamlFlattener.Parse(content);
            if (node is null)
            {
                continue; // blank file contributes nothing
            }
            if (node is not IDictionary<object, object> dict)
            {
                return (null, $"{path}: must be a YAML mapping at the top level.");
            }

            foreach (var (key, value) in dict)
            {
                if (!merged.TryAdd(key, value))
                {
                    return (null, $"{path}: top-level key '{key}' collides with the same key already provided by another file for this instance.");
                }
            }
        }

        return (Serializer.Serialize(merged), null);
    }
}
