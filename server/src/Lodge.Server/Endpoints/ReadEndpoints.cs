using System.Text.Json;
using Lodge.Core.Abstractions;
using Lodge.Core.Catalog;
using Lodge.Core.Diff;
using Lodge.Core.Reconciliation;
using Lodge.Core.Domain.Entities;
using Lodge.Core.Domain.Enums;
using Lodge.Infrastructure.Execution;
using Lodge.Infrastructure.Persistence;
using Lodge.Infrastructure.Reconciliation;
using Lodge.Server.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lodge.Server.Endpoints;

/// <summary>Kind-scoped read surface. Every route is namespaced by kind code.</summary>
public static class ReadEndpoints
{
    public static IEndpointRouteBuilder MapReadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1").RequireAuthorization();

        group.MapGet("/kinds", async (LodgeDbContext db, CancellationToken ct) =>
        {
            // Disabled kinds (folder gone from the inventory) are hidden, not deleted.
            var kinds = await db.Kinds
                .Where(p => p.Enabled)
                .OrderBy(p => p.Code)
                .Select(p => new KindDto(p.Code, p.Name, p.Enabled))
                .ToListAsync(ct);
            return Results.Ok(kinds);
        });

        group.MapGet("/kinds/{kindCode}/capabilities", async (
            string kindCode, ICapabilityCatalogProvider catalogs, CancellationToken ct) =>
        {
            var result = await catalogs.GetGenericCatalogAsync(kindCode, ct);
            var dto = new CapabilityCatalogDto(
                result.Catalog.KindCode,
                result.Catalog.Capabilities.Select(c => new CapabilityDefinitionDto(
                    c.Code,
                    c.Title,
                    c.Description,
                    c.IsView,
                    c.Signals.Select(s => new SignalDefinitionDto(
                        s.Path,
                        s.Kind.ToString(),
                        s.Label,
                        s.Rules.Select(r => new SignalRuleDto(
                            r.Trigger.ToString(),
                            r.WhenJson,
                            r.ItemKey,
                            r.Actions.Select(a => new ActionTemplateDto(
                                a.Key,
                                a.Label,
                                a.Policy.ToString(),
                                a.Requires,
                                a.ExecutorKind.ToString().ToLowerInvariant(),
                                a.Inputs.Select(i => new RuleInputDto(
                                    i.Name, i.Kind.ToString(), i.Value, i.Required)).ToList(),
                                a.DependsOn)).ToList())).ToList())).ToList())).ToList());

            return Results.Ok(dto);
        });

        group.MapGet("/kinds/{kindCode}/instances", async (
            string kindCode, LodgeDbContext db, CancellationToken ct) =>
        {
            var instances = await db.Instances
                .Where(t => t.KindCode == kindCode && t.Enabled)
                .OrderBy(t => t.InstanceCode)
                .Select(t => new InstanceDto(
                    t.Id, t.KindCode, t.InstanceCode, t.DisplayName, t.Generation, t.Region, t.CreatedAt,
                    db.RegistryRevisions.Where(r => r.InstanceId == t.Id)
                        .OrderByDescending(r => r.CreatedAt)
                        .Select(r => (DateTimeOffset?)r.CreatedAt)
                        .FirstOrDefault()))
                .ToListAsync(ct);
            return Results.Ok(instances);
        });

        group.MapGet("/kinds/{kindCode}/instances/{instanceId}", async (
            string kindCode, string instanceId, LodgeDbContext db, CancellationToken ct) =>
        {
            var instance = await ResolveInstanceAsync(db, kindCode, instanceId, ct);
            if (instance is null)
            {
                return Results.NotFound();
            }

            var latestRevision = await db.RegistryRevisions
                .Where(r => r.InstanceId == instance.Id)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(ct);

            var dto = new InstanceDetailDto(
                new InstanceDto(
                    instance.Id, instance.KindCode, instance.InstanceCode, instance.DisplayName,
                    instance.Generation, instance.Region, instance.CreatedAt, latestRevision?.CreatedAt),
                latestRevision?.YamlContent);

            return Results.Ok(dto);
        });

