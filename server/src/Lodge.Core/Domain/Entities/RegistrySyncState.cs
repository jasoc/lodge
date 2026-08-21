namespace Lodge.Core.Domain.Entities;

/// <summary>
/// Tracks the last commit SHA Lodge has already ingested for a kind's inventory
/// branch. The polling service compares the branch head against this to detect and
/// diff only what changed. Lodge reads Git; it never writes it.
/// </summary>
public class RegistrySyncState
{
    public Guid Id { get; set; }

    public string KindCode { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    /// <summary>Last commit SHA processed; null before the first baseline.</summary>
    public string? LastSeenSha { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
