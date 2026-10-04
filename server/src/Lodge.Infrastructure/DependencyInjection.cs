using Lodge.Core.Abstractions;
using Lodge.Core.Catalog;
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
        services.Configure<HttpExecutorOptions>(configuration.GetSection(HttpExecutorOptions.SectionName));
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

        // Shared by the catalog provider (stamps each container.build action with its playbook
        // folder's fingerprint) and the Docker executor (re-checks it before building).
        services.AddSingleton(sp => new PlaybookContextResolver(sp.GetRequiredService<IOptions<GitSnapshotOptions>>().Value.RepoRoot));
        services.AddSingleton<ICapabilityCatalogProvider, FileCapabilityCatalogProvider>();
        services.AddScoped<ActionExecutionService>();

        // Auth. Personal and service bearer tokens both authenticate through
        // ApiTokenService/LodgeBearerAuthenticationHandler; TokenCurrentUserAccessor reads
        // the resulting identity per-request. Users and their groups live in UserDirectory
        // (mirrored from the IdP with OIDC, edited in Lodge locally); an action's
        // `requires` is checked against them.
        services.AddHttpContextAccessor();
        services.AddScoped<ApiTokenService>();
        services.AddScoped<UserDirectory>();
        services.AddScoped<ICurrentUserAccessor, TokenCurrentUserAccessor>();

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

        // Execution: each action declares its executor explicitly (container | http — see
        // Lodge.Core.Domain.Enums.ExecutorKind), dispatched by CompositeRunbookExecutor, the
        // one registered as IRunbookExecutor. Concrete executors are singletons so run state
        // persists across requests for status polling; the HTTP one over a named client
        // (AddHttpClient<T> would make it transient and drop that state).
        // The container executor's runtime: Docker, the only one so far. Another runtime
        // (Kubernetes) is another IImageBuilder + IContainerRunner pair, chosen here.
        services.AddSingleton<DockerCli>();
        services.AddSingleton<IImageBuilder, DockerImageBuilder>();
        services.AddSingleton<IContainerRunner, DockerContainerRunner>();
        services.AddSingleton<ContainerRunbookExecutor>();
        services.AddHttpClient("http-executor");
        services.AddSingleton(sp => new HttpRunbookExecutor(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("http-executor"),
            sp.GetRequiredService<IOptions<HttpExecutorOptions>>()));
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
