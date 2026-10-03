using System.Runtime.Versioning;
using Lodge.Core.Catalog;
using Lodge.Infrastructure.Execution;
using Lodge.Infrastructure.Git;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lodge.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class PlaybookContextResolverTests : IDisposable
{
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("lodge-playbook-test-").FullName;
    private readonly PlaybookContextResolver _resolver;
    private readonly DockerBuildConfig _build = new("playbooks/probe");

    public PlaybookContextResolverTests()
    {
        _resolver = new PlaybookContextResolver(Options.Create(new GitSnapshotOptions { RepoRoot = _repoRoot }));
        Write("Dockerfile", "FROM alpine:3.20\n");
        Write("bin/run.sh", "echo hi\n");
    }

    public void Dispose() => Directory.Delete(_repoRoot, recursive: true);

    private string PlaybookDir => Path.Combine(_repoRoot, "inventory", "homelab", "playbooks", "probe");

    private void Write(string relative, string content)
    {
        var path = Path.Combine(PlaybookDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Fingerprint_is_stable_for_unchanged_content()
        => Assert.Equal(_resolver.ComputeFingerprint("homelab", _build), _resolver.ComputeFingerprint("homelab", _build));

    [Fact]
    public void Fingerprint_changes_with_file_content()
    {
        var before = _resolver.ComputeFingerprint("homelab", _build);
        Write("bin/run.sh", "echo bye\n");
        Assert.NotEqual(before, _resolver.ComputeFingerprint("homelab", _build));
    }

    [Fact]
    public void Fingerprint_changes_with_the_executable_bit()
    {
        var before = _resolver.ComputeFingerprint("homelab", _build);
        File.SetUnixFileMode(Path.Combine(PlaybookDir, "bin/run.sh"),
            File.GetUnixFileMode(Path.Combine(PlaybookDir, "bin/run.sh")) | UnixFileMode.UserExecute);
        Assert.NotEqual(before, _resolver.ComputeFingerprint("homelab", _build));
    }

    [Fact]
    public void Fingerprint_changes_with_build_args_and_target()
    {
        var plain = _resolver.ComputeFingerprint("homelab", _build);
        Assert.NotEqual(plain, _resolver.ComputeFingerprint("homelab",
            _build with { Args = new Dictionary<string, string> { ["V"] = "1" } }));
        Assert.NotEqual(plain, _resolver.ComputeFingerprint("homelab", _build with { Target = "runtime" }));
    }

    [Fact]
    public void Missing_context_or_dockerfile_is_reported()
    {
        Assert.Throws<PlaybookContextException>(() => _resolver.ComputeFingerprint("homelab", new DockerBuildConfig("playbooks/nope")));
        var ex = Assert.Throws<PlaybookContextException>(
            () => _resolver.ComputeFingerprint("homelab", _build with { Dockerfile = "Other.Dockerfile" }));
        Assert.Contains("Other.Dockerfile", ex.Message);
    }

    [Fact]
    public void A_symlinked_context_escaping_the_kind_folder_is_rejected()
    {
        var outside = Directory.CreateTempSubdirectory("lodge-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "Dockerfile"), "FROM alpine\n");
            Directory.CreateSymbolicLink(Path.Combine(_repoRoot, "inventory", "homelab", "playbooks", "evil"), outside);
            Assert.Throws<PlaybookContextException>(
                () => _resolver.ComputeFingerprint("homelab", new DockerBuildConfig("playbooks/evil")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
