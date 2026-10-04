using System.Diagnostics;
using Lodge.Infrastructure.Secrets;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lodge.Tests;

/// <summary>
/// Exercises <see cref="PassCliSecretProvider"/> against a fake <see cref="IProcessRunner"/>
/// — a real <c>pass-cli</c> binary can't be assumed in CI, unlike the cheap-real-I/O
/// options used elsewhere (a real local HTTP listener for the webhook executor, real env
/// vars for <c>EnvSecretProvider</c>). This is a deliberate divergence, not an oversight.
/// </summary>
public class PassCliSecretProviderTests
{
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public ProcessStartInfo? LastStartInfo { get; private set; }
        public string? TempEnvFileContentAtCallTime { get; private set; }
        public int ExitCode { get; set; }
        public string Stdout { get; set; } = string.Empty;
        public string Stderr { get; set; } = string.Empty;

        public Task<ProcessRunResult> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken = default)
        {
            LastStartInfo = startInfo;
            var envFilePath = startInfo.ArgumentList[startInfo.ArgumentList.IndexOf("--env-file") + 1];
            TempEnvFileContentAtCallTime = File.ReadAllText(envFilePath);
            return Task.FromResult(new ProcessRunResult(ExitCode, Stdout, Stderr));
        }
    }

    [Fact]
    public async Task GetSecretAsync_writes_a_pass_uri_temp_env_file_and_returns_trimmed_stdout()
    {
        var runner = new FakeProcessRunner { ExitCode = 0, Stdout = "s3cr3t\n" };
        var provider = new PassCliSecretProvider(Options.Create(new PassCliOptions()), runner);

        var value = await provider.GetSecretAsync("Homelab/environments/DB_PASSWORD");

        Assert.Equal("s3cr3t", value);
        Assert.Equal(
            "LODGE_RESOLVED_SECRET=\"pass://Homelab/environments/DB_PASSWORD\"\n",
            runner.TempEnvFileContentAtCallTime);
    }

    [Fact]
    public async Task GetSecretAsync_builds_the_expected_pass_cli_argv()
    {
        var runner = new FakeProcessRunner { ExitCode = 0, Stdout = "x" };
        var provider = new PassCliSecretProvider(Options.Create(new PassCliOptions { BinaryPath = "pass-cli" }), runner);

        await provider.GetSecretAsync("Homelab/environments/TOKEN");

        Assert.Equal("pass-cli", runner.LastStartInfo!.FileName);
        Assert.Equal(
            new[] { "run", "--env-file", runner.LastStartInfo.ArgumentList[2], "--no-masking", "--", "sh", "-c", "printf %s \"$LODGE_RESOLVED_SECRET\"" },
            runner.LastStartInfo.ArgumentList);
    }

    [Fact]
    public async Task GetSecretAsync_throws_naming_only_the_ref_never_a_value_on_nonzero_exit()
    {
        var runner = new FakeProcessRunner { ExitCode = 1, Stdout = "should-never-appear", Stderr = "auth failed" };
        var provider = new PassCliSecretProvider(Options.Create(new PassCliOptions()), runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetSecretAsync("Homelab/environments/DB_PASSWORD"));

        Assert.Contains("Homelab/environments/DB_PASSWORD", ex.Message);
        Assert.DoesNotContain("should-never-appear", ex.Message);
    }

    [Fact]
    public async Task GetSecretAsync_deletes_the_temp_env_file_even_on_failure()
    {
        var runner = new FakeProcessRunner { ExitCode = 1 };
        var provider = new PassCliSecretProvider(Options.Create(new PassCliOptions()), runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetSecretAsync("some/ref"));

        var envFilePath = runner.LastStartInfo!.ArgumentList[runner.LastStartInfo.ArgumentList.IndexOf("--env-file") + 1];
        Assert.False(File.Exists(envFilePath));
    }
}
