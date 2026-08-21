using System.Collections.Concurrent;

namespace Lodge.Infrastructure.Auth;

/// <summary>One in-flight OIDC authorization-code flow, keyed by its <c>state</c> value.
/// Exactly one of <see cref="UiRedirect"/>/<see cref="CliRedirect"/> is set, deciding how
/// <c>/api/v1/auth/oidc/callback</c> hands the minted token back to the caller.</summary>
public sealed record PendingOidcLogin(
    string CodeVerifier,
    string? UiRedirect,
    string? CliRedirect,
    DateTimeOffset CreatedAt);

/// <summary>
/// Holds PKCE state for OIDC logins between <c>/auth/oidc/login</c> (which redirects to
/// the IdP) and <c>/auth/oidc/callback</c> (which the IdP redirects back to). In-memory
/// is deliberate: Lodge runs as one process, one container (see <c>docs/AGENTS.md</c>),
/// and a login flow that outlives a single restart isn't a real scenario worth persisting
/// state for. Entries older than <see cref="Ttl"/> are swept on every insert.
/// </summary>
public sealed class PendingOidcLoginStore(TimeSpan? ttl = null)
{
    private readonly TimeSpan _ttl = ttl ?? TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, PendingOidcLogin> _pending = new();

    public void Add(string state, PendingOidcLogin login)
    {
        Sweep();
        _pending[state] = login;
    }

    public bool TryTake(string state, out PendingOidcLogin login)
    {
        if (_pending.TryRemove(state, out var found) && DateTimeOffset.UtcNow - found.CreatedAt <= _ttl)
        {
            login = found;
            return true;
        }
        login = default!;
        return false;
    }

    private void Sweep()
    {
        var cutoff = DateTimeOffset.UtcNow - _ttl;
        foreach (var (state, login) in _pending)
        {
            if (login.CreatedAt < cutoff)
            {
                _pending.TryRemove(state, out _);
            }
        }
    }
}
