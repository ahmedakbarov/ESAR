using System.Text.Json;
using Esar.Application.Abstractions;
using Esar.Application.Approvals;
using Esar.Application.Auditing;
using Esar.Application.Contracts;
using Esar.Application.Matching;
using Esar.Application.Merging;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using MatchType = Esar.Domain.Enums.MatchType;
using Microsoft.Extensions.Logging;

namespace Esar.Application.Ingestion;

public interface IAssetIngestionService
{
    /// <summary>Processes one discovered asset end-to-end: normalize → match → merge/create → events.</summary>
    Task<IngestionOutcome> IngestAsync(DiscoveredAsset discovered, CancellationToken ct = default);
}

public enum IngestionOutcome { Created, Updated, QueuedForReview, Failed }

public class AssetIngestionService : IAssetIngestionService
{
    private readonly IUnitOfWork _uow;
    private readonly INormalizationService _normalization;
    private readonly IMatchingEngine _matching;
    private readonly IMergeEngine _merge;
    private readonly IApprovalService _approvals;
    private readonly IEventBus _events;
    private readonly IAuditService _audit;
    private readonly ILogger<AssetIngestionService> _logger;

    public AssetIngestionService(IUnitOfWork uow, INormalizationService normalization, IMatchingEngine matching,
        IMergeEngine merge, IApprovalService approvals, IEventBus events, IAuditService audit,
        ILogger<AssetIngestionService> logger)
    {
        _uow = uow;
        _normalization = normalization;
        _matching = matching;
        _merge = merge;
        _approvals = approvals;
        _events = events;
        _audit = audit;
        _logger = logger;
    }

    public async Task<IngestionOutcome> IngestAsync(DiscoveredAsset discovered, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(discovered.ExternalId))
        {
            _logger.LogWarning("Discovered asset from {Source} has no external id — rejected", discovered.Source);
            return IngestionOutcome.Failed;
        }

        var normalized = _normalization.Normalize(discovered);

        // 1. Already linked to a golden record? Straight merge, no matching needed.
        var linked = await _uow.Assets.FindBySourceAsync(normalized.Source, normalized.ExternalId, ct);
        if (linked is not null)
        {
            await UpdateExistingAsync(linked, normalized, ct);
            await _uow.SaveChangesAsync(ct);
            await _events.PublishAsync(EventTopics.AssetUpdated, new { AssetId = linked.Id, Source = normalized.Source.ToString() }, ct);
            return IngestionOutcome.Updated;
        }

        // 1b. Previously (soft-)deleted but this source still reports it — reactivate instead of
        // trying to create a fresh Asset, which would collide with the surviving AssetSource row
        // on the (ConnectorType, ExternalId) unique constraint and fail every sync from now on.
        var revived = await _uow.Assets.FindDeletedBySourceAsync(normalized.Source, normalized.ExternalId, ct);
        if (revived is not null)
        {
            revived.IsDeleted = false;
            revived.Status = AssetStatus.Active;
            revived.LifecycleStatus = LifecycleStatus.Active;
            await UpdateExistingAsync(revived, normalized, ct);
            await _uow.SaveChangesAsync(ct);
            await _audit.LogAsync(AuditAction.AssetReactivated, nameof(Asset), revived.Id.ToString(),
                new { revived.Hostname, Source = normalized.Source.ToString(), normalized.ExternalId }, ct);
            _logger.LogInformation(
                "Asset {AssetId} ({Hostname}) was deleted but is still reported by {Source}:{ExternalId} — reactivated",
                revived.Id, revived.Hostname, normalized.Source, normalized.ExternalId);
            await _events.PublishAsync(EventTopics.AssetUpdated, new { AssetId = revived.Id, Source = normalized.Source.ToString() }, ct);
            return IngestionOutcome.Updated;
        }

        // 2. Run the matching engine.
        var match = await _matching.MatchAsync(normalized, ct);
        var record = new MatchRecord
        {
            SourceConnector = normalized.Source,
            ExternalId = normalized.ExternalId,
            CandidateHostname = normalized.Hostname,
            ConfidenceScore = match.ConfidenceScore,
            MatchType = match.MatchType,
            Decision = match.Decision,
            ExplanationJson = match.ExplanationJson,
            CandidateJson = JsonSerializer.Serialize(normalized)
        };

