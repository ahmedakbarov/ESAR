using Esar.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Esar.Infrastructure.Persistence;

public class EsarDbContext : DbContext
{
    public EsarDbContext(DbContextOptions<EsarDbContext> options) : base(options) { }

    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AssetSource> AssetSources => Set<AssetSource>();
    public DbSet<AssetIp> AssetIps => Set<AssetIp>();
    public DbSet<AssetTag> AssetTags => Set<AssetTag>();
    public DbSet<AssetHistory> AssetHistories => Set<AssetHistory>();
    public DbSet<AssetSoftware> AssetSoftware => Set<AssetSoftware>();
    public DbSet<AssetCompliance> AssetCompliance => Set<AssetCompliance>();
    public DbSet<AssetEvent> AssetEvents => Set<AssetEvent>();
    public DbSet<AssetRisk> AssetRisks => Set<AssetRisk>();
    public DbSet<ConnectorConfig> Connectors => Set<ConnectorConfig>();
    public DbSet<ConnectorJob> ConnectorJobs => Set<ConnectorJob>();
    public DbSet<SourcePriority> SourcePriorities => Set<SourcePriority>();
    public DbSet<MatchingRule> MatchingRules => Set<MatchingRule>();
    public DbSet<MatchRecord> MatchRecords => Set<MatchRecord>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationTemplate> NotificationTemplates => Set<NotificationTemplate>();
    public DbSet<EscalationRule> EscalationRules => Set<EscalationRule>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AssetRelationship> AssetRelationships => Set<AssetRelationship>();
    public DbSet<CompliancePolicy> CompliancePolicies => Set<CompliancePolicy>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Asset>(e =>
        {
            e.ToTable("assets");
            e.HasIndex(x => x.NormalizedHostname);
            e.HasIndex(x => x.SerialNumber);
            e.HasIndex(x => x.BiosUuid);
            e.HasIndex(x => x.CloudResourceId);
            e.HasIndex(x => new { x.Status, x.IsDeleted });
            e.HasIndex(x => x.LastSeen);
            e.Property(x => x.Hostname).HasMaxLength(255).IsRequired();
            e.Property(x => x.NormalizedHostname).HasMaxLength(255);
            e.Property(x => x.ComplianceScore).HasPrecision(5, 2);
            e.Property(x => x.DataQualityScore).HasPrecision(5, 2);
            e.Property(x => x.AttributeSourcesJson).HasColumnType("jsonb");
            e.Property(x => x.DataQualityIssuesJson).HasColumnType("jsonb");
            e.HasQueryFilter(x => !x.IsDeleted);
            e.HasMany(x => x.Sources).WithOne(x => x.Asset!).HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.IpAddresses).WithOne(x => x.Asset!).HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Tags).WithOne(x => x.Asset!).HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Histories).WithOne(x => x.Asset!).HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Software).WithOne(x => x.Asset!).HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.ComplianceRecords).WithOne(x => x.Asset!).HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Events).WithOne(x => x.Asset!).HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Risk).WithOne(x => x.Asset!).HasForeignKey<AssetRisk>(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AssetSource>(e =>
        {
            e.ToTable("asset_sources");
            e.HasIndex(x => new { x.ConnectorType, x.ExternalId }).IsUnique();
            e.Property(x => x.ExternalId).HasMaxLength(512).IsRequired();
            e.Property(x => x.RawData).HasColumnType("jsonb");
        });

        b.Entity<AssetIp>(e =>
        {
            e.ToTable("asset_ips");
            e.HasIndex(x => x.IpAddress);
            e.HasIndex(x => x.MacAddress);
            e.Property(x => x.IpAddress).HasMaxLength(64).IsRequired();
            e.Property(x => x.MacAddress).HasMaxLength(17);
        });

        b.Entity<AssetTag>(e =>
        {
            e.ToTable("asset_tags");
            e.HasIndex(x => new { x.AssetId, x.Key }).IsUnique();
            e.Property(x => x.Key).HasMaxLength(128).IsRequired();
            e.Property(x => x.Value).HasMaxLength(1024);
        });

        b.Entity<AssetHistory>(e =>
        {
            e.ToTable("asset_history");
            e.HasIndex(x => new { x.AssetId, x.ChangedAt });
            e.Property(x => x.FieldName).HasMaxLength(128);
        });

        b.Entity<AssetSoftware>(e =>
        {
            e.ToTable("asset_software");
            e.HasIndex(x => new { x.AssetId, x.Name, x.Source });
            e.Property(x => x.Name).HasMaxLength(512).IsRequired();
        });

        b.Entity<AssetCompliance>(e =>
        {
            e.ToTable("asset_compliance");
            e.HasIndex(x => new { x.AssetId, x.Control }).IsUnique();
        });

        b.Entity<AssetEvent>(e =>
        {
            e.ToTable("asset_events");
            e.HasIndex(x => new { x.AssetId, x.OccurredAt });
            e.Property(x => x.Payload).HasColumnType("jsonb");
        });

        b.Entity<AssetRisk>(e =>
        {
            e.ToTable("asset_risk");
            e.HasIndex(x => x.AssetId).IsUnique();
            e.Property(x => x.RiskScore).HasPrecision(6, 2);
            e.Property(x => x.ExposureScore).HasPrecision(6, 2);
        });

        b.Entity<ConnectorConfig>(e =>
        {
            e.ToTable("connectors");
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.SettingsJson).HasColumnType("jsonb");
            e.HasMany(x => x.Jobs).WithOne(x => x.Connector!).HasForeignKey(x => x.ConnectorId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ConnectorJob>(e =>
        {
            e.ToTable("connector_jobs");
            e.HasIndex(x => new { x.ConnectorId, x.CreatedAt });
            e.Property(x => x.Log).HasColumnType("jsonb");
        });

        b.Entity<SourcePriority>(e =>
        {
            e.ToTable("source_priorities");
            e.HasIndex(x => new { x.ConnectorType, x.Attribute }).IsUnique();
            e.Property(x => x.Attribute).HasMaxLength(128);
        });

        b.Entity<MatchingRule>(e =>
        {
            e.ToTable("matching_rules");
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Attribute).HasMaxLength(128).IsRequired();
            e.Property(x => x.Weight).HasPrecision(5, 4);
        });

        b.Entity<MatchRecord>(e =>
        {
            e.ToTable("match_records");
            e.HasIndex(x => new { x.SourceConnector, x.ExternalId });
            e.HasIndex(x => x.Decision);
            e.Property(x => x.ConfidenceScore).HasPrecision(5, 4);
            e.Property(x => x.ExplanationJson).HasColumnType("jsonb");
            e.Property(x => x.CandidateJson).HasColumnType("jsonb");
            e.HasOne(x => x.MatchedAsset).WithMany().HasForeignKey(x => x.MatchedAssetId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasIndex(x => x.Username).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
            // Prevents two ESAR accounts from silently claiming the same federated identity.
            // Partial (WHERE ExternalObjectId IS NOT NULL) so it's a no-op for Local accounts
            // and for federated placeholders an admin pre-created but that haven't logged in yet.
            e.HasIndex(x => new { x.AuthProvider, x.ExternalObjectId }).IsUnique()
                .HasFilter("\"ExternalObjectId\" IS NOT NULL");
            e.Property(x => x.Username).HasMaxLength(128).IsRequired();
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
        });

        b.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
        });

        b.Entity<Permission>(e =>
        {
            e.ToTable("permissions");
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(128).IsRequired();
        });

        b.Entity<UserRole>(e =>
        {
            e.ToTable("user_roles");
            e.HasKey(x => new { x.UserId, x.RoleId });
            e.HasOne(x => x.User).WithMany(x => x!.UserRoles).HasForeignKey(x => x.UserId);
            e.HasOne(x => x.Role).WithMany(x => x!.UserRoles).HasForeignKey(x => x.RoleId);
        });

        b.Entity<RolePermission>(e =>
        {
            e.ToTable("role_permissions");
            e.HasKey(x => new { x.RoleId, x.PermissionId });
            e.HasOne(x => x.Role).WithMany(x => x!.RolePermissions).HasForeignKey(x => x.RoleId);
            e.HasOne(x => x.Permission).WithMany(x => x!.RolePermissions).HasForeignKey(x => x.PermissionId);
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.Property(x => x.Details).HasColumnType("jsonb");
        });

        b.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.HasIndex(x => new { x.Status, x.CreatedAt });
        });

        b.Entity<NotificationTemplate>(e =>
        {
            e.ToTable("notification_templates");
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
        });

        b.Entity<EscalationRule>(e => e.ToTable("escalation_rules"));

        b.Entity<Incident>(e =>
        {
            e.ToTable("incidents");
            e.HasIndex(x => new { x.DedupKey, x.Status });
            e.HasIndex(x => new { x.Status, x.Severity });
            e.Property(x => x.DedupKey).HasMaxLength(256).IsRequired();
            e.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Report>(e =>
        {
            e.ToTable("reports");
            e.Property(x => x.ParametersJson).HasColumnType("jsonb");
        });

        b.Entity<Setting>(e =>
        {
            e.ToTable("settings");
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Key).HasMaxLength(256).IsRequired();
        });

        b.Entity<AssetRelationship>(e =>
        {
            e.ToTable("asset_relationships");
            e.HasIndex(x => new { x.SourceAssetId, x.TargetAssetId, x.Type }).IsUnique();
            e.HasIndex(x => x.TargetAssetId);
            e.HasOne(x => x.SourceAsset).WithMany().HasForeignKey(x => x.SourceAssetId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.TargetAsset).WithMany().HasForeignKey(x => x.TargetAssetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CompliancePolicy>(e =>
        {
            e.ToTable("compliance_policies");
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.AppliesToAssetTypesJson).HasColumnType("jsonb");
            e.Property(x => x.AppliesToEnvironmentsJson).HasColumnType("jsonb");
            e.Property(x => x.AppliesToConnectorsJson).HasColumnType("jsonb");
            e.Property(x => x.AppliesToTagsJson).HasColumnType("jsonb");
            e.Property(x => x.AppliesToHostnamePatternsJson).HasColumnType("jsonb");
            e.Property(x => x.AppliesToIpRangesJson).HasColumnType("jsonb");
            e.Property(x => x.AppliesToSubscriptionsJson).HasColumnType("jsonb");
            e.Property(x => x.RequiredControlsJson).HasColumnType("jsonb");
            e.Property(x => x.MandatoryControlsJson).HasColumnType("jsonb");
        });

        b.Entity<ApprovalRequest>(e =>
        {
            e.ToTable("approval_requests");
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
            e.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.SetNull);
        });

        // Store all enums as strings for readability and safe reordering.
        foreach (var entityType in b.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (type.IsEnum) property.SetProviderClrType(typeof(string));
            }
        }
    }
}
