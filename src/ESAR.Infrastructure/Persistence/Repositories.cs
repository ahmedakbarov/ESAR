using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Esar.Application.Abstractions;
using Esar.Application.Common;
using Esar.Application.Contracts;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Esar.Infrastructure.Persistence;

public class GenericRepository<T> : IRepository<T> where T : class
{
    protected readonly EsarDbContext Db;
    public GenericRepository(EsarDbContext db) => Db = db;

    public Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Db.Set<T>().FindAsync(new object[] { id }, ct).AsTask();

    public Task<List<T>> ListAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default)
        => (predicate is null ? Db.Set<T>() : Db.Set<T>().Where(predicate)).ToListAsync(ct);

    public Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => Db.Set<T>().FirstOrDefaultAsync(predicate, ct);

    public Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default)
        => predicate is null ? Db.Set<T>().CountAsync(ct) : Db.Set<T>().CountAsync(predicate, ct);

    public async Task<PagedResult<T>> PageAsync(Func<IQueryable<T>, IQueryable<T>>? shape,
        int page, int pageSize, CancellationToken ct = default)
    {
        IQueryable<T> query = Db.Set<T>().AsNoTracking();
        if (shape is not null) query = shape(query);
        var total = await query.LongCountAsync(ct);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<T> { Items = items, Page = page, PageSize = pageSize, TotalCount = total };
    }

    public async Task AddAsync(T entity, CancellationToken ct = default) => await Db.Set<T>().AddAsync(entity, ct);
    public async Task AddRangeAsync(IEnumerable<T> entities, CancellationToken ct = default)
        => await Db.Set<T>().AddRangeAsync(entities, ct);
    /// <summary>
    /// State-aware update. Entities loaded from this scoped context are already tracked, so
    /// SaveChanges/DetectChanges computes real scalar changes and marks brand-new children
    /// reachable via navigations (discovered AssetIp/AssetTag/AssetSource rows with
    /// client-generated GUIDs) as Added. DbSet.Update walks the whole graph and wrongly flags
    /// those new children as Modified → UPDATE affects 0 rows → DbUpdateConcurrencyException
    /// that breaks the entire connector run. Only genuinely detached entities need attaching.
    /// </summary>
    public void Update(T entity)
    {
        var entry = Db.Entry(entity);
        if (entry.State == EntityState.Detached) entry.State = EntityState.Modified;
    }
    public void Remove(T entity) => Db.Set<T>().Remove(entity);
}

public class AssetRepository : GenericRepository<Asset>, IAssetRepository
{
    public AssetRepository(EsarDbContext db) : base(db) { }

    private IQueryable<Asset> Detailed => Db.Assets
        .Include(a => a.Sources)
        .Include(a => a.Identifiers)
        .Include(a => a.IpAddresses)
        .Include(a => a.Tags)
        .Include(a => a.ComplianceRecords)
        .Include(a => a.Software)
        .Include(a => a.GroupMemberships)
        .Include(a => a.Risk);

