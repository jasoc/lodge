using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace Lodge.Infrastructure.Execution;

/// <summary>
/// The docker CLI, against whichever daemon the standard docker environment points at —
/// the local socket by default, or <c>DOCKER_HOST</c> if set on the server process.
/// </summary>
public sealed class DockerCli
{
    private readonly DockerExecutorOptions _options;

    public DockerCli(IOptions<DockerExecutorOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>
    /// Runs the docker CLI to completion. With a <paramref name="logPath"/>, both streams
    /// are appended to it (serialized so lines never interleave mid-write); otherwise they
    /// are discarded, or collected into <paramref name="capture"/> (stdout only).
    /// <paramref name="environment"/> goes to the CLI process itself — how values reach a
    /// container without ever appearing in an argv (<c>-e NAME</c>).
    /// </summary>
    public async Task<int> RunAsync(
        IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment, string? logPath, StringBuilder? capture = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.DockerBinaryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{_options.DockerBinaryPath}'.");

        var logLock = new object();
        var stdout = capture is not null
            ? CaptureAsync(process.StandardOutput, capture)
            : PipeAsync(process.StandardOutput.BaseStream, logPath, logLock);
        var stderr = PipeAsync(process.StandardError.BaseStream, logPath, logLock);

        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode;
    }

    private static async Task CaptureAsync(StreamReader reader, StringBuilder capture)
        => capture.Append(await reader.ReadToEndAsync());

    private static async Task PipeAsync(Stream source, string? logPath, object logLock)
    {
        var buffer = new byte[4096];
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            if (logPath is null)
            {
                continue;
            }
            lock (logLock)
            {
                using var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                fs.Write(buffer, 0, read);
            }
        }
    }
}

/// <summary>
/// <see cref="IImageBuilder"/> on the docker daemon: the image cache is the daemon's own
/// image store, builds go through buildx (BuildKit), which is what makes
/// <c>--build-context</c> available for additional contexts.
/// </summary>
public sealed class DockerImageBuilder : IImageBuilder
{
    private readonly DockerExecutorOptions _options;
    private readonly DockerCli _docker;

    public DockerImageBuilder(IOptions<DockerExecutorOptions> options, DockerCli docker)
    {
        _options = options.Value;
        _docker = docker;
    }

    public async Task<bool> ExistsAsync(string image, CancellationToken cancellationToken = default)
        => await _docker.RunAsync(new[] { "image", "inspect", "--format", "{{.Id}}", image }, null, logPath: null) == 0;

    public async Task<int> BuildAsync(ImageBuildSpec spec, string logPath, CancellationToken cancellationToken = default)
    {
        var args = new List<string>
        {
            "build",
            "--tag", spec.Image,
            "--label", "lodge.managed=true",
            "--label", $"lodge.playbook={spec.Playbook}",
            "--label", $"lodge.fingerprint={spec.Fingerprint}",
            "--file", spec.DockerfilePath
        };
        if (spec.Target is not null)
        {
            args.Add("--target");
            args.Add(spec.Target);
        }
        foreach (var (name, value) in spec.Args ?? new Dictionary<string, string>())
        {
            args.Add("--build-arg");
            args.Add($"{name}={value}");
        }
        foreach (var (name, dir) in spec.AdditionalContexts)
        {
            args.Add("--build-context");
            args.Add($"{name}={dir}");
        }
        args.Add(spec.ContextDirectory);

        var exitCode = await _docker.RunAsync(args, null, logPath);
        if (exitCode == 0)
        {
            await PruneOldImagesAsync(spec.Playbook, keep: spec.Image, logPath);
        }
        return exitCode;
    }

    /// <summary>Best-effort: keeps the newest <see cref="DockerExecutorOptions.KeepImagesPerPlaybook"/> images of one playbook, never the one just built.</summary>
    private async Task PruneOldImagesAsync(string playbook, string keep, string logPath)
    {
        if (_options.KeepImagesPerPlaybook <= 0)
        {
            return;
        }

        var listing = new StringBuilder();
        var exit = await _docker.RunAsync(
            new[] { "image", "ls", "--filter", $"label=lodge.playbook={playbook}", "--format", "{{.Repository}}:{{.Tag}}" },
            null, logPath: null, capture: listing);
        if (exit != 0)
        {
            return;
        }

        // `docker image ls` lists newest first.
        var stale = listing.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => tag != keep && !tag.EndsWith(":<none>", StringComparison.Ordinal))
            .Skip(Math.Max(0, _options.KeepImagesPerPlaybook - 1))
            .ToList();
        foreach (var tag in stale)
        {
            // An image still used by a running container just fails to delete — fine.
            if (await _docker.RunAsync(new[] { "image", "rm", tag }, null, logPath: null) == 0)
            {
                File.AppendAllText(logPath, $"[lodge] pruned old playbook image {tag}{Environment.NewLine}");
            }
        }
    }
}

