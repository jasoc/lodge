using System.Text.Json;
using Lodge.Core.Catalog;
using Lodge.Core.Domain.Entities;
using ActionEntity = Lodge.Core.Domain.Entities.Action;

namespace Lodge.Infrastructure.Persistence;

/// <summary>
/// The single way an audit event is written, plus the shared payload shapes. Payloads are
/// meant to be read by a human after the fact, so they carry the actual values involved
/// (signal values, resolved inputs, prompt answers, run outcome), not just ids. Secret
/// values never reach here: actions only hold secret <em>references</em>, and only their
/// input names are recorded.
/// </summary>
public static class AuditLog
{
    public static void AddAudit(
        this LodgeDbContext db, Guid? instanceId, string kindCode, string eventType, string actor, object payload)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            InstanceId = instanceId,
            KindCode = kindCode,
            EventType = eventType,
            Actor = actor,
            PayloadJson = JsonSerializer.Serialize(payload),
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// Everything that identifies and describes an action row at the moment of the event;
    /// callers add event-specific keys to the returned map.
    /// </summary>
    public static Dictionary<string, object?> ActionSnapshot(ActionEntity action)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actionId"] = action.Id,
            ["capability"] = action.CapabilityCode,
            ["label"] = action.Label,
            ["signalPath"] = action.SignalPath,
            ["itemKey"] = action.ItemKey,
            ["actionKey"] = action.ActionKey,
            ["trigger"] = action.Trigger.ToString(),
            ["policy"] = action.Policy.ToString(),
            ["status"] = action.Status.ToString(),
            ["desiredValue"] = ParseJson(action.DesiredValueJson),
            ["inputs"] = ParseJson(action.ResolvedInputsJson),
            ["executor"] = action.ExecutorKind.ToString(),
        };

        if (action.Requires is not null)
        {
            payload["requires"] = action.Requires;
        }
        if (action.Synthetic)
        {
            payload["synthetic"] = true;
        }
        if (action.ExecutionRef is not null)
        {
            payload["runId"] = action.ExecutionRef;
        }
        if (SecretInputNames(action.SecretInputsJson) is { Count: > 0 } secretNames)
        {
            payload["secretInputs"] = secretNames;
        }
        if (ParseJson(action.DependsOnJson) is { } dependsOn)
        {
            payload["dependsOn"] = dependsOn;
        }

        return payload;
    }

    /// <summary>
    /// A stored canonical-JSON string as a JSON value, so the payload nests it instead of
    /// double-encoding it as a string. Unparseable input is kept verbatim.
    /// </summary>
    public static object? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static List<string> SecretInputNames(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<string>();
        }
        try
        {
            return (JsonSerializer.Deserialize<List<SecretInputRef>>(json) ?? new List<SecretInputRef>())
                .Select(s => s.Name)
                .ToList();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }
}
