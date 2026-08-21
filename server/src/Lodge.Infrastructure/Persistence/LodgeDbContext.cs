using Lodge.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using ActionEntity = Lodge.Core.Domain.Entities.Action;

namespace Lodge.Infrastructure.Persistence;

/// <summary>
/// EF Core context for Lodge. All kind-scoped tables carry <c>kind_code</c>;
/// tables and columns use snake_case to match the SSOT vocabulary. Schema is owned by
/// the SQL files under <c>/migrations</c> (applied by Badgie.Migrator) — this mapping
/// is kept in sync with them by hand; there are no EF migrations.
/// </summary>
public class LodgeDbContext : DbContext
{
    public LodgeDbContext(DbContextOptions<LodgeDbContext> options) : base(options)
    {
    }

    public DbSet<Kind> Kinds => Set<Kind>();
    public DbSet<Instance> Instances => Set<Instance>();
    public DbSet<RegistryRevision> RegistryRevisions => Set<RegistryRevision>();
    public DbSet<ActionEntity> Actions => Set<ActionEntity>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<RegistrySyncState> RegistrySyncStates => Set<RegistrySyncState>();
    public DbSet<SyncCycle> SyncCycles => Set<SyncCycle>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Kind>(e =>
        {
            e.ToTable("kinds");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(64);
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(256).IsRequired();
            e.Property(x => x.Enabled).HasColumnName("enabled");
        });

        b.Entity<Instance>(e =>
        {
            e.ToTable("instances");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.KindCode).HasColumnName("kind_code").HasMaxLength(64).IsRequired();
            e.Property(x => x.InstanceCode).HasColumnName("instance_code").HasMaxLength(128).IsRequired();
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(256);
            e.Property(x => x.Generation).HasColumnName("generation");
            e.Property(x => x.Region).HasColumnName("region").HasMaxLength(64);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => new { x.KindCode, x.InstanceCode }).IsUnique();
            e.HasOne(x => x.Kind).WithMany(p => p.Instances)
                .HasForeignKey(x => x.KindCode).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<RegistryRevision>(e =>
        {
            e.ToTable("registry_revisions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.InstanceId).HasColumnName("instance_id");
            e.Property(x => x.GitRef).HasColumnName("git_ref").HasMaxLength(128);
            e.Property(x => x.YamlContent).HasColumnName("yaml_content");
            e.Property(x => x.ContentHash).HasColumnName("content_hash").HasMaxLength(128);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne(x => x.Instance).WithMany(t => t.Revisions)
                .HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ActionEntity>(e =>
        {
            e.ToTable("actions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.InstanceId).HasColumnName("instance_id");
            e.Property(x => x.CapabilityCode).HasColumnName("capability_code").HasMaxLength(128).IsRequired();
            e.Property(x => x.SignalPath).HasColumnName("signal_path").HasMaxLength(512).IsRequired();
            e.Property(x => x.ItemKey).HasColumnName("item_key").HasMaxLength(512);
            e.Property(x => x.ActionKey).HasColumnName("action_key").HasMaxLength(256).IsRequired();
            e.Property(x => x.RunbookRef).HasColumnName("runbook_ref").HasMaxLength(256).IsRequired();
            e.Property(x => x.Trigger).HasColumnName("trigger").HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Label).HasColumnName("label").HasMaxLength(256);
            e.Property(x => x.Policy).HasColumnName("policy").HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.DesiredValueJson).HasColumnName("desired_value_json");
            e.Property(x => x.ResolvedInputsJson).HasColumnName("resolved_inputs_json");
            e.Property(x => x.PendingPromptsJson).HasColumnName("pending_prompts_json");
            e.Property(x => x.ExecutionRef).HasColumnName("execution_ref").HasMaxLength(256);
            e.Property(x => x.Synthetic).HasColumnName("synthetic");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.InvalidatedAt).HasColumnName("invalidated_at");
            e.Property(x => x.InvalidatedBy).HasColumnName("invalidated_by").HasMaxLength(128);
            e.HasOne(x => x.Instance).WithMany(t => t.Actions)
                .HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Cascade);
            // History projection: latest SUCCEEDED per (instance, signal, item, action key).
            e.HasIndex(x => new { x.InstanceId, x.SignalPath, x.ActionKey, x.CreatedAt })
                .HasDatabaseName("ix_actions_history");
            // One live row per identity — mirrored as a partial unique index in
            // migrations/004-actions.sql + migrations/009-actions-action-key.sql
            // (EF never creates it; documented here only).
        });

        b.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.InstanceId).HasColumnName("instance_id");
            e.Property(x => x.KindCode).HasColumnName("kind_code").HasMaxLength(64);
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(128);
            e.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(128);
            e.Property(x => x.PayloadJson).HasColumnName("payload_json");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.InstanceId);
        });

        b.Entity<RegistrySyncState>(e =>
        {
            e.ToTable("registry_sync_state");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.KindCode).HasColumnName("kind_code").HasMaxLength(64).IsRequired();
            e.Property(x => x.Branch).HasColumnName("branch").HasMaxLength(128).IsRequired();
            e.Property(x => x.LastSeenSha).HasColumnName("last_seen_sha").HasMaxLength(128);
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => new { x.KindCode, x.Branch }).IsUnique();
        });

        b.Entity<SyncCycle>(e =>
        {
            e.ToTable("sync_cycles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.TriggeredBy).HasColumnName("triggered_by").HasMaxLength(32).IsRequired();
            e.Property(x => x.Success).HasColumnName("success");
            e.Property(x => x.KindsChecked).HasColumnName("kinds_checked");
            e.Property(x => x.InstancesReconciled).HasColumnName("instances_reconciled");
            e.Property(x => x.DriftCount).HasColumnName("drift_count");
            e.Property(x => x.Error).HasColumnName("error");
            e.Property(x => x.MessagesJson).HasColumnName("messages_json");
            e.Property(x => x.ValidationErrorsJson).HasColumnName("validation_errors_json");
            e.HasIndex(x => x.StartedAt);
        });

        b.Entity<Setting>(e =>
        {
            e.ToTable("settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(128);
            e.Property(x => x.Value).HasColumnName("value").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        b.Entity<ApiToken>(e =>
        {
            e.ToTable("api_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(128).IsRequired();
            e.Property(x => x.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(256);
            e.Property(x => x.SubjectId).HasColumnName("subject_id").HasMaxLength(128).IsRequired();
            e.Property(x => x.GroupsJson).HasColumnName("groups_json");
            e.Property(x => x.IsAdmin).HasColumnName("is_admin");
            e.Property(x => x.ScopesJson).HasColumnName("scopes_json");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.SubjectId);
        });
    }
}
