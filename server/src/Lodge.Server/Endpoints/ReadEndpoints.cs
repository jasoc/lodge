using System.Text.Json;
using Lodge.Core.Domain.Entities;
using Lodge.Infrastructure.Persistence;
using Lodge.Server.Contracts;
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
            var kinds = await db.Kinds
                .OrderBy(p => p.Code)
                .Select(p => new KindDto(p.Code, p.Name, p.Enabled))
                .ToListAsync(ct);
            return Results.Ok(kinds);
        });

        group.MapGet("/kinds/{kindCode}/instances", async (
            string kindCode, LodgeDbContext db, CancellationToken ct) =>
        {
            var instances = await db.Instances
                .Where(t => t.KindCode == kindCode)
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
            string kindCode, string instanceId, LodgeDbContext db, CancellationToken ct) =>
        {
            var instance = await ResolveInstanceAsync(db, kindCode, instanceId, ct);
            if (instance is null)
            {
                return Results.NotFound();
            }

            var actions = await db.Actions
                .Where(a => a.InstanceId == instance.Id)
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync(ct);

            var dtos = actions.Select(a => new ActionDto(
                a.Id,
                a.CapabilityCode,
                a.SignalPath,
                a.ItemKey,
                a.RunbookRef,
                a.Trigger.ToString(),
                a.Label,
                a.Policy.ToString(),
                a.Status.ToString(),
                a.Synthetic,
                DeserializePrompts(a.PendingPromptsJson),
                a.CreatedAt,
                a.CompletedAt)).ToList();

            return Results.Ok(dtos);
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

        return app;
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