        group.MapGet("/kinds/{kindCode}/instances/{instanceId}/actions", async (
            string kindCode, string instanceId, LodgeDbContext db, ActionExecutionService execution, CancellationToken ct) =>
        {
            var instance = await ResolveInstanceAsync(db, kindCode, instanceId, ct);
            if (instance is null)
            {
                return Results.NotFound();
            }

            await execution.RefreshRunningAsync(instance.Id, kindCode, ct);

            var actions = await db.Actions
                .Where(a => a.InstanceId == instance.Id)
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync(ct);

            var dtos = actions.Select(a => new ActionDto(
                a.Id,
                a.CapabilityCode,
                a.SignalPath,
                a.ItemKey,
                a.ActionKey,
                a.Requires,
                a.Trigger.ToString(),
                a.Label,
                a.Policy.ToString(),
                a.Status.ToString(),
                a.Synthetic,
                DeserializePrompts(a.PendingPromptsJson),
                a.CreatedAt,
                a.CompletedAt,
                a.ExecutionRef,
                a.InvalidatedAt,
                DeserializeDependsOn(a.DependsOnJson))).ToList();

            return Results.Ok(dtos);
        });

        // Tails the action's latest run: the UI polls with the returned next_offset while
        // `running` is true. A retried action shows its newest run only.
        group.MapGet("/kinds/{kindCode}/instances/{instanceId}/actions/{actionId:guid}/log", async (
            string kindCode, string instanceId, Guid actionId, long? offset,
            LodgeDbContext db, IRunbookExecutor executor, IRunbookLogReader logs, CancellationToken ct) =>
        {
            const int maxChunkBytes = 256 * 1024;

            var instance = await ResolveInstanceAsync(db, kindCode, instanceId, ct);
            var action = instance is null
                ? null
                : await db.Actions.FirstOrDefaultAsync(a => a.Id == actionId && a.InstanceId == instance.Id, ct);
            if (action is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(action.ExecutionRef))
            {
                return Results.Ok(new ActionLogDto(action.Id, null, action.Status.ToString(), false, false,
                    "This action has not run yet.", string.Empty, 0));
            }

            var status = await executor.GetStatusAsync(action.ExecutionRef, ct);
            var chunk = await logs.ReadLogAsync(action.ExecutionRef, offset ?? 0, maxChunkBytes, ct);
            return Results.Ok(new ActionLogDto(
                action.Id,
                action.ExecutionRef,
                action.Status.ToString(),
                chunk is not null,
                status.State is RunbookRunState.Queued or RunbookRunState.Running,
                status.Message,
                chunk?.Text ?? string.Empty,
                chunk?.NextOffset ?? 0));
        });

