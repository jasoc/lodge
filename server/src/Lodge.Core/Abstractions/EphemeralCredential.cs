namespace Lodge.Core.Abstractions;

/// <summary>
/// A short-lived credential issued for a specific target and purpose.
/// </summary>
public sealed record EphemeralCredential(
    string Target,
    string Username,
    string Secret,
    string Purpose,
    DateTimeOffset ExpiresAt);