/// <summary>
/// <see cref="IContainerRunner"/> on the docker daemon. A run is <c>docker create</c> (every
/// value passed as <c>-e NAME</c> with the value in the CLI's own environment), <c>docker
/// start</c>, <c>docker logs --follow</c> into the run log, <c>docker wait</c> for the exit
/// code, then <c>docker rm</c> — not one attached <c>docker run --rm</c>, so the container
/// and its exit code survive a server restart until they've been collected.
/// </summary>
public sealed class DockerContainerRunner : IContainerRunner
{
    private readonly DockerExecutorOptions _options;
    private readonly DockerCli _docker;

    public DockerContainerRunner(IOptions<DockerExecutorOptions> options, DockerCli docker)
    {
        _options = options.Value;
        _docker = docker;
    }

    public async Task<int> RunAsync(ContainerRunSpec spec, string logPath, CancellationToken cancellationToken = default)
    {
        // `docker create` pulls a missing image (its progress goes to the log) and prints
        // the new container's id. The name makes a second create of the same run fail
        // instead of starting a duplicate.
        var created = new StringBuilder();
        var exitCode = await _docker.RunAsync(CreateArgs(spec), spec.Environment, logPath, created);
        if (exitCode != 0)
        {
            return exitCode;
        }

        var containerId = created.ToString().Trim();
        exitCode = await _docker.RunAsync(new[] { "start", containerId }, null, logPath);
        if (exitCode != 0)
        {
            await RemoveAsync(containerId);
            return exitCode;
        }

        return await FollowAsync(containerId, since: null, logPath);
    }

    public async Task<int?> ReattachAsync(string runId, string logPath, CancellationToken cancellationToken = default)
    {
        var listing = new StringBuilder();
        var exitCode = await _docker.RunAsync(
            new[] { "ps", "--all", "--quiet", "--no-trunc", "--filter", $"label={ContainerLabels.RunId}={runId}" },
            null, logPath: null, capture: listing);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"could not list containers (docker exit {exitCode}).");
        }

        var containerId = listing.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (containerId is null)
        {
            return null;
        }

        // Only what the container printed after the log was last written: a line or two
        // around the restart may show up twice, none goes missing.
        DateTimeOffset? since = File.Exists(logPath) ? File.GetLastWriteTimeUtc(logPath) : null;
        File.AppendAllText(logPath, $"[lodge] reattached to container {containerId[..Math.Min(12, containerId.Length)]} after a server restart{Environment.NewLine}");
        return await FollowAsync(containerId, since, logPath);
    }

    /// <summary>Streams the container's output into the log until it stops, then collects its exit code and removes it.</summary>
    private async Task<int> FollowAsync(string containerId, DateTimeOffset? since, string logPath)
    {
        var logsArgs = new List<string> { "logs", "--follow" };
        if (since is { } from)
        {
            logsArgs.Add("--since");
            logsArgs.Add(from.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture));
        }
        logsArgs.Add(containerId);
        await _docker.RunAsync(logsArgs, null, logPath);

        var waited = new StringBuilder();
        var exitCode = await _docker.RunAsync(new[] { "wait", containerId }, null, logPath, waited);
        if (exitCode != 0 || !int.TryParse(waited.ToString().Trim(), System.Globalization.CultureInfo.InvariantCulture, out var containerExit))
        {
            throw new InvalidOperationException($"could not read the exit code of container {containerId} (docker exit {exitCode}).");
        }

        await RemoveAsync(containerId);
        return containerExit;
    }

    /// <summary>Best-effort: a container left behind is found again by its label, never mistaken for a new run.</summary>
    private Task<int> RemoveAsync(string containerId)
        => _docker.RunAsync(new[] { "rm", "--force", "--volumes", containerId }, null, logPath: null);

    private List<string> CreateArgs(ContainerRunSpec spec)
    {
        var args = new List<string> { "create", "--name", $"lodge-{spec.RunId}" };
        if (!string.IsNullOrWhiteSpace(_options.Network))
        {
            args.Add("--network");
            args.Add(_options.Network);
        }
        foreach (var (name, value) in spec.Labels)
        {
            args.Add("--label");
            args.Add($"{name}={value}");
        }
        foreach (var name in spec.Environment.Keys)
        {
            args.Add("-e");
            args.Add(name);
        }

        // `--entrypoint` only takes the executable; any further entrypoint elements are
        // ordinary leading argv, exactly how docker itself composes ENTRYPOINT + CMD.
        var command = spec.Command;
        if (spec.Entrypoint is { Count: > 0 } entrypoint)
        {
            args.Add("--entrypoint");
            args.Add(entrypoint[0]);
            command = entrypoint.Skip(1).Concat(spec.Command).ToList();
        }

        args.Add(spec.Image);
        args.AddRange(command);
        return args;
    }
}
