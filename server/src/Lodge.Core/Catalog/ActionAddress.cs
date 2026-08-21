namespace Lodge.Core.Catalog;

/// <summary>
/// A parsed reference to one action identity, in the addressing syntax shared by
/// <c>past_history</c> entries and <c>depends_on</c> declarations: <c>signal_path.action_key</c>
/// for a scalar signal, <c>signal_path[item_key].action_key</c> for one item of a collection
/// signal, <c>signal_path[item_key].*</c> to address every action key of that one known item,
/// or the bare <c>"*"</c> universal instance-wide marker. Item-key wildcards (<c>[*]</c>) are
/// never valid — a specific item must always be named.
/// </summary>
public readonly record struct ActionAddress(string? SignalPath, string? ItemKey, string ActionKey, bool IsUniversal)
{
    public static readonly ActionAddress Universal = new(null, null, "*", true);

    /// <summary>True for <c>item[key].*</c> — matches every action key of that one item.</summary>
    public bool IsActionWildcard => !IsUniversal && ActionKey == "*";
}

/// <summary>Parses the shared address syntax against a catalog's known signal paths.</summary>
public static class ActionAddressParser
{
    public static bool TryParse(string? raw, CapabilityCatalog catalog, out ActionAddress address, out string? error)
    {
        address = default;
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "address is empty.";
            return false;
        }

        if (text == "*")
        {
            address = ActionAddress.Universal;
            error = null;
            return true;
        }

        string signalPath;
        string? itemKey = null;
        string remainder;

        var bracketStart = text.IndexOf('[');
        if (bracketStart >= 0)
        {
            var bracketEnd = text.IndexOf(']', bracketStart);
            if (bracketEnd < 0)
            {
                error = $"'{text}': unmatched '[' — expected signal_path[item_key].action_key.";
                return false;
            }

            var candidateKey = text[(bracketStart + 1)..bracketEnd];
            if (candidateKey == "*")
            {
                error = $"'{text}': item-key wildcards are not supported — name the specific item, e.g. signal_path[known-key].*.";
                return false;
            }
            if (string.IsNullOrEmpty(candidateKey))
            {
                error = $"'{text}': empty item key in brackets.";
                return false;
            }

            var afterBracket = text[(bracketEnd + 1)..];
            if (afterBracket.Length < 2 || afterBracket[0] != '.')
            {
                error = $"'{text}': expected '.action_key' after 'signal_path[item_key]'.";
                return false;
            }

            var candidateSignalPath = text[..bracketStart];
            if (!IsKnownSignalPath(candidateSignalPath, catalog))
            {
                error = $"'{text}': '{candidateSignalPath}' is not a known signal path.";
                return false;
            }

            signalPath = candidateSignalPath;
            itemKey = candidateKey;
            remainder = afterBracket[1..];
        }
        else
        {
            var resolved = ResolveLongestSignalPrefix(text, catalog);
            if (resolved is null)
            {
                error = $"'{text}': could not resolve a known signal path prefix.";
                return false;
            }

            (signalPath, remainder) = resolved.Value;
        }

        if (string.IsNullOrEmpty(remainder))
        {
            error = $"'{text}': missing action key after the signal path.";
            return false;
        }
        if (remainder.Contains('.'))
        {
            error = $"'{text}': unexpected extra segment after the action key ('{remainder}').";
            return false;
        }

        error = null;
        address = new ActionAddress(signalPath, itemKey, remainder, false);
        return true;
    }

    private static bool IsKnownSignalPath(string path, CapabilityCatalog catalog)
        => catalog.Capabilities.SelectMany(c => c.Signals).Any(s => string.Equals(s.Path, path, StringComparison.Ordinal));

    /// <summary>
    /// Resolves the "features.sso_login" vs "virtual_machines.provision_vm" ambiguity by
    /// matching the longest known catalog signal path that prefixes the address text.
    /// </summary>
    private static (string SignalPath, string Remainder)? ResolveLongestSignalPrefix(string text, CapabilityCatalog catalog)
    {
        var knownPaths = catalog.Capabilities
            .SelectMany(c => c.Signals)
            .Select(s => s.Path)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(p => p.Length);

        foreach (var path in knownPaths)
        {
            if (string.Equals(text, path, StringComparison.Ordinal))
            {
                return (path, string.Empty);
            }
            if (text.StartsWith(path + ".", StringComparison.Ordinal))
            {
                return (path, text[(path.Length + 1)..]);
            }
        }

        return null;
    }
}