        switch (match.Decision)
        {
            case MatchDecision.AutoMerged when match.MatchedAsset is not null:
                record.MatchedAssetId = match.MatchedAsset.Id;
                await UpdateExistingAsync(match.MatchedAsset, normalized, ct);
                foreach (var pending in await _uow.MatchRecords.ListAsync(existing =>
                             existing.SourceConnector == normalized.Source &&
                             existing.ExternalId == normalized.ExternalId &&
                             existing.Decision == MatchDecision.QueuedForReview, ct))
                {
                    pending.Decision = MatchDecision.Approved;
                    pending.ReviewedBy = "system:auto-match";
                    pending.ReviewedAt = DateTime.UtcNow;
                    pending.ReviewComment = "Automatically resolved after stronger identity evidence arrived.";
                    _uow.MatchRecords.Update(pending);
                }
                await _uow.MatchRecords.AddAsync(record, ct);
                await _uow.SaveChangesAsync(ct);
                await _events.PublishAsync(EventTopics.AssetUpdated,
                    new { AssetId = match.MatchedAsset.Id, Source = normalized.Source.ToString() }, ct);
                return IngestionOutcome.Updated;

            case MatchDecision.QueuedForReview when match.MatchedAsset is not null:
                // Ambiguous — park the candidate for a human decision; do not touch the golden record.
                record.MatchedAssetId = match.MatchedAsset.Id;
                var pending = await _uow.MatchRecords.FirstOrDefaultAsync(existing =>
                    existing.SourceConnector == normalized.Source &&
                    existing.ExternalId == normalized.ExternalId &&
                    existing.Decision == MatchDecision.QueuedForReview, ct);
                if (pending is null)
                    await _uow.MatchRecords.AddAsync(record, ct);
                else
                {
                    pending.CandidateHostname = record.CandidateHostname;
                    pending.MatchedAssetId = record.MatchedAssetId;
                    pending.ConfidenceScore = record.ConfidenceScore;
                    pending.MatchType = record.MatchType;
                    pending.ExplanationJson = record.ExplanationJson;
                    pending.CandidateJson = record.CandidateJson;
                    pending.UpdatedAt = DateTime.UtcNow;
                    _uow.MatchRecords.Update(pending);
                }
                await _uow.SaveChangesAsync(ct);
                _logger.LogInformation("Candidate {ExternalId} from {Source} queued for review (score {Score})",
                    normalized.ExternalId, normalized.Source, match.ConfidenceScore);
                return IngestionOutcome.QueuedForReview;

            default:
                var requiresApproval = await RequiresApprovalAsync(ct);
                var created = await CreateNewAsync(normalized, requiresApproval, ct);
                record.CreatedAssetId = created.Id;
                await _uow.MatchRecords.AddAsync(record, ct);
                await _uow.SaveChangesAsync(ct);
                if (requiresApproval)
                {
                    // Asset stays in LifecycleStatus.Planned until an owner approves it.
                    await _approvals.CreateAsync(ApprovalType.NewAsset, created.Id, new AssetApprovalPayload(),
                        $"connector:{normalized.Source}", "Newly discovered asset pending owner validation", ct);
                }
                await _events.PublishAsync(EventTopics.AssetCreated,
                    new { AssetId = created.Id, Source = normalized.Source.ToString() }, ct);
                return IngestionOutcome.Created;
        }
    }

    private async Task<bool> RequiresApprovalAsync(CancellationToken ct)
    {
        var setting = await _uow.Settings.FirstOrDefaultAsync(
            s => s.Key == SettingKeys.ApprovalRequireForNewAssets, ct);
        return setting is not null && bool.TryParse(setting.Value, out var required) && required;
    }

    private async Task UpdateExistingAsync(Asset asset, DiscoveredAsset incoming, CancellationToken ct)
    {
        await _merge.ApplyAsync(asset, incoming, ct);
        await UpsertSourceLinkAsync(asset, incoming, ct);
        _uow.Assets.Update(asset);
    }

    private async Task<Asset> CreateNewAsync(DiscoveredAsset d, bool requiresApproval, CancellationToken ct)
    {
        var asset = new Asset
        {
            Hostname = d.Hostname ?? d.ExternalId,
            NormalizedHostname = _normalization.NormalizeHostname(d.Hostname ?? d.ExternalId),
            FirstSeen = d.SeenAt,
            LastSeen = d.SeenAt,
            Status = AssetStatus.Active,
            LifecycleStatus = requiresApproval ? LifecycleStatus.Planned : LifecycleStatus.Active,
            CreatedBy = $"connector:{d.Source}"
        };
        await _merge.ApplyAsync(asset, d, ct);
        await UpsertSourceLinkAsync(asset, d, ct);
        await _uow.Assets.AddAsync(asset, ct);
        return asset;
    }

    private async Task UpsertSourceLinkAsync(Asset asset, DiscoveredAsset d, CancellationToken ct)
    {
        var link = asset.Sources.FirstOrDefault(s => s.ConnectorType == d.Source && s.ExternalId == d.ExternalId);
        if (link is null)
        {
            var created = new AssetSource
            {
                AssetId = asset.Id,
                ConnectorType = d.Source,
                ExternalId = d.ExternalId,
                SourceHostname = d.Hostname,
                RawData = d.RawJson,
                FirstSeen = d.SeenAt,
                LastSeen = d.SeenAt
            };
            asset.Sources.Add(created);
            // Source links also use client-generated GUID keys. Explicitly registering the
            // dependent keeps an Azure-to-AD auto-merge from producing a zero-row UPDATE.
            await _uow.AssetSources.AddAsync(created, ct);
        }
        else
        {
            link.LastSeen = d.SeenAt;
            link.SourceHostname = d.Hostname ?? link.SourceHostname;
            link.RawData = d.RawJson ?? link.RawData;
        }
        asset.NormalizedHostname = string.IsNullOrEmpty(asset.NormalizedHostname)
            ? asset.Hostname.ToLowerInvariant()
            : asset.NormalizedHostname;
    }
}
