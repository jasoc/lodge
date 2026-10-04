using System.Diagnostics;
using Lodge.Core.Catalog;
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
    /// container without ever appearing in an argv (<c>-e NAME</c>). Whatever reaches the log
    /// passes through <paramref name="secrets"/> first, so a container that echoes its
    /// environment doesn't leak resolved secrets into a log anyone can read.
    /// </summary>
    public async Task<int> RunAsync(
        IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment, string? logPath,
        StringBuilder? capture = null, SecretMasker? secrets = null)
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
            : PipeAsync(process.StandardOutput.BaseStream, logPath, logLock, secrets);
        var stderr = PipeAsync(process.StandardError.BaseStream, logPath, logLock, secrets);

        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode;
    }

    private static async Task CaptureAsync(StreamReader reader, StringBuilder capture)
        => capture.Append(await reader.ReadToEndAsync());

    private static async Task PipeAsync(Stream source, string? logPath, object logLock, SecretMasker? secrets)
    {
        var buffer = new byte[4096];
        await using Stream sink = logPath is null
            ? Stream.Null
            : secrets is { IsEmpty: false } ? secrets.Wrap(new AppendStream(logPath, logLock)) : new AppendStream(logPath, logLock);
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            sink.Write(buffer, 0, read);
        }
        sink.Flush();
    }

    /// <summary>Appends each write to the log file under the shared lock, so lines of the two streams never interleave mid-write.</summary>
    private sealed class AppendStream(string path, object gate) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (gate)
            {
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                fs.Write(buffer, offset, count);
            }
        }

        public override void Flush()
        {
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
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
///
/// Containers are created restrictive: all capabilities dropped, <c>no-new-privileges</c>,
/// a read-only root filesystem with writable tmpfs at <c>/tmp</c> and <c>/work</c>, and a
/// non-root user (the image's own, else <see cref="DefaultUser"/>). An action's
/// <see cref="ContainerSecurity"/> undoes any of that, explicitly. A run past its timeout
/// is killed by its <c>lodge.run_id</c> label.
/// </summary>
public sealed class DockerContainerRunner : IContainerRunner
{
    /// <summary>"nobody": the non-root user a container gets when its image names none.</summary>
    public const string DefaultUser = "65534:65534";

    /// <summary>The always-writable work dir of a read-only container (the <c>LODGE_WORK_DIR</c> env var points at it).</summary>
    public const string WorkDir = "/work";

    private readonly DockerExecutorOptions _options;
    private readonly DockerCli _docker;

    public DockerContainerRunner(IOptions<DockerExecutorOptions> options, DockerCli docker)
    {
        _options = options.Value;
        _docker = docker;
    }

    public async Task<ContainerRunResult> RunAsync(ContainerRunSpec spec, string logPath, CancellationToken cancellationToken = default)
    {
        var secrets = spec.Secrets ?? SecretMasker.None;
        var network = ResolveNetwork(spec.NetworkProfile);
        var user = await ResolveUserAsync(spec, logPath);

        // `docker create` pulls a missing image (its progress goes to the log) and prints
        // the new container's id. The name makes a second create of the same run fail
        // instead of starting a duplicate.
        var created = new StringBuilder();
        var exitCode = await _docker.RunAsync(CreateArgs(spec, network, user), spec.Environment, logPath, created, secrets);
        if (exitCode != 0)
        {
            return new ContainerRunResult(exitCode);
        }

        var containerId = created.ToString().Trim();
        exitCode = await _docker.RunAsync(new[] { "start", containerId }, null, logPath, secrets: secrets);
        if (exitCode != 0)
        {
            await RemoveAsync(containerId);
            return new ContainerRunResult(exitCode);
        }

        var deadline = spec.Timeout is { } timeout ? DateTimeOffset.UtcNow + timeout : (DateTimeOffset?)null;
        return await FollowAsync(spec.RunId, containerId, deadline, logPath, secrets, logOutput: true);
    }

    public async Task<ContainerRunResult?> ReattachAsync(
        string runId, string logPath, DateTimeOffset? deadline, CancellationToken cancellationToken = default)
    {
        var containerId = (await ListAsync(runId)).FirstOrDefault();
        if (containerId is null)
        {
            return null;
        }

        // The resolved secrets of the run lived only in the process that started it, so what
        // the container prints from here on can't be masked — and the log is readable by
        // anyone who can see the action. It is not logged at all; the exit code is.
        File.AppendAllText(logPath,
            $"[lodge] reattached to container {containerId[..Math.Min(12, containerId.Length)]} after a server restart; " +
            $"its output from here on is not logged (secrets can't be masked after a restart).{Environment.NewLine}");
        return await FollowAsync(runId, containerId, deadline, logPath, SecretMasker.None, logOutput: false);
    }

    public async Task<int> RemoveOrphansAsync(
        Func<CancellationToken, Task<IReadOnlySet<string>>> liveRunIds, CancellationToken cancellationToken = default)
    {
        var listing = new StringBuilder();
        var exitCode = await _docker.RunAsync(
            new[]
            {
                "ps", "--all", "--no-trunc", "--filter", $"label={ContainerLabels.Managed}=true",
                "--format", $"{{{{.ID}}}} {{{{.Label \"{ContainerLabels.RunId}\"}}}}"
            },
            null, logPath: null, capture: listing);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"could not list containers (docker exit {exitCode}).");
        }

        var live = await liveRunIds(cancellationToken);
        var removed = 0;
        foreach (var line in listing.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            var runId = parts.Length > 1 ? parts[1] : string.Empty;
            if (runId.Length > 0 && live.Contains(runId))
            {
                continue;
            }
            if (await RemoveAsync(parts[0]) == 0)
            {
                removed++;
            }
        }
        return removed;
    }

    /// <summary>The ids of the containers (any state) of one run.</summary>
    private async Task<IReadOnlyList<string>> ListAsync(string runId, bool runningOnly = false)
    {
        var listing = new StringBuilder();
        var args = new List<string> { "ps", "--quiet", "--no-trunc", "--filter", $"label={ContainerLabels.RunId}={runId}" };
        if (!runningOnly)
        {
            args.Insert(1, "--all");
        }
        var exitCode = await _docker.RunAsync(args, null, logPath: null, capture: listing);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"could not list containers (docker exit {exitCode}).");
        }

        return listing.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Streams the container's output into the log until it stops, then collects its exit
    /// code and removes it. At <paramref name="deadline"/> the container is killed by its
    /// run-id label — the stream then ends by itself — and the run reports it timed out.
    /// </summary>
    private async Task<ContainerRunResult> FollowAsync(
        string runId, string containerId, DateTimeOffset? deadline, string logPath, SecretMasker secrets, bool logOutput)
    {
        using var finished = new CancellationTokenSource();
        var timedOut = false;
        Task? watchdog = null;
        if (deadline is { } at)
        {
            watchdog = Task.Run(async () =>
            {
                try
                {
                    var remaining = at - DateTimeOffset.UtcNow;
                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(remaining, finished.Token);
                    }
                    timedOut = true;
                    File.AppendAllText(logPath, $"[lodge] timed out: killing the container{Environment.NewLine}");
                    await KillAsync(runId, logPath);
                }
                catch (OperationCanceledException)
                {
                    // The container ended on its own first.
                }
            });
        }

        try
        {
            await _docker.RunAsync(new[] { "logs", "--follow", containerId }, null, logOutput ? logPath : null, secrets: secrets);

            var waited = new StringBuilder();
            var exitCode = await _docker.RunAsync(new[] { "wait", containerId }, null, logPath, waited, secrets);
            if (exitCode != 0 || !int.TryParse(waited.ToString().Trim(), System.Globalization.CultureInfo.InvariantCulture, out var containerExit))
            {
                throw new InvalidOperationException($"could not read the exit code of container {containerId} (docker exit {exitCode}).");
            }

            await RemoveAsync(containerId);
            return new ContainerRunResult(containerExit, timedOut);
        }
        finally
        {
            await finished.CancelAsync();
            if (watchdog is not null)
            {
                await watchdog;
            }
        }
    }

    /// <summary>Kills the running container(s) of a run by label — whatever its id, and a no-op when it is already gone.</summary>
    private async Task KillAsync(string runId, string logPath)
    {
        foreach (var id in await ListAsync(runId, runningOnly: true))
        {
            await _docker.RunAsync(new[] { "kill", id }, null, logPath);
        }
    }

    /// <summary>Best-effort: a container left behind is found again by its label, never mistaken for a new run.</summary>
    private Task<int> RemoveAsync(string containerId)
        => _docker.RunAsync(new[] { "rm", "--force", "--volumes", containerId }, null, logPath: null);

    /// <summary>The docker network of a profile: <c>default</c>/unset is <see cref="DockerExecutorOptions.Network"/>, <c>none</c> no network.</summary>
    private string? ResolveNetwork(string? profile)
    {
        if (string.IsNullOrEmpty(profile) || profile == "default")
        {
            return string.IsNullOrWhiteSpace(_options.Network) ? null : _options.Network;
        }
        if (profile == "none")
        {
            return "none";
        }
        if (_options.NetworkProfiles.TryGetValue(profile, out var network) && !string.IsNullOrWhiteSpace(network))
        {
            return network;
        }

        throw new ContainerPolicyException(
            $"network profile '{profile}' is not configured on this server (DockerExecutor__NetworkProfiles__{profile}=<docker network>); " +
            "built in: default, none.");
    }

    /// <summary>
    /// The <c>--user</c> to run as, or null to keep the image's own. <c>auto</c> (the
    /// default) keeps a non-root user the image declares and otherwise falls back to
    /// <see cref="DefaultUser"/>; <c>image</c> keeps whatever the image says, root included.
    /// </summary>
    private async Task<string?> ResolveUserAsync(ContainerRunSpec spec, string logPath)
    {
        var requested = spec.Security?.User;
        if (requested == ContainerSecurity.UserImage)
        {
            return null;
        }
        if (requested is not null and not ContainerSecurity.UserAuto)
        {
            return requested;
        }

        var imageUser = await InspectImageUserAsync(spec.Image, logPath);
        return imageUser is null or "" or "root" or "0" || imageUser.StartsWith("root:", StringComparison.Ordinal) || imageUser.StartsWith("0:", StringComparison.Ordinal)
            ? DefaultUser
            : null;
    }

    /// <summary>The image's <c>USER</c> (empty = root), pulling the image first when it isn't local yet; null if it can't be inspected (create reports the real error).</summary>
    private async Task<string?> InspectImageUserAsync(string image, string logPath)
    {
        var output = new StringBuilder();
        if (await _docker.RunAsync(new[] { "image", "inspect", "--format", "{{.Config.User}}", image }, null, logPath: null, capture: output) != 0)
        {
            if (await _docker.RunAsync(new[] { "pull", "--quiet", image }, null, logPath) != 0)
            {
                return null;
            }
            output.Clear();
            if (await _docker.RunAsync(new[] { "image", "inspect", "--format", "{{.Config.User}}", image }, null, logPath: null, capture: output) != 0)
            {
                return null;
            }
        }

        return output.ToString().Trim();
    }

    private List<string> CreateArgs(ContainerRunSpec spec, string? network, string? user)
    {
        var security = spec.Security;
        var args = new List<string> { "create", "--name", $"lodge-{spec.RunId}" };
        if (network is not null)
        {
            args.Add("--network");
            args.Add(network);
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

        // Restrictive by default; each relaxation is an explicit field of the action.
        args.Add("--cap-drop=ALL");
        foreach (var cap in security?.CapAdd ?? Array.Empty<string>())
        {
            args.Add($"--cap-add={cap}");
        }
        if (security?.NoNewPrivileges != false)
        {
            args.Add("--security-opt");
            args.Add("no-new-privileges");
        }
        if (security?.ReadOnlyRootfs != false)
        {
            args.Add("--read-only");
            var writable = new[] { "/tmp", WorkDir }.Concat(security?.Tmpfs ?? Array.Empty<string>()).Distinct();
            foreach (var path in writable)
            {
                args.Add("--tmpfs");
                args.Add($"{path}:rw,nosuid,nodev,mode=1777,size={_options.TmpfsSize}");
            }
        }
        else
        {
            foreach (var path in security.Tmpfs ?? Array.Empty<string>())
            {
                args.Add("--tmpfs");
                args.Add($"{path}:rw,nosuid,nodev,mode=1777,size={_options.TmpfsSize}");
            }
        }
        if (user is not null)
        {
            args.Add("--user");
            args.Add(user);
        }

        if (spec.Resources is { } resources)
        {
            if (resources.Memory is { } memory)
            {
                args.Add("--memory");
                args.Add(memory);
                args.Add("--memory-swap");
                args.Add(memory);
            }
            if (resources.Cpus is { } cpus)
            {
                args.Add("--cpus");
                args.Add(cpus.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            if (resources.Pids is { } pids)
            {
                args.Add("--pids-limit");
                args.Add(pids.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
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
