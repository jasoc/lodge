using Lodge.Core.Domain.Entities;
using Lodge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lodge.Infrastructure.Auth;

/// <summary>
/// Users and their groups — what an action's <c>requires</c> is checked against. The
/// identity provider is the only source: <see cref="SyncFromIdentityProviderAsync"/> mirrors
/// a user and their group claim (names 1:1) at every OIDC login. Lodge never edits them.
/// A group has no row of its own: it exists while someone is in it.
/// </summary>
public sealed class UserDirectory
{
    private readonly LodgeDbContext _db;

    public UserDirectory(LodgeDbContext db) => _db = db;

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default)
        => await _db.Users.Include(u => u.Groups).OrderBy(u => u.Id).ToListAsync(cancellationToken);

    public async Task<User?> FindAsync(string id, CancellationToken cancellationToken = default)
        => await _db.Users.Include(u => u.Groups).FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    /// <summary>The current group names of a user; null when Lodge doesn't know the user.</summary>
    public async Task<IReadOnlyList<string>?> GroupsOfAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (!await _db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return null;
        }
        return await _db.UserGroups.Where(g => g.UserId == userId).Select(g => g.GroupName).OrderBy(g => g)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Upserts an OIDC user and replaces their memberships with the token's groups.</summary>
    public async Task<User> SyncFromIdentityProviderAsync(
        string subject, string displayName, IReadOnlyList<string> groups, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(subject, cancellationToken);
        if (user is null)
        {
            user = new User { Id = subject, CreatedAt = DateTimeOffset.UtcNow };
            _db.Users.Add(user);
        }

        user.DisplayName = displayName;
        user.Source = UserSources.Oidc;
        user.LastLoginAt = DateTimeOffset.UtcNow;
        SetGroups(user, groups);
        await _db.SaveChangesAsync(cancellationToken);
        return user;
    }

    private void SetGroups(User user, IReadOnlyList<string> groups)
    {
        var wanted = groups.Select(g => g.Trim()).Where(g => g.Length > 0).ToHashSet(StringComparer.Ordinal);
        foreach (var membership in user.Groups.Where(g => !wanted.Contains(g.GroupName)).ToList())
        {
            user.Groups.Remove(membership);
            _db.UserGroups.Remove(membership);
        }
        foreach (var name in wanted.Where(n => user.Groups.All(g => g.GroupName != n)))
        {
            user.Groups.Add(new UserGroup { UserId = user.Id, GroupName = name });
        }
    }
}
