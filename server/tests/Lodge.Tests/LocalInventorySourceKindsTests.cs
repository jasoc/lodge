using Lodge.Infrastructure.Git;
using Lodge.Infrastructure.Reconciliation;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lodge.Tests;

public sealed class LocalInventorySourceKindsTests : IDisposable
{
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("lodge-kinds-test-").FullName;

    public void Dispose() => Directory.Delete(_repoRoot, recursive: true);

    private void Folder(string relative, string? kindYaml = null)
    {
        var dir = Path.Combine(_repoRoot, "inventory", relative);
        Directory.CreateDirectory(dir);
        if (kindYaml is not null)
        {
            File.WriteAllText(Path.Combine(dir, "kind.yaml"), kindYaml);
        }
    }

    [Fact]
    public async Task Every_valid_kind_folder_is_a_kind_named_by_its_optional_manifest()
    {
        Folder("homelab", "name: \"Homelab\"\n");
        Folder("acme");
        Folder("broken", "name: [unclosed\n");
        Folder("Not-A-Code");
        Folder(".hidden");

        var source = new LocalInventorySource(Options.Create(new GitSnapshotOptions { RepoRoot = _repoRoot }));
        var kinds = await source.GetKindsAsync();

        Assert.Equal(
            new[] { new KindDescriptor("acme", null), new KindDescriptor("broken", null), new KindDescriptor("homelab", "Homelab") },
            kinds);
    }

    [Fact]
    public async Task No_inventory_folder_means_no_kinds()
    {
        var source = new LocalInventorySource(Options.Create(new GitSnapshotOptions { RepoRoot = _repoRoot }));
        Assert.Empty(await source.GetKindsAsync());
    }
}
