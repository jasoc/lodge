using Lodge.Core.Abstractions;

namespace Lodge.Infrastructure.Secrets;

/// <summary>
/// Resolves secrets from environment variables on the server process — the OSS-friendly
/// default so the shell/webhook executors and the GitHub inventory source can pull real
/// credentials without a company-specific vault. Env vars are static: they can't actually
/// rotate or expire, so <see cref="GetEphemeralCredentialAsync"/> returns a real value
/// wrapped with the requested TTL as a label only, not a genuinely short-lived
/// credential — that needs a real vault (FortiPAM, Vault, ...) as a future drop-in.
/// </summary>
public sealed class EnvSecretProvider : ISecretProvider
{
    public Task<string> GetSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        var value = Environment.GetEnvironmentVariable(secretRef);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(
                $"Secret '{secretRef}' is not set. Export it as an environment variable on the server process.");
        }
        return Task.FromResult(value);
    }

    public Task<EphemeralCredential> GetEphemeralCredentialAsync(
        string target, TimeSpan ttl, string purpose, CancellationToken cancellationToken = default)
    {
        var secretKey = $"LODGE_SECRET_{Sanitize(target)}_{Sanitize(purpose)}";
        var secret = Environment.GetEnvironmentVariable(secretKey);
        if (string.IsNullOrEmpty(secret))
        {
            throw new InvalidOperationException(
                $"No credential configured for target '{target}' / purpose '{purpose}'. Export {secretKey}.");
        }

        var username = Environment.GetEnvironmentVariable($"{secretKey}_USERNAME") ?? "lodge";

        return Task.FromResult(new EphemeralCredential(
            Target: target,
            Username: username,
            Secret: secret,
            Purpose: purpose,
            ExpiresAt: DateTimeOffset.UtcNow.Add(ttl)));
    }

    private static string Sanitize(string value)
        => new(value.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
