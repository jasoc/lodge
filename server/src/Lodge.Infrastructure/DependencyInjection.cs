using Lodge.Core.Abstractions;
using Lodge.Infrastructure.Auth;
using Lodge.Infrastructure.Execution;
using Lodge.Infrastructure.Git;
using Lodge.Infrastructure.Persistence;
using Lodge.Infrastructure.Reconciliation;
using Lodge.Infrastructure.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lodge.Infrastructure;

/// <summary>
/// Registers Lodge infrastructure services: EF Core (PostgreSQL), the inventory source
/// (local working tree or GitHub), the capability catalog provider, the unified
/// reconciliation loop, the shell-command runbook executor, token-based auth, and the
/// (mock) secret provider.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddLodgeInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<GitSnapshotOptions>(configuration.GetSection(GitSnapshotOptions.SectionName));
        services.Configure<GitOptions>(configuration.GetSection(GitOptions.SectionName));
        services.Configure<ShellExecutorOptions>(configuration.GetSection(ShellExecutorOptions.SectionName));
        services.Configure<WebhookExecutorOptions>(configuration.GetSection(WebhookExecutorOptions.SectionName));
        services.Configure<DockerExecutorOptions>(configuration.GetSection(DockerExecutorOptions.SectionName));
        services.Configure<PassCliOptions>(configuration.GetSection(PassCliOptions.SectionName));
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
        services.Configure<OidcOptions>(configuration.GetSection(OidcOptions.SectionName));

        var connectionString = LodgeConnectionString.Build(configuration);
        services.AddDbContext<LodgeDbContext>(options => options.UseNpgsql(connectionString));

        // The only Local-vs-GitHub difference is where inventory YAML is read from.
        // One reconciliation loop drives both modes.
        var gitProvider = configuration[$"{GitOptions.SectionName}:Provider"] ?? "Local";
        if (string.Equals(gitProvider, "GitHub", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<IGitHubClient, GitHubApiClient>();
            services.AddScoped<IInventorySource, GitHubInventorySource>();
        }
        else
        {
            services.AddScoped<IInventorySource, LocalInventorySource>();
        }

        // The unified reconciliation loop: one timer, one cycle, single-flighted so the
        // timer and manual "Run now" callers coalesce. Interval/enabled live in the DB
        // (settings table) and are re-read every iteration.
        services.AddSingleton<ReconciliationCoordinator>();
        services.AddHostedService<ReconciliationLoopService>();
        services.AddScoped<ReconciliationRunner>();
        services.AddScoped<ActionsQueryService>();
        services.AddScoped<SettingsService>();

        // Shared by the catalog provider (stamps each docker.build action with its playbook
        // folder's fingerprint) and the Docker executor (re-checks it before building).
        services.AddSingleton<PlaybookContextResolver>();
        services.AddSingleton<ICapabilityCatalogProvider, FileCapabilityCatalogProvider>();
        services.AddScoped<ActionExecutionService>();

        // Auth + RBAC seams. Personal and service bearer tokens both authenticate through
        // ApiTokenService/LodgeBearerAuthenticationHandler; TokenCurrentUserAccessor reads
        // the resulting identity per-request. An OidcCurrentUserAccessor-equivalent is a
        // drop-in later — it only changes how a token gets minted, not how requests
        // authenticate. FilePermissionResolver stays the RBAC seam unchanged.
        services.AddHttpContextAccessor();
        services.AddScoped<ApiTokenService>();
        services.AddScoped<ICurrentUserAccessor, TokenCurrentUserAccessor>();
        services.AddSingleton<IPermissionResolver, FilePermissionResolver>();

        // OIDC (Auth:Mode = Oidc): the server performs the Authorization Code + PKCE
        // flow itself — the CLI and SPA only ever talk to Lodge's own
        // /api/v1/auth/oidc/* endpoints, never the IdP directly. Singleton so the
        // discovery-document cache inside OidcClient survives across requests, same
        // rationale as the webhook executor's named-client pattern below.
        services.AddSingleton<PendingOidcLoginStore>();
        services.AddHttpClient("oidc");
        services.AddSingleton(sp => new OidcClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("oidc"),
            sp.GetRequiredService<IOptions<OidcOptions>>()));

        // Runbook execution: each action declares its executor explicitly
        // (ExecutorKind: Shell/Webhook/Docker — see Lodge.Core.Domain.Enums.ExecutorKind),
        // dispatched by CompositeRunbookExecutor, the one registered as IRunbookExecutor.
        // All concrete executors stay singletons so run state persists across requests for
        // status polling. MockRunbookExecutor remains available for demos and tests but
        // isn't registered by default.
        services.AddSingleton<ShellCommandRunbookExecutor>();
        services.AddSingleton<DockerRunbookExecutor>();
        // AddHttpClient<T>() registers T as transient, which would drop
        // WebhookRunbookExecutor's in-flight-run state on every injection — build it as a
        // singleton over a named client from IHttpClientFactory instead.
        services.AddHttpClient("webhook");
        services.AddSingleton(sp => new WebhookRunbookExecutor(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("webhook"),
            sp.GetRequiredService<IOptions<WebhookExecutorOptions>>()));
        // Registered under both its own type and the interface, resolving to the same
        // instance — the execution-status callback endpoint needs the concrete type to
        // reach TryReportWebhookStatus, which isn't part of the IRunbookExecutor contract.
        services.AddSingleton<CompositeRunbookExecutor>();
        services.AddSingleton<IRunbookExecutor>(sp => sp.GetRequiredService<CompositeRunbookExecutor>());
        services.AddSingleton<IRunbookLogReader>(sp => sp.GetRequiredService<CompositeRunbookExecutor>());

        // Secret provider, chosen by Secrets:Provider (mirrors Git:Provider above): "Env"
        // (default) resolves from environment variables on the server process — the
        // OSS-friendly default needing no company-specific vault; "PassCli" shells out to
        // the pass-cli binary (Proton Pass CLI) to resolve real credentials; "Mock" is for
        // tests/demos.
        var secretsProvider = configuration["Secrets:Provider"] ?? "Env";
        if (string.Equals(secretsProvider, "PassCli", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IProcessRunner, SystemProcessRunner>();
            services.AddSingleton<ISecretProvider, PassCliSecretProvider>();
        }
        else if (string.Equals(secretsProvider, "Mock", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ISecretProvider, MockSecretProvider>();
        }
        else
        {
            services.AddSingleton<ISecretProvider, EnvSecretProvider>();
        }

        return services;
    }
}
