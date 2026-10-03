namespace Lodge.Infrastructure.Secrets;

/// <summary>Configuration for <see cref="PassCliSecretProvider"/>.</summary>
public sealed class PassCliOptions
{
    public const string SectionName = "PassCli";

    /// <summary>Path to the pass-cli (Proton Pass CLI) binary.</summary>
    public string BinaryPath { get; set; } = "pass-cli";

    public int TimeoutSeconds { get; set; } = 30;
}
