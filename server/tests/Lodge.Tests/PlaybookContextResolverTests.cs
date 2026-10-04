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
    private readonly ContainerBuildConfig _build = new("playbooks/probe");

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
        Assert.Throws<PlaybookContextException>(() => _resolver.ComputeFingerprint("homelab", new ContainerBuildConfig("playbooks/nope")));
        var ex = Assert.Throws<PlaybookContextException>(
            () => _resolver.ComputeFingerprint("homelab", _build with { Dockerfile = "Other.Dockerfile" }));
        Assert.Contains("Other.Dockerfile", ex.Message);
    }

    private void WriteShared(string relative, string content)
    {
        var path = Path.Combine(_repoRoot, "inventory", "homelab", "playbooks", "_base", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private ContainerBuildConfig WithBase => _build with
    {
        AdditionalContexts = new Dictionary<string, string> { ["base"] = "playbooks/_base" }
    };

    [Fact]
    public void Additional_contexts_resolve_by_name_inside_the_kind_folder()
    {
        WriteShared("lib.sh", "echo lib\n");
        var contexts = _resolver.ResolveAdditionalContexts("homelab", WithBase);
        var (name, dir) = Assert.Single(contexts);
        Assert.Equal("base", name);
        Assert.Equal(Path.Combine(_repoRoot, "inventory", "homelab", "playbooks", "_base"), dir);
        Assert.Empty(_resolver.ResolveAdditionalContexts("homelab", _build));
    }

    [Fact]
    public void Fingerprint_covers_additional_contexts_but_not_sibling_playbooks()
    {
        WriteShared("lib.sh", "echo lib\n");
        var without = _resolver.ComputeFingerprint("homelab", _build);
        var with = _resolver.ComputeFingerprint("homelab", WithBase);
        Assert.NotEqual(without, with);

        WriteShared("lib.sh", "echo lib v2\n");
        var changedBase = _resolver.ComputeFingerprint("homelab", WithBase);
        Assert.NotEqual(with, changedBase);
        Assert.Equal(without, _resolver.ComputeFingerprint("homelab", _build));

        var sibling = Path.Combine(_repoRoot, "inventory", "homelab", "playbooks", "other");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "Dockerfile"), "FROM alpine\n");
        Assert.Equal(changedBase, _resolver.ComputeFingerprint("homelab", WithBase));
    }

    [Fact]
    public void Missing_or_symlinked_additional_contexts_are_rejected()
    {
        var missing = Assert.Throws<PlaybookContextException>(() => _resolver.ComputeFingerprint("homelab", WithBase));
        Assert.Contains("additional context 'base'", missing.Message);

        var outside = Directory.CreateTempSubdirectory("lodge-outside-").FullName;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_repoRoot, "inventory", "homelab", "playbooks", "_base"), outside);
            Assert.Throws<PlaybookContextException>(() => _resolver.ComputeFingerprint("homelab", WithBase));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
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
                () => _resolver.ComputeFingerprint("homelab", new ContainerBuildConfig("playbooks/evil")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
