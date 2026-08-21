namespace Lodge.Core.Catalog;

/// <summary>
/// A capability YAML file is malformed or violates a catalog invariant. Raised by the
/// loader; the reconciliation cycle records it as a validation error instead of crashing.
/// </summary>
public sealed class CatalogFormatException : Exception
{
    public CatalogFormatException(string message) : base(message)
    {
    }

    public CatalogFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