    public Task<Asset?> GetWithDetailsAsync(Guid id, CancellationToken ct = default)
        => Detailed.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<PagedResult<Asset>> SearchAsync(AssetSearchCriteria c, CancellationToken ct = default)
    {
        var query = Db.Assets
            .Include(a => a.Sources)
            .Include(a => a.IpAddresses)
            .AsQueryable();
        if (c.IncludeDeleted) query = query.IgnoreQueryFilters();

        if (!string.IsNullOrWhiteSpace(c.Search))
        {
            if (c.UseRegex)
            {
                // Npgsql translates Regex.IsMatch to the PostgreSQL ~ operator.
                var pattern = c.Search.Trim();
                // ReDoS guard: PostgreSQL's regex engine can backtrack catastrophically on
                // hostile patterns (e.g. "(a+)+$"). Cap the pattern length and bound the query
                // with a short command timeout so a malicious search cannot pin a DB core.
                if (pattern.Length > 200)
                    throw new ArgumentException("Regex pattern is too long (max 200 characters).");
                Db.Database.SetCommandTimeout(TimeSpan.FromSeconds(5));
                query = query.Where(a =>
                    Regex.IsMatch(a.Hostname, pattern) ||
                    (a.Fqdn != null && Regex.IsMatch(a.Fqdn, pattern)));
            }
            else
            {
                var term = $"%{c.Search.Trim()}%";
                query = query.Where(a =>
                    EF.Functions.ILike(a.Hostname, term) ||
                    EF.Functions.ILike(a.Fqdn ?? "", term) ||
                    EF.Functions.ILike(a.OwnerName ?? "", term) ||
                    a.IpAddresses.Any(ip => ip.IsActive && EF.Functions.ILike(ip.IpAddress, term)) ||
                    EF.Functions.ILike(a.SerialNumber ?? "", term));
            }
        }
        if (TryEnum<AssetType>(c.AssetType, out var type)) query = query.Where(a => a.AssetType == type);
        if (TryEnum<AssetStatus>(c.Status, out var status)) query = query.Where(a => a.Status == status);
        if (TryEnum<LifecycleStatus>(c.LifecycleStatus, out var lifecycle))
            query = query.Where(a => a.LifecycleStatus == lifecycle);
        if (TryEnum<EnvironmentType>(c.Environment, out var env)) query = query.Where(a => a.Environment == env);
        if (TryEnum<CriticalityLevel>(c.Criticality, out var crit)) query = query.Where(a => a.Criticality == crit);
        if (TryEnum<ComplianceStatus>(c.ComplianceStatus, out var comp)) query = query.Where(a => a.ComplianceStatus == comp);
        if (!string.IsNullOrWhiteSpace(c.BusinessUnit)) query = query.Where(a => a.BusinessUnit == c.BusinessUnit);
        if (!string.IsNullOrWhiteSpace(c.Owner))
            query = query.Where(a => EF.Functions.ILike(a.OwnerName ?? "", $"%{c.Owner.Trim()}%"));
        if (TryEnum<ConnectorType>(c.Source, out var source))
            query = query.Where(a => a.Sources.Any(s => s.ConnectorType == source));
        if (!string.IsNullOrWhiteSpace(c.Ip))
            query = query.Where(a => a.IpAddresses.Any(i => i.IsActive && i.IpAddress == c.Ip.Trim()));
        if (!string.IsNullOrWhiteSpace(c.Mac))
        {
            var mac = c.Mac.Trim().ToLowerInvariant();
            query = query.Where(a => a.IpAddresses.Any(i => i.IsActive && i.MacAddress == mac));
        }
        if (!string.IsNullOrWhiteSpace(c.Os))
            query = query.Where(a => EF.Functions.ILike(a.OperatingSystem ?? "", $"%{c.Os.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(c.Software))
            query = query.Where(a => a.Software.Any(s => EF.Functions.ILike(s.Name, $"%{c.Software.Trim()}%")));
        if (!string.IsNullOrWhiteSpace(c.CloudProvider))
            query = query.Where(a => a.CloudProvider == c.CloudProvider);
        if (c.MaxDataQualityScore is { } maxDq)
            query = query.Where(a => a.DataQualityScore <= maxDq);
        if (c.PolicyExempt is { } policyExempt)
            query = query.Where(a => a.PolicyExempt == policyExempt);
        if (!string.IsNullOrWhiteSpace(c.TagKey))
        {
            query = string.IsNullOrWhiteSpace(c.TagValue)
                ? query.Where(a => a.Tags.Any(t => t.Key == c.TagKey))
                : query.Where(a => a.Tags.Any(t => t.Key == c.TagKey && t.Value == c.TagValue));
        }

        // Excel-style multi-select column filters: OR inside one column, AND across columns.
        var types = ParseEnums<AssetType>(c.AssetTypes);
        if (types.Count > 0) query = query.Where(a => types.Contains(a.AssetType));
        var statuses = ParseEnums<AssetStatus>(c.Statuses);
        if (statuses.Count > 0) query = query.Where(a => statuses.Contains(a.Status));
        var lifecycles = ParseEnums<LifecycleStatus>(c.LifecycleStatuses);
        if (lifecycles.Count > 0) query = query.Where(a => lifecycles.Contains(a.LifecycleStatus));
        var environments = ParseEnums<EnvironmentType>(c.Environments);
        if (environments.Count > 0) query = query.Where(a => environments.Contains(a.Environment));
        var criticalities = ParseEnums<CriticalityLevel>(c.Criticalities);
        if (criticalities.Count > 0) query = query.Where(a => criticalities.Contains(a.Criticality));
        var compliances = ParseEnums<ComplianceStatus>(c.ComplianceStatuses);
        if (compliances.Count > 0) query = query.Where(a => compliances.Contains(a.ComplianceStatus));
        var sources = ParseEnums<ConnectorType>(c.Sources);
        if (sources.Count > 0) query = query.Where(a => a.Sources.Any(s => sources.Contains(s.ConnectorType)));
        if (c.OsNames is { Count: > 0 })
        {
            var osNames = c.OsNames.Where(o => !string.IsNullOrWhiteSpace(o)).ToList();
            if (osNames.Count > 0) query = query.Where(a => a.OperatingSystem != null && osNames.Contains(a.OperatingSystem));
        }
        if (c.BusinessUnits is { Count: > 0 })
        {
            var units = c.BusinessUnits.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
            if (units.Count > 0) query = query.Where(a => a.BusinessUnit != null && units.Contains(a.BusinessUnit));
        }

        query = ApplySort(query, c.SortBy, c.SortDescending);

        var total = await query.LongCountAsync(ct);
        var page = Math.Max(1, c.Page);
        var pageSize = Math.Clamp(c.PageSize, 1, 500);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync(ct);

        return new PagedResult<Asset> { Items = items, Page = page, PageSize = pageSize, TotalCount = total };
    }

    public Task<Asset?> FindBySourceAsync(ConnectorType connector, string externalId, CancellationToken ct = default)
        => Detailed.IgnoreQueryFilters()
            .Where(a => !a.IsDeleted && a.MergedIntoAssetId == null)
            .FirstOrDefaultAsync(a => a.Sources.Any(s => s.ConnectorType == connector && s.ExternalId == externalId), ct);

    public Task<Asset?> FindDeletedBySourceAsync(ConnectorType connector, string externalId, CancellationToken ct = default)
        => Detailed.IgnoreQueryFilters()
            .Where(a => a.IsDeleted && a.MergedIntoAssetId == null)
            .FirstOrDefaultAsync(a => a.Sources.Any(s => s.ConnectorType == connector && s.ExternalId == externalId), ct);

    public async Task<Asset?> FindByHardIdentifierAsync(string attribute, string value,
        CancellationToken ct = default)
        => (await FindHardIdentifierCandidatesAsync(attribute, value, ct)).FirstOrDefault();

    public Task<List<Asset>> FindHardIdentifierCandidatesAsync(string attribute, string value,
        CancellationToken ct = default)
    {
        var q = Detailed.Where(a => a.MergedIntoAssetId == null);
        var persisted = q.Where(a => a.Identifiers.Any(identifier =>
            identifier.IsActive && identifier.Namespace == attribute && identifier.NormalizedValue == value));
        return attribute switch
        {
            MatchAttributes.AzureResourceId => q.Where(a =>
                a.CloudResourceId == value ||
                a.Identifiers.Any(i => i.IsActive && i.Namespace == attribute && i.NormalizedValue == value) ||
                a.Sources.Any(s => s.ConnectorType == ConnectorType.Azure && s.ExternalId == value))
                .OrderBy(a => a.Id).Take(2).AsSplitQuery().ToListAsync(ct),
            MatchAttributes.AwsInstanceId => q.Where(a =>
                a.CloudResourceId == value ||
                a.Identifiers.Any(i => i.IsActive && i.Namespace == attribute && i.NormalizedValue == value) ||
                a.Sources.Any(s => s.ConnectorType == ConnectorType.Aws && s.ExternalId == value))
                .OrderBy(a => a.Id).Take(2).AsSplitQuery().ToListAsync(ct),
            MatchAttributes.VmwareUuid => q.Where(a =>
                a.BiosUuid == value ||
                a.Identifiers.Any(i => i.IsActive && i.Namespace == attribute && i.NormalizedValue == value) ||
                a.Sources.Any(s => s.ConnectorType == ConnectorType.VmwareVCenter && s.ExternalId == value))
                .OrderBy(a => a.Id).Take(2).AsSplitQuery().ToListAsync(ct),
            MatchAttributes.BiosUuid => q.Where(a => a.BiosUuid == value ||
                    a.Identifiers.Any(i => i.IsActive && i.Namespace == attribute && i.NormalizedValue == value))
                .OrderBy(a => a.Id).Take(2).AsSplitQuery().ToListAsync(ct),
            MatchAttributes.SerialNumber => q.Where(a => a.SerialNumber == value ||
                    a.Identifiers.Any(i => i.IsActive && i.Namespace == attribute && i.NormalizedValue == value))
                .OrderBy(a => a.Id).Take(2).AsSplitQuery().ToListAsync(ct),
            MatchAttributes.ObjectGuid => persisted.OrderBy(a => a.Id).Take(2).AsSplitQuery().ToListAsync(ct),
            MatchAttributes.EndpointId => persisted.OrderBy(a => a.Id).Take(2).AsSplitQuery().ToListAsync(ct),
            MatchAttributes.AdComputerObjectGuid or MatchAttributes.EntraDeviceId or MatchAttributes.AzureVmId or
                MatchAttributes.DefenderMachineId or MatchAttributes.CrowdStrikeDeviceId or
                MatchAttributes.SentinelOneAgentId or MatchAttributes.CortexEndpointId =>
                persisted.OrderBy(a => a.Id).Take(2).AsSplitQuery().ToListAsync(ct),
            _ => persisted.OrderBy(a => a.Id).Take(2).AsSplitQuery().ToListAsync(ct)
        };
    }

    public async Task<List<Asset>> FindSoftCandidatesAsync(string? normalizedHostname,
        IReadOnlyCollection<string> macs, IReadOnlyCollection<string> ips, DateTime networkEvidenceCutoff,
        CancellationToken ct = default)
    {
        var macList = macs.ToList();
        var ipList = ips.ToList();
        var hasHostname = !string.IsNullOrWhiteSpace(normalizedHostname);
        if (!hasHostname && macList.Count == 0 && ipList.Count == 0) return new List<Asset>();

        return await Detailed
            .Where(a => a.MergedIntoAssetId == null)
            .Where(a =>
                (hasHostname && a.NormalizedHostname == normalizedHostname) ||
                (macList.Count > 0 && a.IpAddresses.Any(i => i.IsActive && i.LastSeen >= networkEvidenceCutoff &&
                    i.MacAddress != null && macList.Contains(i.MacAddress))) ||
                (ipList.Count > 0 && a.IpAddresses.Any(i => i.IsActive && i.LastSeen >= networkEvidenceCutoff &&
                    ipList.Contains(i.IpAddress))))
            .OrderByDescending(a => hasHostname && a.NormalizedHostname == normalizedHostname)
            .ThenByDescending(a => macList.Count > 0 && a.IpAddresses.Any(i => i.IsActive &&
                i.LastSeen >= networkEvidenceCutoff && i.MacAddress != null && macList.Contains(i.MacAddress)))
            .ThenByDescending(a => ipList.Count > 0 && a.IpAddresses.Any(i => i.IsActive &&
                i.LastSeen >= networkEvidenceCutoff && ipList.Contains(i.IpAddress)))
            .ThenByDescending(a => a.LastSeen)
            .ThenBy(a => a.Id)
            .Take(250)
            .AsSplitQuery()
            .ToListAsync(ct);
    }

    public Task<List<Asset>> FindSoftCandidatesAsync(string? normalizedHostname,
        IReadOnlyCollection<string> macs, IReadOnlyCollection<string> ips, CancellationToken ct = default)
        => FindSoftCandidatesAsync(normalizedHostname, macs, ips, DateTime.UtcNow.AddDays(-30), ct);

    private static IQueryable<Asset> ApplySort(IQueryable<Asset> query, string sortBy, bool desc)
    {
        Expression<Func<Asset, object>> key = sortBy.ToLowerInvariant() switch
        {
            "lastseen" => a => a.LastSeen,
            "firstseen" => a => a.FirstSeen,
            "criticality" => a => a.Criticality,
            "compliancescore" => a => a.ComplianceScore,
            "compliancestatus" => a => a.ComplianceStatus,
            "assettype" => a => a.AssetType,
            "environment" => a => a.Environment,
            "status" => a => a.Status,
            "os" => a => a.OperatingSystem ?? "",
            "owner" => a => a.OwnerName ?? "",
            "businessunit" => a => a.BusinessUnit ?? "",
            "dataqualityscore" => a => a.DataQualityScore,
            "healthscore" => a => a.HealthScore,
            _ => a => a.NormalizedHostname
        };
        // Stable tiebreaker so identical sort keys page deterministically.
        var ordered = desc ? query.OrderByDescending(key) : query.OrderBy(key);
        return ordered.ThenBy(a => a.Id);
    }

    public async Task<List<FilterValue>> ListFilterValuesAsync(string field, CancellationToken ct = default)
    {
        // Distinct values of one column across non-deleted assets (the global query filter
        // excludes soft-deleted rows), counted database-side for the column filter dropdowns.
        // Enum keys are grouped in SQL and stringified after materialization — enum ToString()
        // inside a projection is not reliably translatable.
        async Task<List<FilterValue>> GroupEnumAsync<TKey>(Expression<Func<Asset, TKey>> key) where TKey : struct
        {
            var rows = await Db.Assets.GroupBy(key)
                .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            return Shape(rows.Select(r => (r.Key.ToString()!, r.Count)));
        }

        switch (field.ToLowerInvariant())
        {
            case "assettype": return await GroupEnumAsync(a => a.AssetType);
            case "status": return await GroupEnumAsync(a => a.Status);
            case "lifecyclestatus": return await GroupEnumAsync(a => a.LifecycleStatus);
            case "environment": return await GroupEnumAsync(a => a.Environment);
            case "criticality": return await GroupEnumAsync(a => a.Criticality);
            case "compliancestatus": return await GroupEnumAsync(a => a.ComplianceStatus);
            case "source":
            {
                var rows = await Db.AssetSources
                    .Where(s => s.Asset != null && !s.Asset.IsDeleted)
                    .GroupBy(s => s.ConnectorType)
                    .Select(g => new { g.Key, Count = g.Select(s => s.AssetId).Distinct().Count() })
                    .ToListAsync(ct);
                return Shape(rows.Select(r => (r.Key.ToString(), r.Count)));
            }
            case "os":
            {
                var rows = await Db.Assets.Where(a => a.OperatingSystem != null)
                    .GroupBy(a => a.OperatingSystem!)
                    .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
                return Shape(rows.Select(r => (r.Key, r.Count)));
            }
            case "businessunit":
            {
                var rows = await Db.Assets.Where(a => a.BusinessUnit != null)
                    .GroupBy(a => a.BusinessUnit!)
                    .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
                return Shape(rows.Select(r => (r.Key, r.Count)));
            }
            default:
                return new List<FilterValue>();
        }

        static List<FilterValue> Shape(IEnumerable<(string Value, int Count)> rows) => rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Value))
            .Select(r => new FilterValue(r.Value, r.Count))
            .OrderBy(v => v.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryEnum<TEnum>(string? value, out TEnum result) where TEnum : struct
    {
        if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, true, out result)) return true;
        result = default;
        return false;
    }

