namespace Lodge.Core.Abstractions;

/// <summary>
/// The single, abstracted path to credentials. FortiPAM-ready: the POC ships a
/// mock implementation; a <c>FortiPamSecretProvider</c> is a future drop-in with
/// no domain logic changes. Lodge never embeds secrets in inventory or code.
/// </summary>
public interface ISecretProvider
{
    /// <summary>Resolve a static secret by opaque reference.</summary>
    Task<string> GetSecretAsync(string secretRef, CancellationToken cancellationToken = default);

    /// <summary>Issue a short-lived credential for a target and purpose.</summary>
    Task<EphemeralCredential> GetEphemeralCredentialAsync(
        string target, TimeSpan ttl, string purpose, CancellationToken cancellationToken = default);
}
