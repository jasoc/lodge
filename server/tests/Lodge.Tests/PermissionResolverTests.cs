using Lodge.Core.Abstractions;
using Lodge.Infrastructure.Auth;
using Lodge.Infrastructure.Git;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lodge.Tests;

public class PermissionResolverTests : IDisposable
{
    private readonly string _repoRoot;

    public PermissionResolverTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), "lodge-perm-" + Guid.NewGuid().ToString("N"));
        var kindDir = Path.Combine(_repoRoot, "inventory", "acme");
        Directory.CreateDirectory(kindDir);
        File.WriteAllText(Path.Combine(kindDir, "runbook-permissions.yaml"), """
            permissions:
              - runbook: acme-ops/configure-sso
                allowed_groups:
                  - LODGE-ACME-L1
                  - LODGE-ACME-L2
              - runbook: acme-ops/redeploy
                allowed_groups:
                  - LODGE-ACME-L2
            """);
    }

    private FilePermissionResolver Resolver() =>
        new(Options.Create(new GitSnapshotOptions { RepoRoot = _repoRoot }));

    private static AuthenticatedUser User(bool isAdmin, params string[] groups) =>
        new("u1", "User One", groups, isAdmin);

    [Fact]
    public void Admin_BypassesAllGrants()
    {
        var resolver = Resolver();
        Assert.True(resolver.CanRun(User(isAdmin: true), "acme-ops/redeploy"));
        Assert.True(resolver.CanRun(User(isAdmin: true), "unknown/runbook"));
    }

    [Fact]
    public void NonAdmin_GrantedWhenGroupsIntersect()
    {
        var resolver = Resolver();
        Assert.True(resolver.CanRun(User(false, "LODGE-ACME-L1"), "acme-ops/configure-sso"));
        Assert.True(resolver.CanRun(User(false, "LODGE-ACME-L2"), "acme-ops/redeploy"));
    }

    [Fact]
    public void NonAdmin_DeniedWhenGroupsDoNotIntersect()
    {
        var resolver = Resolver();
        // L1 is not allowed on redeploy.
        Assert.False(resolver.CanRun(User(false, "LODGE-ACME-L1"), "acme-ops/redeploy"));
    }

    [Fact]
    public void NonAdmin_DeniedForUngovernedRunbook_FailClosed()
    {
        var resolver = Resolver();
        Assert.False(resolver.CanRun(User(false, "LODGE-ACME-L2"), "unknown/runbook"));
    }

    [Fact]
    public void Invalidate_ReloadsPermissionsFileFromDisk()
    {
        var resolver = Resolver();
        Assert.False(resolver.CanRun(User(false, "LODGE-ACME-L1"), "acme-ops/redeploy"));

        File.WriteAllText(Path.Combine(_repoRoot, "inventory", "acme", "runbook-permissions.yaml"), """
            permissions:
              - runbook: acme-ops/redeploy
                allowed_groups:
                  - LODGE-ACME-L1
            """);

        // Without invalidation the stale in-memory map would still deny this.
        resolver.Invalidate();

        Assert.True(resolver.CanRun(User(false, "LODGE-ACME-L1"), "acme-ops/redeploy"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoRoot))
        {
            Directory.Delete(_repoRoot, recursive: true);
        }
    }
}