        // View capabilities (no rules) rendered against the instance's latest inventory.
        group.MapGet("/kinds/{kindCode}/instances/{instanceId}/views", async (
            string kindCode, string instanceId, LodgeDbContext db, ICapabilityCatalogProvider catalogs, CancellationToken ct) =>
        {
            var instance = await ResolveInstanceAsync(db, kindCode, instanceId, ct);
            if (instance is null)
            {
                return Results.NotFound();
            }

            var yaml = await db.RegistryRevisions
                .Where(r => r.InstanceId == instance.Id)
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => r.YamlContent)
                .FirstOrDefaultAsync(ct);
            var catalog = await catalogs.GetCatalogAsync(kindCode, instance.InstanceCode, ct);
            var root = YamlFlattener.Parse(yaml);

            var views = catalog.Catalog.Capabilities
                .Where(c => c.IsView)
                .Select(c => ViewEvaluator.Evaluate(c, root))
                .ToList();
            return Results.Ok(views);
        });

        group.MapGet("/kinds/{kindCode}/instances/{instanceId}/events", async (
            string kindCode, string instanceId, LodgeDbContext db, CancellationToken ct) =>
        {
            var instance = await ResolveInstanceAsync(db, kindCode, instanceId, ct);
            if (instance is null)
            {
                return Results.NotFound();
            }

            var events = await db.AuditEvents
                .Where(a => a.InstanceId == instance.Id)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => new AuditEventDto(a.Id, a.EventType, a.Actor, a.PayloadJson, a.CreatedAt))
                .ToListAsync(ct);

            return Results.Ok(events);
        });

        // Cross-instance read surface: every action / audit event across every kind and
        // instance, for the global Drift/Actions/Audit pages. Optional filters are
        // AND-combined; omitting a filter applies no constraint on that dimension.
        group.MapGet("/actions", async (
            [FromQuery(Name = "status")] ActionStatus? status,
            [FromQuery(Name = "policy")] ActionPolicy? policy,
            [FromQuery(Name = "kind_code")] string? kindCode,
            [FromQuery(Name = "instance_id")] Guid? instanceId,
            LodgeDbContext db, CancellationToken ct) =>
        {
            var query =
                from a in db.Actions
                join i in db.Instances on a.InstanceId equals i.Id
                where status == null || a.Status == status
                where policy == null || a.Policy == policy
                where kindCode == null || i.KindCode == kindCode
                where instanceId == null || i.Id == instanceId
                orderby a.CreatedAt descending
                select new { Action = a, Instance = i };

            var rows = await query.ToListAsync(ct);

            var dtos = rows.Select(r => new GlobalActionDto(
                r.Action.Id,
                r.Action.CapabilityCode,
                r.Action.SignalPath,
                r.Action.ItemKey,
                r.Action.ActionKey,
                r.Action.Requires,
                r.Action.Trigger.ToString(),
                r.Action.Label,
                r.Action.Policy.ToString(),
                r.Action.Status.ToString(),
                r.Action.Synthetic,
                DeserializePrompts(r.Action.PendingPromptsJson),
                r.Action.CreatedAt,
                r.Action.CompletedAt,
                r.Instance.KindCode,
                r.Instance.Id,
                r.Instance.InstanceCode,
                r.Instance.DisplayName,
                r.Action.ExecutionRef,
                // Last touched when its run started, for a RUNNING row: the start time.
                r.Action.UpdatedAt)).ToList();

            return Results.Ok(dtos);
        });

        group.MapGet("/events", async (
            [FromQuery(Name = "kind_code")] string? kindCode,
            [FromQuery(Name = "instance_id")] Guid? instanceId,
            LodgeDbContext db, CancellationToken ct) =>
        {
            var query =
                from e in db.AuditEvents
                join i in db.Instances on e.InstanceId equals i.Id into instanceJoin
                from i in instanceJoin.DefaultIfEmpty()
                where kindCode == null || e.KindCode == kindCode
                where instanceId == null || e.InstanceId == instanceId
                orderby e.CreatedAt descending
                select new { Event = e, Instance = i };

            var rows = await query.ToListAsync(ct);

            var dtos = rows.Select(r => new GlobalAuditEventDto(
                r.Event.Id,
                r.Event.KindCode,
                r.Event.EventType,
                r.Event.Actor,
                r.Event.PayloadJson,
                r.Event.CreatedAt,
                r.Event.InstanceId,
                r.Instance?.InstanceCode,
                r.Instance?.DisplayName)).ToList();

            return Results.Ok(dtos);
        });

        return app;
    }

    private static IReadOnlyList<ActionIdentityDto> DeserializeDependsOn(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<ActionIdentityDto>();
        }
        try
        {
            return (JsonSerializer.Deserialize<List<ActionIdentity>>(json) ?? new List<ActionIdentity>())
                .Select(d => new ActionIdentityDto(d.SignalPath, d.ItemKey, d.ActionKey))
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<ActionIdentityDto>();
        }
    }

    private static IReadOnlyList<PendingPromptDto> DeserializePrompts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<PendingPromptDto>();
        }
        try
        {
            var prompts = JsonSerializer.Deserialize<List<PendingPromptDto>>(json);
            return prompts ?? (IReadOnlyList<PendingPromptDto>)Array.Empty<PendingPromptDto>();
        }
        catch (JsonException)
        {
            return Array.Empty<PendingPromptDto>();
        }
    }

    private static async Task<Instance?> ResolveInstanceAsync(
        LodgeDbContext db, string kindCode, string instanceId, CancellationToken ct)
    {
        if (Guid.TryParse(instanceId, out var id))
        {
            return await db.Instances.FirstOrDefaultAsync(
                t => t.KindCode == kindCode && t.Id == id, ct);
        }

        return await db.Instances.FirstOrDefaultAsync(
            t => t.KindCode == kindCode && t.InstanceCode == instanceId, ct);
    }
}
