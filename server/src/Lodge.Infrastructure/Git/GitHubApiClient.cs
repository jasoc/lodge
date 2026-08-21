using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lodge.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Lodge.Infrastructure.Git;

/// <summary>
/// <see cref="IGitHubClient"/> backed by the GitHub REST API. The token is resolved
/// via <see cref="ISecretProvider"/> and sent as a bearer credential; it is never
/// stored in config or code. All calls are reads.
/// </summary>
public sealed class GitHubApiClient : IGitHubClient
{
    private readonly HttpClient _http;
    private readonly ISecretProvider _secrets;
    private readonly GitHubOptions _options;

    public GitHubApiClient(HttpClient http, ISecretProvider secrets, IOptions<GitOptions> options)
    {
        _http = http;
        _secrets = secrets;
        _options = options.Value.GitHub;

        _http.BaseAddress ??= new Uri("https://api.github.com/");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("lodge");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<string> GetBranchHeadShaAsync(string branch, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get,
            $"repos/{_options.Owner}/{_options.Repo}/commits/{Uri.EscapeDataString(branch)}", cancellationToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return doc.RootElement.GetProperty("sha").GetString()
            ?? throw new InvalidOperationException("GitHub commit response missing 'sha'.");
    }

    public async Task<IReadOnlyList<string>> GetChangedFilesAsync(string baseSha, string headSha, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get,
            $"repos/{_options.Owner}/{_options.Repo}/compare/{baseSha}...{headSha}", cancellationToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var files = new List<string>();
        if (doc.RootElement.TryGetProperty("files", out var filesElement))
        {
            foreach (var file in filesElement.EnumerateArray())
            {
                if (file.TryGetProperty("filename", out var name) && name.GetString() is { } path)
                {
                    files.Add(path);
                }
            }
        }
        return files;
    }

    public async Task<string?> GetFileContentAsync(string path, string gitRef, CancellationToken cancellationToken = default)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        using var request = await CreateRequestAsync(HttpMethod.Get,
            $"repos/{_options.Owner}/{_options.Repo}/contents/{normalized}?ref={Uri.EscapeDataString(gitRef)}", cancellationToken);
        using var response = await _http.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        var encoding = root.TryGetProperty("encoding", out var enc) ? enc.GetString() : null;
        var content = root.TryGetProperty("content", out var c) ? c.GetString() : null;
        if (content is null)
        {
            return null;
        }

        if (string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = Convert.FromBase64String(content.Replace("\n", string.Empty));
            return Encoding.UTF8.GetString(bytes);
        }
        return content;
    }

    public async Task<IReadOnlyList<string>> ListDirectoryAsync(string path, string gitRef, CancellationToken cancellationToken = default)
    {
        var entries = await ListContentsAsync(path, gitRef, cancellationToken);
        return entries
            .Where(e => string.Equals(e.Type, "file", StringComparison.Ordinal) &&
                e.Path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Path)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ListSubdirectoriesAsync(string path, string gitRef, CancellationToken cancellationToken = default)
    {
        var entries = await ListContentsAsync(path, gitRef, cancellationToken);
        return entries
            .Where(e => string.Equals(e.Type, "dir", StringComparison.Ordinal))
            .Select(e => e.Path)
            .ToList();
    }

    private async Task<IReadOnlyList<(string Type, string Path)>> ListContentsAsync(
        string path, string gitRef, CancellationToken cancellationToken)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/').TrimEnd('/');
        using var request = await CreateRequestAsync(HttpMethod.Get,
            $"repos/{_options.Owner}/{_options.Repo}/contents/{normalized}?ref={Uri.EscapeDataString(gitRef)}", cancellationToken);
        using var response = await _http.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<(string, string)>();
        }
        response.EnsureSuccessStatusCode();

        // A directory listing is a JSON array (a single-file path returns an object
        // instead, which this method is not meant for).
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<(string, string)>();
        }

        var entries = new List<(string Type, string Path)>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var type = entry.TryGetProperty("type", out var t) ? t.GetString() : null;
            var entryPath = entry.TryGetProperty("path", out var p) ? p.GetString() : null;
            if (type is not null && entryPath is not null)
            {
                entries.Add((type, entryPath));
            }
        }
        return entries;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string uri, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrWhiteSpace(_options.TokenSecretRef))
        {
            var token = await _secrets.GetSecretAsync(_options.TokenSecretRef, cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return request;
    }
}
