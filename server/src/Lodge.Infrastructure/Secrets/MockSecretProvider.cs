using Lodge.Core.Abstractions;

namespace Lodge.Infrastructure.Secrets;

/// <summary>
/// Secret provider for tests and demos. Returns deterministic placeholder values and
/// never touches a real vault: no real secrets ever flow through it.
/// </summary>
public sealed class MockSecretProvider : ISecretProvider
{
    public Task<string> GetSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        // Intentionally NOT a real secret. Placeholder for POC wiring only.
        return Task.FromResult($"mock-secret::{secretRef}");
    }
}
