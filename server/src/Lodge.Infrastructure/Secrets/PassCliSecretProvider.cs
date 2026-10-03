using System.Diagnostics;
using Lodge.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Lodge.Infrastructure.Secrets;

/// <summary>
/// Resolves secrets by shelling out to <c>pass-cli</c> (the Proton Pass CLI) on the Lodge
/// host — the counterpart to the homelab automation's own convention
/// (<c>ansible.sh</c>/<c>compose.sh</c>/<c>terraform.sh</c> all build a temp env file of
/// <c>pass://Homelab/environments/&lt;VAR&gt;</c> references and run <c>pass-cli run
/// --env-file &lt;file&gt; --no-masking -- &lt;command&gt;</c>). No verified single-secret
/// <c>pass-cli get &lt;ref&gt;</c> subcommand exists in the grounded evidence for this, so
/// this reuses that exact proven shape against a throwaway one-line env file instead —
/// double-check this against real <c>pass-cli --help</c> before relying on it in
/// production, this is the best-grounded shape available, not a verified API contract.
///
/// <see cref="GetSecretAsync"/>'s <c>secretRef</c> is the bare path (e.g.
/// <c>Homelab/environments/DB_PASSWORD</c>), not a <c>pass://</c> URI — this provider
/// prepends that itself, keeping capability YAML terse (<c>secret: "Homelab/environments/DB_PASSWORD"</c>).
///
/// Hard invariant: the resolved value never appears in an exception message, a log line,
/// or anywhere outside the returned string — only <c>secretRef</c> does. Preserve this on
/// every future edit.
/// </summary>
public sealed class PassCliSecretProvider : ISecretProvider
{
    private readonly PassCliOptions _options;
    private readonly IProcessRunner _runner;

    public PassCliSecretProvider(IOptions<PassCliOptions> options, IProcessRunner runner)
    {
        _options = options.Value;
        _runner = runner;
    }

    public async Task<string> GetSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        var tempEnvFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempEnvFile, $"LODGE_RESOLVED_SECRET=\"pass://{secretRef}\"\n", cancellationToken);

            var startInfo = new ProcessStartInfo { FileName = _options.BinaryPath };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--env-file");
            startInfo.ArgumentList.Add(tempEnvFile);
            startInfo.ArgumentList.Add("--no-masking");
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("sh");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("printf %s \"$LODGE_RESOLVED_SECRET\"");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var result = await _runner.RunAsync(startInfo, linked.Token);
            if (result.ExitCode != 0)
            {
                // Never include the resolved value here — only the ref. The error path
                // never has the value anyway (pass-cli failed before printing it), but the
                // invariant is stated explicitly so a future edit doesn't accidentally
                // start including result.Stdout in this message.
                throw new InvalidOperationException(
                    $"pass-cli failed to resolve secret '{secretRef}' (exit {result.ExitCode}).");
            }

            return result.Stdout.Trim();
        }
        finally
        {
            File.Delete(tempEnvFile);
        }
    }

    public Task<EphemeralCredential> GetEphemeralCredentialAsync(
        string target, TimeSpan ttl, string purpose, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "PassCliSecretProvider only resolves static secrets — ephemeral credentials need a real vault (FortiPAM, Vault, ...).");
}
