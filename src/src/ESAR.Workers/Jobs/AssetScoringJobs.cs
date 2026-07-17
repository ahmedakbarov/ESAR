using Esar.Application.Abstractions;
using Esar.Application.Incidents;
using Esar.Application.Scoring;
using Esar.Domain.Enums;
using Hangfire;

namespace Esar.Workers.Jobs;

/// <summary>
/// Periodic recalculation of data quality, health and risk scores for the whole fleet,
/// plus cross-asset consistency checks (duplicate IP detection).
/// </summary>
public class AssetScoringJobs
{
    private readonly IUnitOfWork _uow;
    private readonly IDataQualityEngine _dataQuality;
    private readonly IAssetHealthEngine _health;
    private readonly IRiskScoringEngine _risk;
    private readonly IIncidentService _incidents;
    private readonly IEventBus _events;
    private readonly ILogger<AssetScoringJobs> _logger;

    public AssetScoringJobs(IUnitOfWork uow, IDataQualityEngine dataQuality, IAssetHealthEngine health,
        IRiskScoringEngine risk, IIncidentService incidents, IEventBus events, ILogger<AssetScoringJobs> logger)
    {
        _uow = uow;
        _dataQuality = dataQuality;
        _health = health;
        _risk = risk;
        _incidents = incidents;
        _events = events;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 2)]
    public async Task RecalculateAllAsync(CancellationToken ct)
    {
        var alertBelow = await GetAlertThresholdAsync(ct);
        var assetIds = (await _uow.Assets.ListAsync(a => !a.IsDeleted, ct)).Select(a => a.Id).ToList();
        _logger.LogInformation("Scoring pass starting for {Count} assets", assetIds.Count);

        var processed = 0;
        foreach (var assetId in assetIds)
        {
            ct.ThrowIfCancellationRequested();
            var asset = await _uow.Assets.GetWithDetailsAsync(assetId, ct);
            if (asset is null) continue;

            var previousDq = asset.DataQualityScore;
            _dataQuality.Evaluate(asset);
            _health.Evaluate(asset);
            _risk.Evaluate(asset);
            _uow.Assets.Update(asset);

            if (asset.DataQualityScore < alertBelow && previousDq >= alertBelow)
            {
                await _events.PublishAsync(EventTopics.DataQualityDegraded, new
                {
                    AssetId = asset.Id,
                    asset.Hostname,
                    Score = asset.DataQualityScore,
                    Issues = asset.DataQualityIssuesJson
                }, ct);
            }

            processed++;
            if (processed % 200 == 0) await _uow.SaveChangesAsync(ct);
        }
        await _uow.SaveChangesAsync(ct);
        _logger.LogInformation("Scoring pass finished: {Count} assets", processed);
    }

    /// <summary>Detects the same IP claimed by multiple active assets — a classic dedup/data-quality smell.</summary>
    [AutomaticRetry(Attempts = 2)]
    public async Task DetectDuplicateIpsAsync(CancellationToken ct)
    {
        var recentCutoff = DateTime.UtcNow.AddDays(-7);
        var ips = await _uow.AssetIps.ListAsync(i => i.LastSeen >= recentCutoff, ct);
        var duplicates = ips
            .Where(i => !string.IsNullOrEmpty(i.IpAddress))
            .GroupBy(i => i.IpAddress)
            .Where(g => g.Select(i => i.AssetId).Distinct().Count() > 1)
            .Take(100)
            .ToList();

        foreach (var group in duplicates)
        {
            var assetIds = group.Select(i => i.AssetId).Distinct().OrderBy(id => id).ToList();
            // Deterministic dedup key: incident is anchored to the first asset of the pair/group.
            await _incidents.RaiseAsync(IncidentType.DuplicateAssets, IncidentSeverity.Medium,
                $"Duplicate IP {group.Key} across {assetIds.Count} assets",
                $"IP address {group.Key} is actively reported for multiple assets: " +
                $"{string.Join(", ", assetIds)}. Review the matching queue or merge duplicates.",
                assetIds[0], null, ct);
        }

        if (duplicates.Count > 0)
            _logger.LogWarning("Duplicate IP detection found {Count} shared addresses", duplicates.Count);
    }

    private async Task<decimal> GetAlertThresholdAsync(CancellationToken ct)
    {
        var setting = await _uow.Settings.FirstOrDefaultAsync(s => s.Key == "dataquality.alertBelowScore", ct);
        return setting is not null && decimal.TryParse(setting.Value, out var v) ? v : 50m;
    }
}
