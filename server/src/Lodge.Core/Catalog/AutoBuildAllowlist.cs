namespace Lodge.Core.Catalog;

/// <summary>
/// Which <c>container.build</c> playbooks an <c>AUTO</c> action may use. An AUTO action runs
/// with no human in front of it, and a build runs whatever the playbook's Dockerfile says
/// (<c>RUN</c> steps included) on the docker host — so anyone who can merge to the inventory
/// could get arbitrary code executed unattended. The rule: an AUTO action with a <c>build</c>
/// is only allowed when the <b>operator</b> has allowlisted that playbook, in the server's
/// configuration (<c>DockerExecutor__AutoBuildAllowlist__0=&lt;kind&gt;/&lt;context&gt;</c>),
/// never in the inventory itself. Anything else is treated as <c>MANUAL_REQUIRED</c> (a human
/// confirms the exact fingerprint) and reported as a validation error.
///
/// An entry is <c>kind/context</c> (the playbook folder as the action names it, e.g.
/// <c>homelab/playbooks/ansible</c>) — which trusts whatever is in that folder, so guard the
/// folder with review (CODEOWNERS) — or <c>kind/context@fingerprint</c>, which pins one
/// content: the first 12 or more hex characters of the playbook's fingerprint (shown in the
/// validation error), so an edit to the folder stops being AUTO until the entry is bumped.
/// </summary>
public sealed class AutoBuildAllowlist
{
    /// <summary>Nothing is allowed: the default.</summary>
    public static readonly AutoBuildAllowlist None = new(Array.Empty<string>());

    private readonly HashSet<string> _folders = new(StringComparer.Ordinal);
    private readonly List<(string Folder, string FingerprintPrefix)> _pinned = new();

    public AutoBuildAllowlist(IEnumerable<string> entries)
    {
        foreach (var raw in entries)
        {
            var entry = raw.Trim().Trim('/');
            if (entry.Length == 0)
            {
                continue;
            }

            var at = entry.IndexOf('@');
            if (at < 0)
            {
                _folders.Add(Normalize(entry));
            }
            else
            {
                _pinned.Add((Normalize(entry[..at]), entry[(at + 1)..].ToLowerInvariant()));
            }
        }
    }

    /// <summary>Whether an AUTO action may build <paramref name="build"/> of <paramref name="kindCode"/>.</summary>
    public bool Allows(string kindCode, ContainerBuildConfig build)
    {
        var folder = Normalize($"{kindCode}/{build.Context}");
        if (_folders.Contains(folder))
        {
            return true;
        }

        return build.Fingerprint is { } fingerprint &&
               _pinned.Any(p => p.Folder == folder && p.FingerprintPrefix.Length >= 12 &&
                                fingerprint.StartsWith(p.FingerprintPrefix, StringComparison.Ordinal));
    }

    /// <summary>The error reported for an AUTO build that isn't allowlisted, naming what to add.</summary>
    public static string NotAllowedMessage(string kindCode, ContainerBuildConfig build)
        => $"is AUTO with a container build ('{build.Context}'), which runs the Dockerfile unattended; it is treated as MANUAL_REQUIRED " +
           $"unless the server allowlists it: DockerExecutor__AutoBuildAllowlist__<n>={kindCode}/{build.Context} " +
           $"(or pin this content: {kindCode}/{build.Context}@{(build.Fingerprint is { Length: >= 12 } f ? f[..12] : "<fingerprint>")}).";

    private static string Normalize(string folder)
        => string.Join('/', folder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != "."));
}
