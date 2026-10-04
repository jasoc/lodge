using System.Globalization;
using Lodge.Core.Catalog;
using Lodge.Core.Diff;

namespace Lodge.Core.Reconciliation;

/// <summary>One column of a view table: a per-item field, named by its signal's label.</summary>
public sealed record ViewColumn(string Label, string Field);

/// <summary>One item of a view table: its key and one display value per column (null when the item lacks it).</summary>
public sealed record ViewRow(string Key, IReadOnlyList<string?> Values);

/// <summary>
/// The items of one collection a view looks into (<c>proxmox.virtual_machines</c>), one row
/// per item sorted by key, one column per <c>collection.*.field</c> signal.
/// </summary>
public sealed record ViewTable(string Collection, IReadOnlyList<ViewColumn> Columns, IReadOnlyList<ViewRow> Rows);

/// <summary>A plain (non-<c>*</c>) view signal: one labelled value.</summary>
public sealed record ViewValue(string Label, string Path, string? Value);

/// <summary>What a view capability shows for one instance.</summary>
public sealed record CapabilityViewData(
    string Code,
    string Title,
    string Description,
    IReadOnlyList<ViewTable> Tables,
    IReadOnlyList<ViewValue> Values);

/// <summary>
/// Renders <see cref="CapabilityDefinition.IsView">view</see> capabilities straight from an
/// instance's inventory. Signals of the form <c>collection.*.field</c> sharing a collection
/// become one table; any other signal is a single labelled value. Pure, like the reconciler.
/// </summary>
public static class ViewEvaluator
{
    public static CapabilityViewData Evaluate(CapabilityDefinition view, object? desiredRoot)
    {
        var tables = new List<ViewTable>();
        var values = new List<ViewValue>();

        foreach (var group in view.Signals.Where(s => s.IsNested).GroupBy(s => s.ParentPath!))
        {
            var columns = group.Select(s => new ViewColumn(LabelOf(s), s.ChildPath!)).ToList();
            var rows = new List<ViewRow>();
            var (found, node) = Reconciler.ResolvePath(desiredRoot, group.Key);
            if (found && node is IDictionary<object, object> items)
            {
                foreach (var (rawKey, body) in items.OrderBy(i => KeyOf(i.Key), StringComparer.Ordinal))
                {
                    rows.Add(new ViewRow(
                        KeyOf(rawKey),
                        columns.Select(c => Display(Reconciler.ResolvePath(body, c.Field))).ToList()));
                }
            }
            tables.Add(new ViewTable(group.Key, columns, rows));
        }

        foreach (var signal in view.Signals.Where(s => !s.IsNested))
        {
            values.Add(new ViewValue(LabelOf(signal), signal.Path, Display(Reconciler.ResolvePath(desiredRoot, signal.Path))));
        }

        return new CapabilityViewData(view.Code, view.Title, view.Description, tables, values);
    }

    private static string LabelOf(SignalDefinition signal) => signal.Label ?? signal.Path.Split('.')[^1];

    private static string KeyOf(object key) => Convert.ToString(key, CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Scalars as plain text, a list of scalars comma-separated, anything else as canonical JSON.</summary>
    private static string? Display((bool Found, object? Node) resolved)
    {
        return !resolved.Found || resolved.Node is null ? null : Text(resolved.Node);

        static string Text(object node) => node switch
        {
            IList<object> list when list.All(i => i is not (IDictionary<object, object> or IList<object>))
                => string.Join(", ", list.Select(i => Convert.ToString(i, CultureInfo.InvariantCulture))),
            IDictionary<object, object> or IList<object> => YamlFlattener.ToCanonicalJson(node),
            _ => Convert.ToString(node, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }
}
