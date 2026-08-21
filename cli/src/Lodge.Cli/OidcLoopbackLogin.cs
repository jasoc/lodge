using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Lodge.Cli;

/// <summary>
/// The CLI's half of the SSO login flow: opens the user's browser at the server's own
/// <c>/api/v1/auth/oidc/login</c> (the server talks to the IdP, never the CLI directly —
/// see docs/AGENTS.md invariant 10), then waits on a local loopback listener for the
/// server to redirect back with a minted token. Same shape as `gh auth login`.
/// </summary>
public static class OidcLoopbackLogin
{
    public static async Task<(string Token, string SubjectId, DateTimeOffset? ExpiresAt)> RunAsync(
        string serverUrl, CancellationToken ct)
    {
        var port = GetFreeLoopbackPort();
        var redirectUri = $"http://127.0.0.1:{port}/callback";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri + "/");
        listener.Start();

        var authorizeUrl = $"{serverUrl.TrimEnd('/')}/api/v1/auth/oidc/login?cli_redirect={Uri.EscapeDataString(redirectUri)}";
        Console.WriteLine("Opening your browser to sign in via SSO...");
        Console.WriteLine($"If it doesn't open automatically, visit:\n  {authorizeUrl}");
        TryOpenBrowser(authorizeUrl);

        using var registration = ct.Register(() => listener.Stop());
        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync();
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        var query = context.Request.QueryString;
        var token = query["token"];
        var subjectId = query["subject_id"];
        var expiresAtRaw = query["expires_at"];

        var ok = !string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(subjectId);
        var html = ok
            ? "<html><body><h3>Signed in to Lodge.</h3><p>You can close this tab.</p></body></html>"
            : "<html><body><h3>Sign-in failed.</h3><p>Return to the terminal for details.</p></body></html>";
        var buffer = System.Text.Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer, ct);
        context.Response.Close();
        listener.Stop();

        if (!ok)
        {
            throw new LodgeApiException("SSO sign-in did not return a token.");
        }

        DateTimeOffset? expiresAt = DateTimeOffset.TryParse(expiresAtRaw, out var parsed) ? parsed : null;
        return (token!, subjectId!, expiresAt);
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort only — the URL printed above is the fallback.
        }
    }
}
