using Lodge.Infrastructure.Secrets;
using Xunit;

namespace Lodge.Tests;

public class EnvSecretProviderTests
{
    [Fact]
    public async Task GetSecretAsync_returns_the_named_environment_variable()
    {
        var key = $"LODGE_TEST_SECRET_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(key, "s3cr3t");
        try
        {
            var provider = new EnvSecretProvider();
            Assert.Equal("s3cr3t", await provider.GetSecretAsync(key));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public async Task GetSecretAsync_throws_a_clear_error_when_unset()
    {
        var provider = new EnvSecretProvider();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetSecretAsync($"LODGE_TEST_UNSET_{Guid.NewGuid():N}"));
        Assert.Contains("not set", ex.Message);
    }

    [Fact]
    public async Task GetEphemeralCredentialAsync_reads_the_sanitized_target_purpose_key()
    {
        var key = "LODGE_SECRET_MY_TARGET_DEPLOY";
        Environment.SetEnvironmentVariable(key, "deploy-secret");
        try
        {
            var provider = new EnvSecretProvider();
            var cred = await provider.GetEphemeralCredentialAsync("my-target", TimeSpan.FromMinutes(5), "deploy");

            Assert.Equal("my-target", cred.Target);
            Assert.Equal("deploy-secret", cred.Secret);
            Assert.Equal("deploy", cred.Purpose);
            Assert.True(cred.ExpiresAt > DateTimeOffset.UtcNow);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }
}
