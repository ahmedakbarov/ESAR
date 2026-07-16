using System.Text.Json;
using Asp.Versioning;
using Esar.Application.Abstractions;
using Esar.Application.Approvals;
using Esar.Application.Auditing;
using Esar.Application.Compliance;
using Esar.Application.Relationships;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Esar.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/relationships")]
public class RelationshipsController : ControllerBase
{
    private readonly IRelationshipService _relationships;
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUserService _user;

    public RelationshipsController(IRelationshipService relationships, IUnitOfWork uow, ICurrentUserService user)
    {
        _relationships = relationships;
        _uow = uow;
        _user = user;
    }

    /// <summary>All active relationships touching an asset (both directions).</summary>
    [HttpGet("asset/{assetId:guid}")]
    [Authorize("assets.read")]
    public async Task<IActionResult> ForAsset(Guid assetId, CancellationToken ct)
    {
        var relationships = await _relationships.GetForAssetAsync(assetId, ct);
        var ids = relationships.SelectMany(r => new[] { r.SourceAssetId, r.TargetAssetId }).Distinct().ToList();
        var hostnames = (await _uow.Assets.ListAsync(a => ids.Contains(a.Id), ct))
            .ToDictionary(a => a.Id, a => a.Hostname);
        return Ok(relationships.Select(r => new
        {
            r.Id,
            r.SourceAssetId,
            SourceHostname = hostnames.GetValueOrDefault(r.SourceAssetId),
            r.TargetAssetId,
            TargetHostname = hostnames.GetValueOrDefault(r.TargetAssetId),
            Type = r.Type.ToString(),
            r.Description,
            Source = r.Source.ToString(),
            r.LastSeen
        }));
    }

    /// <summary>Impact analysis: downstream blast radius + upstream dependencies.</summary>
    [HttpGet("asset/{assetId:guid}/impact")]
    [Authorize("assets.read")]
    public async Task<IActionResult> Impact(Guid assetId, [FromQuery] int depth = 3, CancellationToken ct = default)
        => Ok(await _relationships.AnalyzeImpactAsync(assetId, depth, ct));

    public record RelationshipRequest(Guid SourceAssetId, Guid TargetAssetId, string Type, string? Description);

