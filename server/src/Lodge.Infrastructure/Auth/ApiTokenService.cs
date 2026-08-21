using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lodge.Core.Abstractions;
using Lodge.Core.Domain.Entities;
using Lodge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lodge.Infrastructure.Auth;

/// <summary>
/// Issues and validates the bearer tokens both auth planes rely on. A personal token
/// (minted via `lodge login`, auto-approved in the no-auth profile or OIDC-derived
/// otherwise) and a service token (scoped automation credential) are validated exactly
/// the same way — only how a token gets issued differs between the two. Only a SHA-256
/// hash of the raw token is ever persisted; the raw value is returned to the caller once,
/// at issuance, and cannot be recovered afterward.
/// </summary>
public sealed class ApiTokenService
{
    private readonly LodgeDbContext _db;

    public ApiTokenService(LodgeDbContext db) => _db = db;

    public async Task<(string RawToken, ApiToken Record)> IssueAsync(
        ApiTokenKind kind,
        string subjectId,
        string displayName,
        IReadOnlyList<string> groups,
        bool isAdmin,
        IReadOnlyList<string>? scopes = null,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        var raw = GenerateRawToken(kind);
        var record = new ApiToken
        {
            Id = Guid.NewGuid(),
            TokenHash = Hash(raw),
            Kind = kind,
            DisplayName = displayName,
            SubjectId = subjectId,
            GroupsJson = JsonSerializer.Serialize(groups),
            IsAdmin = isAdmin,
            ScopesJson = JsonSerializer.Serialize(scopes ?? Array.Empty<string>()),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = ttl is null ? null : DateTimeOffset.UtcNow.Add(ttl.Value)
        };

        _db.ApiTokens.Add(record);
        await _db.SaveChangesAsync(cancellationToken);
        return (raw, record);
    }

    /// <summary>Resolves a raw bearer token to the identity it authenticates as, or null
    /// if it's unknown, revoked, or expired. Touches <c>last_used_at</c> on success.</summary>
    public async Task<AuthenticatedUser?> ValidateAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        var hash = Hash(rawToken);
        var record = await _db.ApiTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (record is null || record.RevokedAt is not null)
        {
            return null;
        }
        if (record.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        record.LastUsedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        var groups = JsonSerializer.Deserialize<string[]>(record.GroupsJson) ?? Array.Empty<string>();
        var scopes = JsonSerializer.Deserialize<string[]>(record.ScopesJson) ?? Array.Empty<string>();
        return new AuthenticatedUser(
            record.SubjectId, record.DisplayName, groups, record.IsAdmin,
            Scopes: scopes, IsServiceToken: record.Kind == ApiTokenKind.Service);
    }

    /// <summary>All tokens (personal and service), most recently created first — never
    /// includes the raw value, only what was persisted at issuance.</summary>
    public async Task<IReadOnlyList<ApiToken>> ListAsync(CancellationToken cancellationToken = default)
        => await _db.ApiTokens.OrderByDescending(t => t.CreatedAt).ToListAsync(cancellationToken);

    /// <summary>Marks a token revoked; idempotent. Returns false if no such token exists.</summary>
    public async Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var record = await _db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (record is null)
        {
            return false;
        }
        record.RevokedAt ??= DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string GenerateRawToken(ApiTokenKind kind)
    {
        var prefix = kind == ApiTokenKind.Service ? "lodge_svc_" : "lodge_pat_";
        return prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    private static string Hash(string rawToken)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();
}
