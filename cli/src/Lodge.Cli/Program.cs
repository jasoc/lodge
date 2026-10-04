using Lodge.Cli;
using Lodge.Validation;

var ct = CancellationToken.None;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    switch (args[0])
    {
        case "login":
            return await LoginAsync(args, ct);
        case "reconcile":
            return await ReconcileAsync(ct);
        case "instances":
            return await InstancesAsync(args, ct);
        case "actions":
            return await ActionsAsync(args, ct);
        case "tokens":
            return await TokensAsync(args, ct);
        case "validate":
            return Validate(args);
        default:
            PrintUsage();
            return 1;
    }
}
catch (LodgeApiException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        lodge — a peer client of the Lodge server, for humans and scripts alike.

        Usage:
          lodge login <server-url>
          lodge reconcile
          lodge instances list <kind>
          lodge instances show <kind> <instance>
          lodge actions list <kind> <instance>
          lodge actions confirm <kind> <instance> <action-id> [actor]
          lodge actions invalidate <kind> <instance> <action-id> [actor]
          lodge tokens create --display-name <name> [--scope <scope>]... [--ttl-minutes <n>]
          lodge tokens list
          lodge tokens revoke <token-id>
          lodge validate <inventory-dir> [--format text|json|github]   (offline, no server needed)
        """);
}

// The one command that doesn't talk to the server: it validates a local inventory with the
// server's own loading code, so a CI job fails exactly where a reconciliation cycle would
// report a validation error. Exit code 0 = valid, 1 = invalid or unusable arguments.
static int Validate(string[] args)
{
    string? directory = null;
    var format = ReportFormat.Text;
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--format" && i + 1 < args.Length)
        {
            if (!DiagnosticFormatter.TryParseFormat(args[++i], out format))
            {
                Console.Error.WriteLine($"error: unknown format '{args[i]}' (expected text, json or github).");
                return 1;
            }
        }
        else if (directory is null && !args[i].StartsWith("--", StringComparison.Ordinal))
        {
            directory = args[i];
        }
        else
        {
            Console.Error.WriteLine("usage: lodge validate <inventory-dir> [--format text|json|github]");
            return 1;
        }
    }
    if (directory is null)
    {
        Console.Error.WriteLine("usage: lodge validate <inventory-dir> [--format text|json|github]");
        return 1;
    }

    // <inventory-dir> is the inventory/ folder itself (kinds inside it) or the repo root holding it.
    var full = Path.GetFullPath(directory);
    string repoRoot;
    if (Directory.Exists(Path.Combine(full, "inventory")))
    {
        repoRoot = full;
    }
    else if (Directory.Exists(full) && string.Equals(Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)), "inventory", StringComparison.Ordinal))
    {
        repoRoot = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar))!;
    }
    else
    {
        Console.Error.WriteLine($"error: '{directory}' is not an inventory: expected an inventory/ folder, or the folder that contains it.");
        return 1;
    }

    var diagnostics = InventoryValidator.Validate(repoRoot);
    Console.Write(DiagnosticFormatter.Format(diagnostics, format));
    return diagnostics.Count == 0 ? 0 : 1;
}

static async Task<int> LoginAsync(string[] args, CancellationToken ct)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: lodge login <server-url>");
        return 1;
    }

    var serverUrl = args[1];

    // The server decides the login flow, not the CLI: NoAuth mints unconditionally,
    // Oidc requires the loopback/browser round trip. Same distinction the SPA makes
    // against the same endpoint.
    using var probe = new LodgeApiClient(serverUrl, token: null);
    var config = await probe.GetAuthConfigAsync(ct);

    if (string.Equals(config.Mode, "Oidc", StringComparison.OrdinalIgnoreCase))
    {
        var (token, subjectId, _) = await OidcLoopbackLogin.RunAsync(serverUrl, ct);
        CredentialStore.Save(new StoredCredentials(serverUrl, token));
        Console.WriteLine($"Logged in to {serverUrl} as {subjectId}.");
        return 0;
    }

    var login = await probe.LoginAsync(ct);
    CredentialStore.Save(new StoredCredentials(serverUrl, login.Token));
    Console.WriteLine($"Logged in to {serverUrl} as {login.SubjectId}.");
    return 0;
}

static LodgeApiClient RequireClient()
{
    var credentials = CredentialStore.Load()
        ?? throw new InvalidOperationException("Not logged in. Run `lodge login <server-url>` first.");
    return new LodgeApiClient(credentials.ServerUrl, credentials.Token);
}

static async Task<int> ReconcileAsync(CancellationToken ct)
{
    using var client = RequireClient();
    var summary = await client.ReconcileAsync(ct);
    Console.WriteLine($"kinds_checked={summary.KindsChecked} instances_reconciled={summary.InstancesReconciled} drift={summary.DriftCount}");
    foreach (var message in summary.Messages)
    {
        Console.WriteLine($"  {message}");
    }
    foreach (var error in summary.ValidationErrors)
    {
        Console.WriteLine($"  [validation] {error}");
    }
    return 0;
}

static async Task<int> InstancesAsync(string[] args, CancellationToken ct)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: lodge instances list|show <kind> [instance]");
        return 1;
    }

    using var client = RequireClient();
    switch (args[1])
    {
        case "list":
            foreach (var t in await client.GetInstancesAsync(args[2], ct))
            {
                Console.WriteLine($"{t.InstanceCode}\t{t.DisplayName}\tgen={t.Generation}\tregion={t.Region}");
            }
            return 0;
        case "show":
            if (args.Length < 4)
            {
                Console.Error.WriteLine("usage: lodge instances show <kind> <instance>");
                return 1;
            }
            var detail = await client.GetInstanceAsync(args[2], args[3], ct);
            Console.WriteLine($"{detail.Instance.InstanceCode} ({detail.Instance.DisplayName})");
            Console.WriteLine(detail.LatestYaml ?? "(no inventory revision yet)");
            return 0;
        default:
            Console.Error.WriteLine("usage: lodge instances list|show <kind> [instance]");
            return 1;
    }
}

static async Task<int> ActionsAsync(string[] args, CancellationToken ct)
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("usage: lodge actions list|confirm|invalidate <kind> <instance> [action-id] [actor]");
        return 1;
    }

    using var client = RequireClient();
    var kindCode = args[2];
    var instanceCode = args[3];

    switch (args[1])
    {
        case "list":
            foreach (var a in await client.GetActionsAsync(kindCode, instanceCode, ct))
            {
                Console.WriteLine($"{a.Id}\t{a.Status}\t{a.Policy}\t{a.Label}");
            }
            return 0;

        case "confirm":
        case "invalidate":
            if (args.Length < 5 || !Guid.TryParse(args[4], out var actionId))
            {
                Console.Error.WriteLine($"usage: lodge actions {args[1]} <kind> <instance> <action-id> [actor]");
                return 1;
            }
            var actor = args.Length > 5 ? args[5] : null;
            var result = args[1] == "confirm"
                ? await client.ConfirmActionAsync(kindCode, instanceCode, actionId, actor, ct)
                : await client.InvalidateActionAsync(kindCode, instanceCode, actionId, actor, ct);

            if (result.Denied)
            {
                Console.Error.WriteLine($"denied: {result.Message}");
                return 1;
            }
            Console.WriteLine($"{result.Status}: {result.Message}");
            return 0;

        default:
            Console.Error.WriteLine("usage: lodge actions list|confirm|invalidate <kind> <instance> [action-id] [actor]");
            return 1;
    }
}

static async Task<int> TokensAsync(string[] args, CancellationToken ct)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: lodge tokens create|list|revoke ...");
        return 1;
    }

    using var client = RequireClient();

    switch (args[1])
    {
        case "create":
        {
            string? displayName = null;
            var scopes = new List<string>();
            int? ttlMinutes = null;
            for (var i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--display-name" when i + 1 < args.Length:
                        displayName = args[++i];
                        break;
                    case "--scope" when i + 1 < args.Length:
                        scopes.Add(args[++i]);
                        break;
                    case "--ttl-minutes" when i + 1 < args.Length:
                        ttlMinutes = int.Parse(args[++i]);
                        break;
                }
            }
            if (string.IsNullOrWhiteSpace(displayName))
            {
                Console.Error.WriteLine("usage: lodge tokens create --display-name <name> [--scope <scope>]... [--ttl-minutes <n>]");
                return 1;
            }

            var created = await client.CreateServiceTokenAsync(displayName, scopes, ttlMinutes, ct);
            Console.WriteLine($"Created service token {created.Id} (scopes: {string.Join(",", created.Scopes)})");
            Console.WriteLine("This raw token is shown once — store it now, it cannot be retrieved again:");
            Console.WriteLine(created.Token);
            return 0;
        }

        case "list":
            foreach (var t in await client.ListTokensAsync(ct))
            {
                var status = t.RevokedAt is not null ? "revoked" : "active";
                Console.WriteLine($"{t.Id}\t{t.Kind}\t{t.DisplayName}\t{status}\tscopes={string.Join(",", t.Scopes)}");
            }
            return 0;

        case "revoke":
            if (args.Length < 3 || !Guid.TryParse(args[2], out var tokenId))
            {
                Console.Error.WriteLine("usage: lodge tokens revoke <token-id>");
                return 1;
            }
            await client.RevokeTokenAsync(tokenId, ct);
            Console.WriteLine($"Revoked {tokenId}.");
            return 0;

        default:
            Console.Error.WriteLine("usage: lodge tokens create|list|revoke ...");
            return 1;
    }
}
