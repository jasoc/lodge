namespace Lodge.Core.Abstractions;

/// <summary>
/// The single, abstracted path to credentials: an action's <c>secret:</c> inputs are resolved
/// through it, only when the action runs. Lodge never embeds secrets in inventory or code, and
/// never stores or logs a resolved value. A real vault is a drop-in implementation.
/// </summary>
public interface ISecretProvider
{
    /// <summary>Resolve a static secret by opaque reference.</summary>
    Task<string> GetSecretAsync(string secretRef, CancellationToken cancellationToken = default);
}
