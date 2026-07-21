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
    private static readonly ConnectorType[] EdrConnectors =
        { ConnectorType.MicrosoftDefender, ConnectorType.CortexXdr, ConnectorType.CrowdStrike, ConnectorType.SentinelOne };

    public AssetRepository(EsarDbContext db) : base(db) { }

    private IQueryable<Asset> Detailed => Db.Assets
        .Include(a => a.Sources)
        .Include(a => a.IpAddresses)
        .Include(a => a.Tags)
        .Include(a => a.ComplianceRecords)
        .Include(a => a.Software)
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
                    a.IpAddresses.Any(ip => EF.Functions.ILike(ip.IpAddress, term)) ||
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
            query = query.Where(a => a.IpAddresses.Any(i => i.IpAddress == c.Ip.Trim()));
        if (!string.IsNullOrWhiteSpace(c.Mac))
        {
            var mac = c.Mac.Trim().ToLowerInvariant();
            query = query.Where(a => a.IpAddresses.Any(i => i.MacAddress == mac));
        }
        if (!string.IsNullOrWhiteSpace(c.Os))
            query = query.Where(a => EF.Functions.ILike(a.OperatingSystem ?? "", $"%{c.Os.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(c.Software))
            query = query.Where(a => a.Software.Any(s => EF.Functions.ILike(s.Name, $"%{c.Software.Trim()}%")));
        if (!string.IsNullOrWhiteSpace(c.CloudProvider))
            query = query.Where(a => a.CloudProvider == c.CloudProvider);
        if (c.MaxDataQualityScore is { } maxDq)
            query = query.Where(a => a.DataQualityScore <= maxDq);
        if (!string.IsNullOrWhiteSpace(c.TagKey))
        {
            query = string.IsNullOrWhiteSpace(c.TagValue)
                ? query.Where(a => a.Tags.Any(t => t.Key == c.TagKey))
                : query.Where(a => a.Tags.Any(t => t.Key == c.TagKey && t.Value == c.TagValue));
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

    public Task<Asset?> FindByHardIdentifierAsync(string attribute, string value, CancellationToken ct = default)
    {
        var q = Detailed.Where(a => a.MergedIntoAssetId == null);
        return attribute switch
        {
            MatchAttributes.AzureResourceId => q.FirstOrDefaultAsync(a =>
                a.CloudResourceId == value ||
                a.Sources.Any(s => s.ConnectorType == ConnectorType.Azure && s.ExternalId == value), ct),
            MatchAttributes.AwsInstanceId => q.FirstOrDefaultAsync(a =>
                a.CloudResourceId == value ||
                a.Sources.Any(s => s.ConnectorType == ConnectorType.Aws && s.ExternalId == value), ct),
            MatchAttributes.VmwareUuid => q.FirstOrDefaultAsync(a =>
                a.BiosUuid == value ||
                a.Sources.Any(s => s.ConnectorType == ConnectorType.VmwareVCenter && s.ExternalId == value), ct),
            MatchAttributes.BiosUuid => q.FirstOrDefaultAsync(a => a.BiosUuid == value, ct),
            MatchAttributes.SerialNumber => q.FirstOrDefaultAsync(a => a.SerialNumber == value, ct),
            MatchAttributes.ObjectGuid => q.FirstOrDefaultAsync(a => a.Sources.Any(s =>
                (s.ConnectorType == ConnectorType.ActiveDirectory || s.ConnectorType == ConnectorType.EntraId) &&
                s.ExternalId == value), ct),
            MatchAttributes.EndpointId => q.FirstOrDefaultAsync(a => a.Sources.Any(s =>
                EdrConnectors.Contains(s.ConnectorType) && s.ExternalId == value), ct),
            _ => Task.FromResult<Asset?>(null)
        };
    }

    public async Task<List<Asset>> FindSoftCandidatesAsync(string? normalizedHostname,
        IReadOnlyCollection<string> macs, IReadOnlyCollection<string> ips, CancellationToken ct = default)
    {
        var macList = macs.ToList();
        var ipList = ips.ToList();
        var hasHostname = !string.IsNullOrWhiteSpace(normalizedHostname);
        if (!hasHostname && macList.Count == 0 && ipList.Count == 0) return new List<Asset>();

        return await Detailed
            .Where(a => a.MergedIntoAssetId == null)
            .Where(a =>
                (hasHostname && a.NormalizedHostname == normalizedHostname) ||
                (macList.Count > 0 && a.IpAddresses.Any(i => i.MacAddress != null && macList.Contains(i.MacAddress))) ||
                (ipList.Count > 0 && a.IpAddresses.Any(i => ipList.Contains(i.IpAddress))))
            .Take(25)
            .AsSplitQuery()
            .ToListAsync(ct);
    }

    private static IQueryable<Asset> ApplySort(IQueryable<Asset> query, string sortBy, bool desc)
    {
        Expression<Func<Asset, object>> key = sortBy.ToLowerInvariant() switch
        {
            "lastseen" => a => a.LastSeen,
            "firstseen" => a => a.FirstSeen,
            "criticality" => a => a.Criticality,
            "compliancescore" => a => a.ComplianceScore,
            "assettype" => a => a.AssetType,
            "environment" => a => a.Environment,
            "status" => a => a.Status,
            _ => a => a.NormalizedHostname
        };
        return desc ? query.OrderByDescending(key) : query.OrderBy(key);
    }

    private static bool TryEnum<TEnum>(string? value, out TEnum result) where TEnum : struct
    {
        if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, true, out result)) return true;
        result = default;
        return false;
    }
}

public class UnitOfWork : IUnitOfWork
{
    private readonly EsarDbContext _db;

    public UnitOfWork(EsarDbContext db)
    {
        _db = db;
        Assets = new AssetRepository(db);
        AssetSources = new GenericRepository<AssetSource>(db);
        AssetIps = new GenericRepository<AssetIp>(db);
        AssetTags = new GenericRepository<AssetTag>(db);
        AssetHistories = new GenericRepository<AssetHistory>(db);
        AssetCompliance = new GenericRepository<AssetCompliance>(db);
        Relationships = new GenericRepository<AssetRelationship>(db);
        CompliancePolicies = new GenericRepository<CompliancePolicy>(db);
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
    public IRepository<AssetIp> AssetIps { get; }
    public IRepository<AssetTag> AssetTags { get; }
    public IRepository<AssetHistory> AssetHistories { get; }
    public IRepository<AssetCompliance> AssetCompliance { get; }
    public IRepository<AssetRelationship> Relationships { get; }
    public IRepository<CompliancePolicy> CompliancePolicies { get; }
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
    {
        try
        {
            return await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // A freshly created child (client-generated GUID key — AssetIp/AssetTag/AssetSource/
            // AssetHistory) can get mis-tracked as Modified, so its UPDATE affects 0 rows and the
            // whole asset save fails. The failed entries are exactly the phantom-new rows: re-mark
            // them as Added and retry once so the insert goes through.
            var repromoted = false;
            foreach (var entry in ex.Entries)
            {
                if (entry.State == EntityState.Modified)
                {
                    entry.State = EntityState.Added;
                    repromoted = true;
                }
            }
            if (!repromoted) throw;
            return await _db.SaveChangesAsync(ct);
        }
    }

    public void ClearChangeTracker() => _db.ChangeTracker.Clear();
}
