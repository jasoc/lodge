using Lodge.Core.Catalog;

namespace Lodge.Infrastructure.Reconciliation;

/// <summary>
/// The live rule catalog for one kind+instance, plus any files that failed to load.
/// Broken files are skipped (surfaced as validation errors on the cycle) so one bad
/// YAML never takes the whole catalog down.
/// </summary>
public sealed record CatalogLoadResult(CapabilityCatalog Catalog, IReadOnlyList<string> Errors);

/// <summary>
/// Provides the merged capability catalog (generic kind rules + per-instance override
/// rules) the reconciler evaluates. Re-read every cycle via <see cref="ICacheInvalidatable"/>
/// so policy edits in <c>mapping/</c> apply live, never frozen on historical rows.
/// </summary>
public interface ICapabilityCatalogProvider
{
    Task<CatalogLoadResult> GetCatalogAsync(string kindCode, string instanceCode, CancellationToken cancellationToken = default);

    /// <summary>The generic (no instance override) catalog for a kind — what the read-only
    /// capability browser shows, since it has no single instance in scope.</summary>
    Task<CatalogLoadResult> GetGenericCatalogAsync(string kindCode, CancellationToken cancellationToken = default);
}
