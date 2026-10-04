namespace Lodge.Core.Catalog;

/// <summary>How a signal's value is shaped in instance inventory.</summary>
public enum SignalKind
{
    /// <summary>A single leaf value (bool/string/number) at the signal path.</summary>
    Scalar,

    /// <summary>A map of stable item keys to item bodies (e.g. <c>virtual_machines.vm-alpha-01: {...}</c>).</summary>
    KeyedCollection,

    /// <summary>A list of scalars where each value is its own identity key (e.g. quirk tags). MODIFY is meaningless here.</summary>
    ScalarList
}

/// <summary>
/// One observed point in a instance's inventory: a dotted path plus the rules that map
/// its current value (or its items' presence/shape) to required actions.
/// </summary>
public sealed class SignalDefinition
{
    /// <summary>
    /// Dotted path into the instance YAML, e.g. <c>features.sso_login</c> or
    /// <c>virtual_machines</c>. A keyed collection may contain one <c>*</c> segment to
    /// address a collection nested inside every item of a parent collection, e.g.
    /// <c>proxmox.virtual_machines.*.containers</c>: its items are keyed
    /// <c>{parent key}/{child key}</c> (see <see cref="NestedItemKeySeparator"/>).
    /// </summary>
    public string Path { get; set; } = string.Empty;

    public SignalKind Kind { get; set; } = SignalKind.Scalar;

    /// <summary>
    /// Keyed collections only: item fields left out of every item body — the snapshot a
    /// MODIFY is detected against, <c>from: item</c> and <c>from: collection</c>. Used to
    /// carve a nested collection out of its parent (a VM's <c>containers</c>), so editing
    /// a container is a change to that container, not to the VM.
    /// </summary>
    public IReadOnlyList<string> Exclude { get; set; } = new List<string>();

    /// <summary>
    /// Scalar lists only: a folder of the kind's inventory (relative to
    /// <c>inventory/{kind}/</c>) whose files the list's entries name — paths or globs
    /// (<c>*</c>, <c>**</c>) relative to it. Each matching file becomes one item, keyed by its
    /// path, whose body is <c>{file, sha256, content}</c>: editing the file is a MODIFY of
    /// that item, and its content travels with the action (<c>from: item.content</c>).
    /// </summary>
    public string? Files { get; set; }

    /// <summary>
    /// The files under <see cref="Files"/> (relative path → content), read by the catalog
    /// provider at load time — the pure reconciler never touches the disk. Null until stamped.
    /// </summary>
    public IReadOnlyDictionary<string, string>? FileIndex { get; set; }

    public IReadOnlyList<SignalRule> Rules { get; set; } = new List<SignalRule>();

    /// <summary>Separates the parent and child key in a nested collection's item keys.</summary>
    public const char NestedItemKeySeparator = '/';

    /// <summary>True for a <c>parent.*.child</c> path.</summary>
    public bool IsNested => WildcardIndex(Path) >= 0;

    /// <summary>The parent collection's path (before <c>.*</c>); null when not nested.</summary>
    public string? ParentPath => IsNested ? Path[..(WildcardIndex(Path) - 1)] : null;

    /// <summary>The child collection's path inside each parent item (after <c>*.</c>); null when not nested.</summary>
    public string? ChildPath => IsNested ? Path[(WildcardIndex(Path) + 2)..] : null;

    private static int WildcardIndex(string path)
    {
        var segments = path.Split('.');
        var offset = 0;
        foreach (var segment in segments)
        {
            if (segment == "*")
            {
                return offset;
            }
            offset += segment.Length + 1;
        }
        return -1;
    }
}