    private static List<TEnum> ParseEnums<TEnum>(List<string>? values) where TEnum : struct
        => values is null
            ? new List<TEnum>()
            : values.Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => Enum.TryParse<TEnum>(v, true, out var parsed) ? parsed : (TEnum?)null)
                .Where(v => v is not null).Select(v => v!.Value).Distinct().ToList();
}

public class UnitOfWork : IUnitOfWork
{
    private readonly EsarDbContext _db;

    public UnitOfWork(EsarDbContext db)
    {
        _db = db;
        Assets = new AssetRepository(db);
        AssetSources = new GenericRepository<AssetSource>(db);
        AssetIdentifiers = new GenericRepository<AssetIdentifier>(db);
        AssetIps = new GenericRepository<AssetIp>(db);
        AssetTags = new GenericRepository<AssetTag>(db);
        AssetHistories = new GenericRepository<AssetHistory>(db);
        AssetEvents = new GenericRepository<AssetEvent>(db);
        AssetRisks = new GenericRepository<AssetRisk>(db);
        AssetCompliance = new GenericRepository<AssetCompliance>(db);
        Relationships = new GenericRepository<AssetRelationship>(db);
        CompliancePolicies = new GenericRepository<CompliancePolicy>(db);
        AssetGroups = new GenericRepository<AssetGroup>(db);
        AssetGroupMembers = new GenericRepository<AssetGroupMember>(db);
        Approvals = new GenericRepository<ApprovalRequest>(db);
        MatchingRules = new GenericRepository<MatchingRule>(db);
        MatchRecords = new GenericRepository<MatchRecord>(db);
        SourcePriorities = new GenericRepository<SourcePriority>(db);
        Connectors = new GenericRepository<ConnectorConfig>(db);
        ConnectorJobs = new GenericRepository<ConnectorJob>(db);
        Incidents = new GenericRepository<Incident>(db);
        Notifications = new GenericRepository<Notification>(db);
        NotificationTemplates = new GenericRepository<NotificationTemplate>(db);
        EscalationRules = new GenericRepository<EscalationRule>(db);
        AuditLogs = new GenericRepository<AuditLog>(db);
        Users = new GenericRepository<User>(db);
        Roles = new GenericRepository<Role>(db);
        Permissions = new GenericRepository<Permission>(db);
        UserRoles = new GenericRepository<UserRole>(db);
        RolePermissions = new GenericRepository<RolePermission>(db);
        Reports = new GenericRepository<Report>(db);
        Settings = new GenericRepository<Setting>(db);
    }