    [HttpPost]
    [Authorize("relationships.manage")]
    public async Task<IActionResult> Create([FromBody] RelationshipRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<RelationshipType>(request.Type, true, out var type))
            return BadRequest(new { error = $"Unknown relationship type '{request.Type}'.",
                validTypes = Enum.GetNames<RelationshipType>() });
        var relationship = await _relationships.AddAsync(request.SourceAssetId, request.TargetAssetId, type,
            request.Description, _user.UserName, ConnectorType.ManualImport, ct);
        return relationship is null
            ? BadRequest(new { error = "Both assets must exist and differ." })
            : Ok(new { relationship.Id });
    }

    [HttpDelete("{id:guid}")]
    [Authorize("relationships.manage")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => await _relationships.RemoveAsync(id, ct) ? NoContent() : NotFound();

    [HttpGet("types")]
    [Authorize("assets.read")]
    public IActionResult Types() => Ok(Enum.GetNames<RelationshipType>());
}

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/policies")]
public class PoliciesController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly ICacheService _cache;
    private readonly IAuditService _audit;
    private readonly ICurrentUserService _user;

    public PoliciesController(IUnitOfWork uow, ICacheService cache, IAuditService audit, ICurrentUserService user)
    {
        _uow = uow;
        _cache = cache;
        _audit = audit;
        _user = user;
    }

    [HttpGet]
    [Authorize("compliance.read")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var policies = await _uow.CompliancePolicies.ListAsync(null, ct);
        return Ok(policies.OrderBy(p => p.Priority).Select(ToDto));
    }

    public record PolicyRequest(string Name, string? Description, bool Enabled, int Priority,
        List<string> AppliesToAssetTypes, List<string> AppliesToEnvironments, string? MinCriticality,
        List<string> RequiredControls, List<string> MandatoryControls);

    [HttpPost]
    [Authorize("policies.manage")]
    public async Task<IActionResult> Create([FromBody] PolicyRequest request, CancellationToken ct)
    {
        var validationError = Validate(request);
        if (validationError is not null) return BadRequest(new { error = validationError });

        var policy = new CompliancePolicy { CreatedBy = _user.UserName };
        Apply(policy, request);
        await _uow.CompliancePolicies.AddAsync(policy, ct);
        await _uow.SaveChangesAsync(ct);
        await InvalidateAndAuditAsync(policy, "created", ct);
        return Ok(ToDto(policy));
    }

    [HttpPut("{id:guid}")]
    [Authorize("policies.manage")]
    public async Task<IActionResult> Update(Guid id, [FromBody] PolicyRequest request, CancellationToken ct)
    {
        var policy = await _uow.CompliancePolicies.GetByIdAsync(id, ct);
        if (policy is null) return NotFound();
        var validationError = Validate(request);
        if (validationError is not null) return BadRequest(new { error = validationError });

        Apply(policy, request);
        policy.Version++;
        policy.UpdatedAt = DateTime.UtcNow;
        policy.UpdatedBy = _user.UserName;
        _uow.CompliancePolicies.Update(policy);
        await _uow.SaveChangesAsync(ct);
        await InvalidateAndAuditAsync(policy, "updated", ct);
        return Ok(ToDto(policy));
    }

    [HttpDelete("{id:guid}")]
    [Authorize("policies.manage")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var policy = await _uow.CompliancePolicies.GetByIdAsync(id, ct);
        if (policy is null) return NotFound();
        _uow.CompliancePolicies.Remove(policy);
        await _uow.SaveChangesAsync(ct);
        await InvalidateAndAuditAsync(policy, "deleted", ct);
        return NoContent();
    }

    private static string? Validate(PolicyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "Name is required.";
        if (request.RequiredControls.Count == 0) return "At least one required control is needed.";
        foreach (var control in request.RequiredControls.Concat(request.MandatoryControls))
            if (!Enum.TryParse<ControlType>(control, true, out _))
                return $"Unknown control '{control}'. Valid: {string.Join(", ", Enum.GetNames<ControlType>())}";
        foreach (var type in request.AppliesToAssetTypes)
            if (!Enum.TryParse<AssetType>(type, true, out _))
                return $"Unknown asset type '{type}'.";
        if (request.MandatoryControls.Any(m => !request.RequiredControls.Contains(m, StringComparer.OrdinalIgnoreCase)))
            return "Every mandatory control must also be in the required controls list.";
        return null;
    }

    private static void Apply(CompliancePolicy policy, PolicyRequest request)
    {
        policy.Name = request.Name.Trim();
        policy.Description = request.Description;
        policy.Enabled = request.Enabled;
        policy.Priority = request.Priority;
        policy.AppliesToAssetTypesJson = JsonSerializer.Serialize(request.AppliesToAssetTypes);
        policy.AppliesToEnvironmentsJson = JsonSerializer.Serialize(request.AppliesToEnvironments);
        policy.MinCriticality = Enum.TryParse<CriticalityLevel>(request.MinCriticality, true, out var crit)
            ? crit : null;
        policy.RequiredControlsJson = JsonSerializer.Serialize(request.RequiredControls);
        policy.MandatoryControlsJson = JsonSerializer.Serialize(request.MandatoryControls);
    }

    private async Task InvalidateAndAuditAsync(CompliancePolicy policy, string action, CancellationToken ct)
    {
        await _cache.RemoveAsync(PolicyEngine.PoliciesCacheKey, ct);
        await _audit.LogAsync(AuditAction.ConfigurationChanged, nameof(CompliancePolicy), policy.Id.ToString(),
            new { action, policy.Name, policy.Version }, ct);
    }

    private static object ToDto(CompliancePolicy p) => new
    {
        p.Id, p.Name, p.Description, p.Enabled, p.Priority, p.Version,
        AppliesToAssetTypes = JsonSerializer.Deserialize<List<string>>(p.AppliesToAssetTypesJson),
        AppliesToEnvironments = JsonSerializer.Deserialize<List<string>>(p.AppliesToEnvironmentsJson),
        MinCriticality = p.MinCriticality?.ToString(),
        RequiredControls = JsonSerializer.Deserialize<List<string>>(p.RequiredControlsJson),
        MandatoryControls = JsonSerializer.Deserialize<List<string>>(p.MandatoryControlsJson),
        p.UpdatedAt, p.UpdatedBy
    };
}

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/approvals")]
public class ApprovalsController : ControllerBase
{
    private readonly IApprovalService _approvals;
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUserService _user;

