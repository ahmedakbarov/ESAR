using Esar.Application.Abstractions;
using Esar.Domain.Enums;
using Esar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Esar.Infrastructure.Queries;

public class DashboardQueries : IDashboardQueries
{
    private readonly EsarDbContext _db;
    private readonly ICacheService _cache;

    public DashboardQueries(EsarDbContext db, ICacheService cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var cached = await _cache.GetAsync<DashboardSummary>(Application.Matching.CacheKeys.DashboardSummary, ct);
        if (cached is not null) return cached;

        var staleCutoff = DateTime.UtcNow.AddDays(-7);
        var summary = new DashboardSummary(
            TotalAssets: await _db.Assets.LongCountAsync(ct),
            ActiveAssets: await _db.Assets.LongCountAsync(a => a.Status == AssetStatus.Active, ct),
            CriticalAssets: await _db.Assets.LongCountAsync(a => a.Criticality == CriticalityLevel.Critical, ct),
            CloudAssets: await _db.Assets.LongCountAsync(a => a.CloudProvider != null, ct),
            NonCompliantAssets: await _db.Assets.LongCountAsync(a => a.ComplianceStatus == ComplianceStatus.NonCompliant, ct),
            PendingReviewMatches: await _db.MatchRecords.LongCountAsync(m => m.Decision == MatchDecision.QueuedForReview, ct),
            OpenIncidents: await _db.Incidents.LongCountAsync(
                i => i.Status == IncidentStatus.Open || i.Status == IncidentStatus.InProgress, ct),
            AvgComplianceScore: Math.Round(await _db.Assets.AverageAsync(a => (decimal?)a.ComplianceScore, ct) ?? 0, 2),
            StaleAssets: await _db.Assets.LongCountAsync(a => a.LastSeen < staleCutoff, ct),
            DuplicateCandidates: await _db.MatchRecords.LongCountAsync(m => m.Decision == MatchDecision.QueuedForReview, ct));

        await _cache.SetAsync(Application.Matching.CacheKeys.DashboardSummary, summary, TimeSpan.FromMinutes(2), ct);
        return summary;
    }

    public async Task<IReadOnlyList<NameCount>> GetAssetsByTypeAsync(CancellationToken ct = default)
    {
        var raw = await _db.Assets.GroupBy(a => a.AssetType)
            .Select(g => new { g.Key, Count = g.LongCount() })
            .ToListAsync(ct);
        return raw.Select(x => new NameCount(x.Key.ToString(), x.Count)).ToList();
    }

    public async Task<IReadOnlyList<NameCount>> GetAssetsByOsAsync(int top = 10, CancellationToken ct = default)
        => await _db.Assets.Where(a => a.OperatingSystem != null)
            .GroupBy(a => a.OperatingSystem!)
            .Select(g => new NameCount(g.Key, g.LongCount()))
            .OrderByDescending(x => x.Count)
            .Take(top)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<NameCount>> GetAssetsByEnvironmentAsync(CancellationToken ct = default)
    {
        var raw = await _db.Assets.GroupBy(a => a.Environment)
            .Select(g => new { g.Key, Count = g.LongCount() })
            .ToListAsync(ct);
        return raw.Select(x => new NameCount(x.Key.ToString(), x.Count)).ToList();
    }

    public async Task<IReadOnlyList<NameCount>> GetMissingControlsAsync(CancellationToken ct = default)
    {
        var raw = await _db.AssetCompliance
            .Where(c => c.Status == ComplianceStatus.NonCompliant)
            .GroupBy(c => c.Control)
            .Select(g => new { g.Key, Count = g.LongCount() })
            .ToListAsync(ct);
        return raw.Select(x => new NameCount(x.Key.ToString(), x.Count))
            .OrderByDescending(x => x.Count).ToList();
    }

    public async Task<IReadOnlyList<TimePoint>> GetAssetGrowthAsync(int days = 30, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.Date.AddDays(-days);
        var created = await _db.Assets
            .Where(a => a.FirstSeen >= since)
            .GroupBy(a => a.FirstSeen.Date)
            .Select(g => new { Date = g.Key, Count = g.LongCount() })
            .OrderBy(x => x.Date)
            .ToListAsync(ct);

        var baseline = await _db.Assets.LongCountAsync(a => a.FirstSeen < since, ct);
        var points = new List<TimePoint>();
        var running = baseline;
        for (var day = since; day <= DateTime.UtcNow.Date; day = day.AddDays(1))
        {
            running += created.FirstOrDefault(c => c.Date == day)?.Count ?? 0;
            points.Add(new TimePoint(day, running));
        }
        return points;
    }

    public async Task<IReadOnlyList<NameCount>> GetConnectorHealthAsync(CancellationToken ct = default)
        => await _db.Connectors
            .Select(c => new NameCount(c.Name, c.IsHealthy ? 1 : 0))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TopRiskAsset>> GetTopRisksAsync(int top = 10, CancellationToken ct = default)
    {
        var raw = await _db.Assets
            .Where(a => a.ComplianceStatus == ComplianceStatus.NonCompliant)
            .OrderByDescending(a => a.Criticality)
            .ThenBy(a => a.ComplianceScore)
            .Take(top)
            .Select(a => new
            {
                a.Id, a.Hostname, RiskScore = a.Risk != null ? a.Risk.RiskScore : 0,
                a.Criticality, a.ComplianceScore
            })
            .ToListAsync(ct);
        return raw.Select(x => new TopRiskAsset(x.Id, x.Hostname, x.RiskScore,
            x.Criticality.ToString(), x.ComplianceScore)).ToList();
    }
}
