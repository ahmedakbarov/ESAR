using System.Linq.Expressions;
using Esar.Application.Common;
using Esar.Domain.Entities;
using Esar.Domain.Enums;

namespace Esar.Application.Abstractions;

public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<T>> ListAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default);
    Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<T> entities, CancellationToken ct = default);
    void Update(T entity);
    void Remove(T entity);
}

public interface IAssetRepository : IRepository<Asset>
{
    Task<Asset?> GetWithDetailsAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<Asset>> SearchAsync(AssetSearchCriteria criteria, CancellationToken ct = default);
    /// <summary>Finds the golden asset already linked to a source record (connector + external id).</summary>
    Task<Asset?> FindBySourceAsync(ConnectorType connector, string externalId, CancellationToken ct = default);
    /// <summary>Executes a hard-identifier lookup for one of the well-known match attributes.</summary>
    Task<Asset?> FindByHardIdentifierAsync(string attribute, string value, CancellationToken ct = default);
    /// <summary>Loads soft-match candidates by normalized hostname, MAC or IP overlap.</summary>
    Task<List<Asset>> FindSoftCandidatesAsync(string? normalizedHostname, IReadOnlyCollection<string> macs,
        IReadOnlyCollection<string> ips, CancellationToken ct = default);
}

public interface IUnitOfWork
{
    IAssetRepository Assets { get; }
    IRepository<AssetSource> AssetSources { get; }
    IRepository<AssetIp> AssetIps { get; }
    IRepository<AssetHistory> AssetHistories { get; }
    IRepository<AssetCompliance> AssetCompliance { get; }
    IRepository<AssetRelationship> Relationships { get; }
    IRepository<CompliancePolicy> CompliancePolicies { get; }
    IRepository<ApprovalRequest> Approvals { get; }
    IRepository<MatchingRule> MatchingRules { get; }
    IRepository<MatchRecord> MatchRecords { get; }
    IRepository<SourcePriority> SourcePriorities { get; }
    IRepository<ConnectorConfig> Connectors { get; }
    IRepository<ConnectorJob> ConnectorJobs { get; }
    IRepository<Incident> Incidents { get; }
    IRepository<Notification> Notifications { get; }
    IRepository<NotificationTemplate> NotificationTemplates { get; }
    IRepository<EscalationRule> EscalationRules { get; }
    IRepository<AuditLog> AuditLogs { get; }
    IRepository<User> Users { get; }
    IRepository<Role> Roles { get; }
    IRepository<Permission> Permissions { get; }
    IRepository<UserRole> UserRoles { get; }
    IRepository<RolePermission> RolePermissions { get; }
    IRepository<Report> Reports { get; }
    IRepository<Setting> Settings { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    /// <summary>Detaches every tracked entity — recovery hatch after a failed SaveChanges so one
    /// poisoned aggregate cannot break subsequent saves that share this scoped context.</summary>
    void ClearChangeTracker();
}
