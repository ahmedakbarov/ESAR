using System.Text.Json;
using Asp.Versioning;
using Esar.Application.Abstractions;
using Esar.Application.Auditing;
using Esar.Application.Compliance;
using Esar.Application.Contracts;
using Esar.Application.Matching;
using Esar.Application.Merging;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Esar.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/matching")]
public class MatchingController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly IMatchingEngine _matching;
    private readonly IMergeEngine _merge;
    private readonly IAuditService _audit;
    private readonly ICurrentUserService _user;
    private readonly ICacheService _cache;

    public MatchingController(IUnitOfWork uow, IMatchingEngine matching, IMergeEngine merge,
        IAuditService audit, ICurrentUserService user, ICacheService cache)
    {
        _uow = uow;
        _matching = matching;
        _merge = merge;
        _audit = audit;
        _user = user;
        _cache = cache;
    }

    /// <summary>Manual review queue: ambiguous soft matches awaiting a human decision.</summary>
    [HttpGet("review-queue")]
    [Authorize("matching.read")]
    public async Task<IActionResult> ReviewQueue(CancellationToken ct)
    {
        var items = await _uow.MatchRecords.ListAsync(m => m.Decision == MatchDecision.QueuedForReview, ct);
        return Ok(items.OrderByDescending(m => m.ConfidenceScore).Select(m => new
        {
            m.Id,
            m.CandidateHostname,
            Source = m.SourceConnector.ToString(),
            m.ExternalId,
            m.MatchedAssetId,
            Score = m.ConfidenceScore,
            Explanation = JsonSerializer.Deserialize<object>(m.ExplanationJson),
            QueuedAt = m.CreatedAt
        }));
    }

    /// <summary>Approves a queued match: the candidate payload is merged into the matched asset.</summary>
    [HttpPost("review-queue/{id:guid}/approve")]
    [Authorize("matching.review")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] ReviewRequest? request, CancellationToken ct)
    {
        var record = await _uow.MatchRecords.GetByIdAsync(id, ct);
        if (record is null || record.Decision != MatchDecision.QueuedForReview) return NotFound();
        if (record.MatchedAssetId is null || record.CandidateJson is null)
            return BadRequest(new { error = "Record has no matched asset or candidate payload." });

        var asset = await _uow.Assets.GetWithDetailsAsync(record.MatchedAssetId.Value, ct);
        if (asset is null) return NotFound(new { error = "Matched asset no longer exists." });

        var candidate = JsonSerializer.Deserialize<DiscoveredAsset>(record.CandidateJson)!;
        await _merge.ApplyAsync(asset, candidate, ct);
        if (!asset.Sources.Any(s => s.ConnectorType == candidate.Source && s.ExternalId == candidate.ExternalId))
        {
            asset.Sources.Add(new AssetSource
            {
                AssetId = asset.Id,
                ConnectorType = candidate.Source,
                ExternalId = candidate.ExternalId,
                SourceHostname = candidate.Hostname,
                RawData = candidate.RawJson
            });
        }
        _uow.Assets.Update(asset);

        record.Decision = MatchDecision.Approved;
        record.ReviewedBy = _user.UserName;
        record.ReviewedAt = DateTime.UtcNow;
        record.ReviewComment = request?.Comment;
        _uow.MatchRecords.Update(record);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.MatchingDecision, nameof(MatchRecord), record.Id.ToString(),
            new { decision = "Approved", assetId = asset.Id }, ct);
        return Ok(new { approved = true, assetId = asset.Id });
    }

    /// <summary>Rejects a queued match: the candidate becomes a brand-new asset.</summary>
    [HttpPost("review-queue/{id:guid}/reject")]
    [Authorize("matching.review")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] ReviewRequest? request,
        [FromServices] Esar.Application.Ingestion.IAssetIngestionService ingestion, CancellationToken ct)
    {
        var record = await _uow.MatchRecords.GetByIdAsync(id, ct);
        if (record is null || record.Decision != MatchDecision.QueuedForReview) return NotFound();
        if (record.CandidateJson is null) return BadRequest(new { error = "Record has no candidate payload." });

        // Mark reviewed first so re-ingestion cannot re-queue against this record.
        record.Decision = MatchDecision.Rejected;
        record.ReviewedBy = _user.UserName;
        record.ReviewedAt = DateTime.UtcNow;
        record.ReviewComment = request?.Comment;
        _uow.MatchRecords.Update(record);
        await _uow.SaveChangesAsync(ct);

        // Create a distinct asset directly from the candidate snapshot.
        var candidate = JsonSerializer.Deserialize<DiscoveredAsset>(record.CandidateJson)!;
        var asset = new Asset
        {
            Hostname = candidate.Hostname ?? candidate.ExternalId,
            NormalizedHostname = (candidate.Hostname ?? candidate.ExternalId).ToLowerInvariant(),
            CreatedBy = _user.UserName
        };
        await _merge.ApplyAsync(asset, candidate, ct);
        asset.Sources.Add(new AssetSource
        {
            AssetId = asset.Id,
            ConnectorType = candidate.Source,
            ExternalId = candidate.ExternalId,
            SourceHostname = candidate.Hostname,
            RawData = candidate.RawJson
        });
        await _uow.Assets.AddAsync(asset, ct);
        record.CreatedAssetId = asset.Id;
        _uow.MatchRecords.Update(record);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.MatchingDecision, nameof(MatchRecord), record.Id.ToString(),
            new { decision = "Rejected", newAssetId = asset.Id }, ct);
        return Ok(new { rejected = true, newAssetId = asset.Id });
    }

    /// <summary>Dry-run matching simulation — nothing is persisted.</summary>
    [HttpPost("simulate")]
    [Authorize("matching.read")]
    public async Task<IActionResult> Simulate([FromBody] DiscoveredAsset candidate, CancellationToken ct)
    {
        var result = await _matching.SimulateAsync(candidate, ct);
        return Ok(new
        {
            decision = result.Decision.ToString(),
            matchedAssetId = result.MatchedAsset?.Id,
            matchedHostname = result.MatchedAsset?.Hostname,
            score = result.ConfidenceScore,
            explanations = result.Explanations
        });
    }

    /// <summary>Matching analytics for the confidence dashboard.</summary>
    [HttpGet("stats")]
    [Authorize("matching.read")]
    public async Task<IActionResult> Stats(CancellationToken ct)
    {
        var records = await _uow.MatchRecords.ListAsync(null, ct);
        var last30 = records.Where(r => r.CreatedAt >= DateTime.UtcNow.AddDays(-30)).ToList();
        return Ok(new
        {
            total = records.Count,
            byDecision = records.GroupBy(r => r.Decision.ToString())
                .ToDictionary(g => g.Key, g => g.Count()),
            avgConfidenceLast30Days = last30.Count == 0 ? 0 : Math.Round(last30.Average(r => r.ConfidenceScore), 4),
            pendingReview = records.Count(r => r.Decision == MatchDecision.QueuedForReview)
        });
    }

    /// <summary>Matching rules (weights, order, hard/soft).</summary>
    [HttpGet("rules")]
    [Authorize("matching.read")]
    public async Task<IActionResult> Rules(CancellationToken ct)
        => Ok((await _uow.MatchingRules.ListAsync(null, ct)).OrderBy(r => r.Order));

    public record RuleUpdate(decimal Weight, int Order, bool Enabled);

    [HttpPut("rules/{id:guid}")]
    [Authorize("settings.manage")]
    public async Task<IActionResult> UpdateRule(Guid id, [FromBody] RuleUpdate update, CancellationToken ct)
    {
        var rule = await _uow.MatchingRules.GetByIdAsync(id, ct);
        if (rule is null) return NotFound();
        rule.Weight = update.Weight;
        rule.Order = update.Order;
        rule.Enabled = update.Enabled;
        rule.UpdatedAt = DateTime.UtcNow;
        _uow.MatchingRules.Update(rule);
        await _uow.SaveChangesAsync(ct);
        await _cache.RemoveAsync(CacheKeys.MatchingRules, ct);
        await _audit.LogAsync(AuditAction.ConfigurationChanged, nameof(MatchingRule), rule.Id.ToString(),
            update, ct);
        return Ok(rule);
    }

    public record ReviewRequest(string? Comment);
}

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/compliance")]
public class ComplianceController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly IComplianceEngine _engine;

    public ComplianceController(IUnitOfWork uow, IComplianceEngine engine)
    {
        _uow = uow;
        _engine = engine;
    }

    /// <summary>Fleet-wide compliance summary by status and control.</summary>
    [HttpGet("summary")]
    [Authorize("compliance.read")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var assets = await _uow.Assets.ListAsync(a => !a.IsDeleted, ct);
        var records = await _uow.AssetCompliance.ListAsync(null, ct);
        return Ok(new
        {
            byStatus = assets.GroupBy(a => a.ComplianceStatus.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            avgScore = assets.Count == 0 ? 0 : Math.Round(assets.Average(a => a.ComplianceScore), 2),
            byControl = records.GroupBy(r => r.Control.ToString()).ToDictionary(g => g.Key, g => new
            {
                compliant = g.Count(r => r.Status == ComplianceStatus.Compliant),
                nonCompliant = g.Count(r => r.Status == ComplianceStatus.NonCompliant),
                pending = g.Count(r => r.Status == ComplianceStatus.Pending),
                unknown = g.Count(r => r.Status == ComplianceStatus.Unknown)
            })
        });
    }

    /// <summary>Re-evaluates all controls for one asset immediately.</summary>
    [HttpPost("assets/{assetId:guid}/evaluate")]
    [Authorize("compliance.read")]
    public async Task<IActionResult> Evaluate(Guid assetId, CancellationToken ct)
    {
        var asset = await _uow.Assets.GetWithDetailsAsync(assetId, ct);
        if (asset is null) return NotFound();
        var status = await _engine.EvaluateAsync(asset, ct);
        await _uow.SaveChangesAsync(ct);
        return Ok(new { assetId, status = status.ToString(), score = asset.ComplianceScore });
    }

    /// <summary>Assets failing a specific control.</summary>
    [HttpGet("failing/{control}")]
    [Authorize("compliance.read")]
    public async Task<IActionResult> Failing(string control, CancellationToken ct)
    {
        if (!Enum.TryParse<ControlType>(control, true, out var controlType))
            return BadRequest(new { error = $"Unknown control '{control}'." });
        var failing = await _uow.AssetCompliance.ListAsync(
            c => c.Control == controlType && c.Status == ComplianceStatus.NonCompliant, ct);
        var assetIds = failing.Select(f => f.AssetId).Distinct().ToList();
        var assets = await _uow.Assets.ListAsync(a => assetIds.Contains(a.Id), ct);
        return Ok(assets.Select(Esar.Application.Assets.AssetDto.From));
    }
}
