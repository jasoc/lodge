using System.Security.Cryptography;
using System.Text;
using Lodge.Core.Catalog;
using Lodge.Infrastructure.Git;
using Microsoft.Extensions.Options;

namespace Lodge.Infrastructure.Execution;

/// <summary>A <c>docker.build</c> context that can't be used: missing, escaping the kind folder, no Dockerfile.</summary>
public sealed class PlaybookContextException : Exception
{
    public PlaybookContextException(string message) : base(message)
    {
    }
}

/// <summary>
/// Resolves a <see cref="DockerBuildConfig"/> against the inventory working tree and
/// fingerprints it. A build context lives at <c>inventory/{kind}/{context}</c> — the same
/// local tree the capability catalog itself is read from, so a catalog and the playbook
/// folders it points at always come from one consistent checkout.
///
/// The fingerprint is a SHA-256 over every file's relative path, unix mode and content
/// (symlinks hashed by their target text, never followed) plus the dockerfile/target/args.
/// It is deliberately independent of the commit SHA, so an unrelated commit doesn't
/// rebuild every playbook. It deliberately ignores <c>.dockerignore</c> semantics: a
/// change to an ignored file costs at most one redundant build and one re-confirmation,
/// never a stale image.
/// </summary>
public sealed class PlaybookContextResolver
{
    private readonly string _repoRoot;

    public PlaybookContextResolver(IOptions<GitSnapshotOptions> options)
    {
        _repoRoot = string.IsNullOrWhiteSpace(options.Value.RepoRoot)
            ? Directory.GetCurrentDirectory()
            : options.Value.RepoRoot;
    }

    /// <summary>Absolute path of the build context folder, confined to <c>inventory/{kind}/</c>.</summary>
    public string ResolveContextDirectory(string kindCode, DockerBuildConfig build)
    {
        var kindRoot = Path.GetFullPath(Path.Combine(_repoRoot, "inventory", kindCode));
        var contextDir = Path.GetFullPath(Path.Combine(kindRoot, build.Context));
        if (!IsUnder(contextDir, kindRoot))
        {
            throw new PlaybookContextException($"build context '{build.Context}' escapes inventory/{kindCode}/.");
        }

        var info = new DirectoryInfo(contextDir);
        if (!info.Exists)
        {
            throw new PlaybookContextException($"build context 'inventory/{kindCode}/{build.Context}' does not exist.");
        }
        if (info.LinkTarget is not null)
        {
            throw new PlaybookContextException($"build context 'inventory/{kindCode}/{build.Context}' is a symlink — use a real folder.");
        }

        return contextDir;
    }

    /// <summary>Absolute path of the Dockerfile, confined to the context folder.</summary>
    public string ResolveDockerfile(string contextDir, DockerBuildConfig build)
    {
        var dockerfile = Path.GetFullPath(Path.Combine(contextDir, build.Dockerfile ?? "Dockerfile"));
        if (!IsUnder(dockerfile, contextDir))
        {
            throw new PlaybookContextException($"dockerfile '{build.Dockerfile}' escapes its build context '{build.Context}'.");
        }
        if (!File.Exists(dockerfile))
        {
            throw new PlaybookContextException(
                $"build context '{build.Context}' has no '{build.Dockerfile ?? "Dockerfile"}'.");
        }

        return dockerfile;
    }

    public string ComputeFingerprint(string kindCode, DockerBuildConfig build)
    {
        var contextDir = ResolveContextDirectory(kindCode, build);
        ResolveDockerfile(contextDir, build);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Line(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text + "\n"));

        Line("lodge-playbook-v1");
        Line($"dockerfile:{build.Dockerfile ?? "Dockerfile"}");
        Line($"target:{build.Target}");
        foreach (var (name, value) in (build.Args ?? new Dictionary<string, string>()).OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            Line($"arg:{name}={value}");
        }

        foreach (var entry in Walk(new DirectoryInfo(contextDir), contextDir))
        {
            var relative = Path.GetRelativePath(contextDir, entry.FullName).Replace('\\', '/');
            if (entry.LinkTarget is { } target)
            {
                Line($"link:{relative}->{target}");
                continue;
            }

            var mode = OperatingSystem.IsWindows() ? 0 : (int)File.GetUnixFileMode(entry.FullName);
            Line($"file:{relative}:{mode}");
            using var stream = File.OpenRead(entry.FullName);
            Line(Convert.ToHexStringLower(SHA256.HashData(stream)));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Files and symlinks under <paramref name="dir"/>, in ordinal relative-path order; symlinked directories are not descended.</summary>
    private static IEnumerable<FileSystemInfo> Walk(DirectoryInfo dir, string root)
    {
        foreach (var entry in dir.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (entry.LinkTarget is not null || entry is FileInfo)
            {
                yield return entry;
            }
            else if (entry is DirectoryInfo sub)
            {
                foreach (var nested in Walk(sub, root))
                {
                    yield return nested;
                }
            }
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSep, StringComparison.Ordinal);
    }
}