    public IAssetRepository Assets { get; }
    public IRepository<AssetSource> AssetSources { get; }
    public IRepository<AssetIdentifier> AssetIdentifiers { get; }
    public IRepository<AssetIp> AssetIps { get; }
    public IRepository<AssetTag> AssetTags { get; }
    public IRepository<AssetHistory> AssetHistories { get; }
    public IRepository<AssetEvent> AssetEvents { get; }
    public IRepository<AssetRisk> AssetRisks { get; }
    public IRepository<AssetCompliance> AssetCompliance { get; }
    public IRepository<AssetRelationship> Relationships { get; }
    public IRepository<CompliancePolicy> CompliancePolicies { get; }
    public IRepository<AssetGroup> AssetGroups { get; }
    public IRepository<AssetGroupMember> AssetGroupMembers { get; }
    public IRepository<ApprovalRequest> Approvals { get; }
    public IRepository<MatchingRule> MatchingRules { get; }
    public IRepository<MatchRecord> MatchRecords { get; }
    public IRepository<SourcePriority> SourcePriorities { get; }
    public IRepository<ConnectorConfig> Connectors { get; }
    public IRepository<ConnectorJob> ConnectorJobs { get; }
    public IRepository<Incident> Incidents { get; }
    public IRepository<Notification> Notifications { get; }
    public IRepository<NotificationTemplate> NotificationTemplates { get; }
    public IRepository<EscalationRule> EscalationRules { get; }
    public IRepository<AuditLog> AuditLogs { get; }
    public IRepository<User> Users { get; }
    public IRepository<Role> Roles { get; }
    public IRepository<Permission> Permissions { get; }
    public IRepository<UserRole> UserRoles { get; }
    public IRepository<RolePermission> RolePermissions { get; }
    public IRepository<Report> Reports { get; }
    public IRepository<Setting> Settings { get; }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);

    public void ClearChangeTracker() => _db.ChangeTracker.Clear();
}
