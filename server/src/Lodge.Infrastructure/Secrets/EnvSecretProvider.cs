using Lodge.Core.Abstractions;

namespace Lodge.Infrastructure.Secrets;

/// <summary>
/// Resolves secrets from environment variables on the server process — the OSS-friendly
/// default so the container/HTTP executors and the GitHub inventory source can pull real
/// credentials without a company-specific vault. Env vars are static: they don't rotate
/// or expire — a real vault is a drop-in <see cref="ISecretProvider"/>.
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
}
