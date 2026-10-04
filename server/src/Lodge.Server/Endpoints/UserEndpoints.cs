using Lodge.Core.Domain.Entities;
using Lodge.Infrastructure.Auth;
using Lodge.Infrastructure.Persistence;
using Lodge.Infrastructure.Reconciliation;
using Lodge.Server.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lodge.Server.Endpoints;

/// <summary>
/// Users and groups, read-only: they come from the identity provider (OIDC), mirrored at
/// each sign-in. Groups are also listed as soon as an action `requires` them.
/// </summary>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1").RequireAuthorization();

        group.MapGet("/users", async (UserDirectory users, CancellationToken ct) =>
            Results.Ok((await users.ListAsync(ct)).Select(ToDto).ToList()));

        group.MapGet("/groups", async (
            LodgeDbContext db, ICapabilityCatalogProvider catalogs, CancellationToken ct) =>
        {
            var members = (await db.UserGroups.ToListAsync(ct))
                .GroupBy(g => g.GroupName)
                .ToDictionary(g => g.Key, g => g.Select(m => m.UserId).Order().ToList(), StringComparer.Ordinal);

            var requiredBy = new Dictionary<string, List<GroupRequirementDto>>(StringComparer.Ordinal);
            foreach (var kind in await db.Kinds.Where(k => k.Enabled).Select(k => k.Code).ToListAsync(ct))
            {
                var catalog = await catalogs.GetGenericCatalogAsync(kind, ct);
                foreach (var capability in catalog.Catalog.Capabilities)
                {
                    var templates = capability.Signals.SelectMany(s => s.Rules).SelectMany(r => r.Actions)
                        .Where(a => a.Requires is not null)
                        .DistinctBy(a => a.Key);
                    foreach (var action in templates)
                    {
                        if (!requiredBy.TryGetValue(action.Requires!, out var list))
                        {
                            requiredBy[action.Requires!] = list = new List<GroupRequirementDto>();
                        }
                        list.Add(new GroupRequirementDto(kind, capability.Code, action.Key, action.Label));
                    }
                }
            }

            var groups = members.Keys.Union(requiredBy.Keys).Order(StringComparer.Ordinal)
                .Select(name => new GroupDto(
                    name,
                    members.GetValueOrDefault(name) ?? new List<string>(),
                    requiredBy.GetValueOrDefault(name) ?? new List<GroupRequirementDto>()))
                .ToList();
            return Results.Ok(groups);
        });

        return app;
    }

    private static UserDto ToDto(User user)
        => new(user.Id, user.DisplayName, user.Source,
            user.Groups.Select(g => g.GroupName).Order(StringComparer.Ordinal).ToList(),
            user.CreatedAt, user.LastLoginAt);
}
