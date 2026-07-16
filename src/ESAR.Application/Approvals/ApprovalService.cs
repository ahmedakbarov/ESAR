using System.Text.Json;
using Esar.Application.Abstractions;
using Esar.Application.Auditing;
using Esar.Application.Merging;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Esar.Application.Approvals;

public interface IApprovalService
{
    Task<ApprovalRequest> CreateAsync(ApprovalType type, Guid? assetId, object payload,
        string requestedBy, string? justification = null, CancellationToken ct = default);
    /// <summary>Approves or rejects a pending request and applies its effect.</summary>
    Task<ApprovalRequest?> DecideAsync(Guid requestId, bool approve, string decidedBy,
        string? comment = null, CancellationToken ct = default);
    Task<List<ApprovalRequest>> GetPendingAsync(CancellationToken ct = default);
}

/// <summary>Payload for NewAsset/OwnershipChange/MetadataChange approvals.</summary>
public record AssetApprovalPayload(string? OwnerName = null, string? OwnerEmail = null,
    string? BusinessUnit = null, string? Department = null, string? Criticality = null,
    string? Classification = null);

/// <summary>Payload for AssetMerge approvals.</summary>
public record MergeApprovalPayload(Guid SurvivorId, Guid DuplicateId);

public class ApprovalService : IApprovalService
{
    private readonly IUnitOfWork _uow;
    private readonly IMergeEngine _merge;
    private readonly IEventBus _events;
    private readonly IAuditService _audit;
    private readonly ILogger<ApprovalService> _logger;

    public ApprovalService(IUnitOfWork uow, IMergeEngine merge, IEventBus events, IAuditService audit,
        ILogger<ApprovalService> logger)
    {
        _uow = uow;
        _merge = merge;
        _events = events;
        _audit = audit;
        _logger = logger;
    }

    public async Task<ApprovalRequest> CreateAsync(ApprovalType type, Guid? assetId, object payload,
        string requestedBy, string? justification = null, CancellationToken ct = default)
    {
        // One pending request per (type, asset) — avoid queues full of duplicates.
        if (assetId is not null)
        {
            var existing = await _uow.Approvals.FirstOrDefaultAsync(a =>
                a.Type == type && a.AssetId == assetId && a.Status == ApprovalStatus.Pending, ct);
            if (existing is not null) return existing;
        }

        var request = new ApprovalRequest
        {
            Type = type,
            AssetId = assetId,
            PayloadJson = JsonSerializer.Serialize(payload),
            RequestedBy = requestedBy,
            Justification = justification,
            CreatedBy = requestedBy
        };
        await _uow.Approvals.AddAsync(request, ct);
        await _uow.SaveChangesAsync(ct);
        await _events.PublishAsync(EventTopics.ApprovalRequested,
            new { RequestId = request.Id, Type = type.ToString(), AssetId = assetId }, ct);
        return request;
    }

    public async Task<ApprovalRequest?> DecideAsync(Guid requestId, bool approve, string decidedBy,
        string? comment = null, CancellationToken ct = default)
    {
        var request = await _uow.Approvals.GetByIdAsync(requestId, ct);
        if (request is null || request.Status != ApprovalStatus.Pending) return null;

        request.Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        request.DecidedBy = decidedBy;
        request.DecidedAt = DateTime.UtcNow;
        request.DecisionComment = comment;
        _uow.Approvals.Update(request);

        if (approve) await ApplyAsync(request, decidedBy, ct);
        else await ApplyRejectionAsync(request, ct);

        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.ConfigurationChanged, nameof(ApprovalRequest), request.Id.ToString(),
            new { request.Type, request.Status, decidedBy }, ct);
        await _events.PublishAsync(EventTopics.ApprovalDecided, new
        {
            RequestId = request.Id,
            Type = request.Type.ToString(),
            Status = request.Status.ToString(),
            request.AssetId,
            decidedBy
        }, ct);
        return request;
    }

    public async Task<List<ApprovalRequest>> GetPendingAsync(CancellationToken ct = default)
    {
        var pending = await _uow.Approvals.ListAsync(a => a.Status == ApprovalStatus.Pending, ct);
        return pending.OrderBy(a => a.CreatedAt).ToList();
    }

    private async Task ApplyAsync(ApprovalRequest request, string decidedBy, CancellationToken ct)
    {
        switch (request.Type)
        {
            case ApprovalType.NewAsset:
            case ApprovalType.OwnershipChange:
            case ApprovalType.MetadataChange:
            {
                if (request.AssetId is null) return;
                var asset = await _uow.Assets.GetWithDetailsAsync(request.AssetId.Value, ct);
                if (asset is null) return;

                var payload = Deserialize<AssetApprovalPayload>(request.PayloadJson);
                if (payload is not null)
                {
                    asset.OwnerName = payload.OwnerName ?? asset.OwnerName;
                    asset.OwnerEmail = payload.OwnerEmail ?? asset.OwnerEmail;
                    asset.BusinessUnit = payload.BusinessUnit ?? asset.BusinessUnit;
                    asset.Department = payload.Department ?? asset.Department;
                    asset.Classification = payload.Classification ?? asset.Classification;
                    if (payload.Criticality is not null &&
                        Enum.TryParse<CriticalityLevel>(payload.Criticality, true, out var criticality))
                        asset.Criticality = criticality;
                }
                if (request.Type == ApprovalType.NewAsset)
                    asset.LifecycleStatus = LifecycleStatus.Active; // activation gate passed
                asset.UpdatedAt = DateTime.UtcNow;
                asset.UpdatedBy = decidedBy;
                _uow.Assets.Update(asset);
                break;
            }
            case ApprovalType.AssetMerge:
            {
                var payload = Deserialize<MergeApprovalPayload>(request.PayloadJson);
                if (payload is null) return;
                var survivor = await _uow.Assets.GetWithDetailsAsync(payload.SurvivorId, ct);
                var duplicate = await _uow.Assets.GetWithDetailsAsync(payload.DuplicateId, ct);
                if (survivor is null || duplicate is null || duplicate.IsDeleted) return;
                await _merge.MergeAssetsAsync(survivor, duplicate, decidedBy, ct);
                _uow.Assets.Update(survivor);
                _uow.Assets.Update(duplicate);
                await _events.PublishAsync(EventTopics.AssetMerged,
                    new { SurvivorId = survivor.Id, DuplicateId = duplicate.Id, decidedBy }, ct);
                break;
            }
        }
    }

    private async Task ApplyRejectionAsync(ApprovalRequest request, CancellationToken ct)
    {
        // A rejected new-asset request quarantines the record for investigation.
        if (request.Type == ApprovalType.NewAsset && request.AssetId is not null)
        {
            var asset = await _uow.Assets.GetByIdAsync(request.AssetId.Value, ct);
            if (asset is not null)
            {
                asset.Status = AssetStatus.Quarantined;
                _uow.Assets.Update(asset);
            }
        }
    }

    private T? Deserialize<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Invalid approval payload: {Json}", json);
            return null;
        }
    }
}