    public ApprovalsController(IApprovalService approvals, IUnitOfWork uow, ICurrentUserService user)
    {
        _approvals = approvals;
        _uow = uow;
        _user = user;
    }

    [HttpGet("pending")]
    [Authorize("assets.read")]
    public async Task<IActionResult> Pending(CancellationToken ct)
    {
        var pending = await _approvals.GetPendingAsync(ct);
        var assetIds = pending.Where(p => p.AssetId != null).Select(p => p.AssetId!.Value).Distinct().ToList();
        var assets = (await _uow.Assets.ListAsync(a => assetIds.Contains(a.Id), ct)).ToDictionary(a => a.Id);
        return Ok(pending.Select(p => new
        {
            p.Id,
            Type = p.Type.ToString(),
            p.AssetId,
            Hostname = p.AssetId is { } id && assets.TryGetValue(id, out var asset) ? asset.Hostname : null,
            p.RequestedBy,
            p.Justification,
            Payload = JsonSerializer.Deserialize<object>(p.PayloadJson),
            RequestedAt = p.CreatedAt
        }));
    }

    public record DecisionRequest(string? Comment, AssetApprovalPayload? Overrides);

    /// <summary>Approves a request; optional Overrides let the approver set owner/BU/criticality inline.</summary>
    [HttpPost("{id:guid}/approve")]
    [Authorize("approvals.decide")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] DecisionRequest? request, CancellationToken ct)
    {
        // Approver-supplied metadata replaces the payload before it is applied.
        if (request?.Overrides is not null)
        {
            var pending = await _uow.Approvals.GetByIdAsync(id, ct);
            if (pending is null || pending.Status != ApprovalStatus.Pending) return NotFound();
            pending.PayloadJson = JsonSerializer.Serialize(request.Overrides);
            _uow.Approvals.Update(pending);
            await _uow.SaveChangesAsync(ct);
        }
        var decided = await _approvals.DecideAsync(id, true, _user.UserName, request?.Comment, ct);
        return decided is null ? NotFound() : Ok(new { decided.Id, Status = decided.Status.ToString() });
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize("approvals.decide")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] DecisionRequest? request, CancellationToken ct)
    {
        var decided = await _approvals.DecideAsync(id, false, _user.UserName, request?.Comment, ct);
        return decided is null ? NotFound() : Ok(new { decided.Id, Status = decided.Status.ToString() });
    }

    /// <summary>Requests a merge that must be approved by a second person (four-eyes principle).</summary>
    public record MergeRequestBody(Guid SurvivorId, Guid DuplicateId, string? Justification);

    [HttpPost("request-merge")]
    [Authorize("assets.merge")]
    public async Task<IActionResult> RequestMerge([FromBody] MergeRequestBody body, CancellationToken ct)
    {
        if (body.SurvivorId == body.DuplicateId)
            return BadRequest(new { error = "Survivor and duplicate must differ." });
        var request = await _approvals.CreateAsync(ApprovalType.AssetMerge, body.SurvivorId,
            new MergeApprovalPayload(body.SurvivorId, body.DuplicateId), _user.UserName, body.Justification, ct);
        return Ok(new { request.Id, Status = request.Status.ToString() });
    }
}
