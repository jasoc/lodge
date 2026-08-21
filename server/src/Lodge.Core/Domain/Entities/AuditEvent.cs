namespace Lodge.Core.Domain.Entities;

/// <summary>
/// An immutable audit record. Every non-trivial action is audited with timestamp,
/// actor, context, and outcome.
/// </summary>
public class AuditEvent
{
    public Guid Id { get; set; }

    public Guid? InstanceId { get; set; }

    public string KindCode { get; set; } = string.Empty;

    /// <summary>Event type, e.g. "registry.revision.stored", "action.generated", "action.adopted".</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Who or what triggered the event, e.g. "github-actions", "system".</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>JSON-encoded context/outcome payload.</summary>
    public string? PayloadJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Instance? Instance { get; set; }
}
