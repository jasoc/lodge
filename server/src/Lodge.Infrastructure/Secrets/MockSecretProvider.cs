using Lodge.Core.Abstractions;

namespace Lodge.Infrastructure.Secrets;

/// <summary>
/// POC-only secret provider. Returns deterministic placeholder values and never
/// touches a real vault. This is explicitly temporary: a
/// <c>FortiPamSecretProvider</c> replaces it with no domain changes. No real
/// secrets ever flow through the POC.
/// </summary>
public sealed class MockSecretProvider : ISecretProvider
{
    public Task<string> GetSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        // Intentionally NOT a real secret. Placeholder for POC wiring only.
        return Task.FromResult($"mock-secret::{secretRef}");
    }

    public Task<EphemeralCredential> GetEphemeralCredentialAsync(
        string target, TimeSpan ttl, string purpose, CancellationToken cancellationToken = default)
    {
        var cred = new EphemeralCredential(
            Target: target,
            Username: "mock-user",
            Secret: $"mock-ephemeral::{Guid.NewGuid():N}",
            Purpose: purpose,
            ExpiresAt: DateTimeOffset.UtcNow.Add(ttl));

        return Task.FromResult(cred);
    }
}
