using Asp.Versioning;
using Esar.Application.Assets;
using Esar.Application.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Esar.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/assets")]
public class AssetsController : ControllerBase
{
    private readonly ISender _mediator;
    public AssetsController(ISender mediator) => _mediator = mediator;

    /// <summary>Searches assets with filtering, sorting and pagination.</summary>
    [HttpGet]
    [Authorize("assets.read")]
    public async Task<ActionResult<PagedResult<AssetDto>>> Search([FromQuery] AssetSearchCriteria criteria,
        CancellationToken ct)
        => Ok(await _mediator.Send(new SearchAssetsQuery(criteria), ct));

    /// <summary>Distinct values of one filterable column — feeds the Excel-style column filters.</summary>
    [HttpGet("filter-values")]
    [Authorize("assets.read")]
    public async Task<IActionResult> FilterValues([FromQuery] string field,
        [FromServices] Esar.Application.Abstractions.IUnitOfWork uow, CancellationToken ct)
        => Ok(await uow.Assets.ListFilterValuesAsync(field, ct));

    /// <summary>Returns the full golden record for one asset.</summary>
    [HttpGet("{id:guid}")]
    [Authorize("assets.read")]
    public async Task<ActionResult<AssetDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var asset = await _mediator.Send(new GetAssetByIdQuery(id), ct);
        return asset is null ? NotFound() : Ok(asset);
    }

    /// <summary>Returns the field-level change history of an asset.</summary>
    [HttpGet("{id:guid}/history")]
    [Authorize("assets.read")]
    public async Task<IActionResult> History(Guid id, CancellationToken ct)
        => Ok(await _mediator.Send(new GetAssetHistoryQuery(id), ct));

    /// <summary>
    /// Unified timeline: field changes, matching decisions and compliance evaluations
    /// merged chronologically — the full lifecycle from discovery to retirement.
    /// </summary>
    [HttpGet("{id:guid}/timeline")]
    [Authorize("assets.read")]
    public async Task<IActionResult> Timeline(Guid id,
        [FromServices] Esar.Application.Abstractions.IUnitOfWork uow,
        [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var history = await uow.AssetHistories.ListAsync(h => h.AssetId == id, ct);
        var matches = await uow.MatchRecords.ListAsync(m => m.MatchedAssetId == id || m.CreatedAssetId == id, ct);
        var compliance = await uow.AssetCompliance.ListAsync(c => c.AssetId == id, ct);

        var timeline = history
            .Select(h => new TimelineEntry(h.ChangedAt, "FieldChange",
                $"{h.FieldName}: '{h.OldValue ?? "∅"}' → '{h.NewValue ?? "∅"}'", h.ChangedBy))
            .Concat(matches.Select(m => new TimelineEntry(m.CreatedAt, "MatchingDecision",
                $"{m.Decision} from {m.SourceConnector} (score {m.ConfidenceScore:0.00})",
                m.ReviewedBy ?? "matching-engine")))
            .Concat(compliance.Select(c => new TimelineEntry(c.CheckedAt, "ComplianceCheck",
                $"{c.Control}: {c.Status} — {c.Details}", "compliance-engine")))
            .OrderByDescending(e => e.At)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToList();
        return Ok(timeline);
    }

    public record TimelineEntry(DateTime At, string Kind, string Summary, string Actor);

    [HttpPost]
    [Authorize("assets.write")]
    public async Task<ActionResult<AssetDto>> Create([FromBody] CreateAssetCommand command, CancellationToken ct)
    {
        var created = await _mediator.Send(command, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id, version = "1" }, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize("assets.write")]
    public async Task<ActionResult<AssetDto>> Update(Guid id, [FromBody] UpdateAssetCommand command,
        CancellationToken ct)
    {
        var updated = await _mediator.Send(command with { Id = id }, ct);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    [Authorize("assets.delete")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => await _mediator.Send(new DeleteAssetCommand(id), ct) ? NoContent() : NotFound();

    /// <summary>Merges a duplicate asset into a surviving golden record.</summary>
    [HttpPost("merge")]
    [Authorize("assets.merge")]
    public async Task<IActionResult> Merge([FromBody] MergeAssetsCommand command, CancellationToken ct)
        => await _mediator.Send(command, ct)
            ? Ok(new { merged = true })
            : BadRequest(new { error = "Merge failed — check that both assets exist and differ." });

    /// <summary>Bulk import; each row flows through the full ingestion (matching + dedup) pipeline.</summary>
    [HttpPost("bulk")]
    [Authorize("assets.import")]
    public async Task<ActionResult<BulkImportResult>> BulkImport([FromBody] BulkImportAssetsCommand command,
        CancellationToken ct)
        => Ok(await _mediator.Send(command, ct));

    public record BulkUpdateRequest(List<Guid> Ids, UpdateAssetCommand Changes);

    [HttpPut("bulk")]
    [Authorize("assets.write")]
    public async Task<IActionResult> BulkUpdate([FromBody] BulkUpdateRequest request, CancellationToken ct)
    {
        var updated = 0;
        foreach (var id in request.Ids.Distinct())
        {
            var result = await _mediator.Send(request.Changes with { Id = id }, ct);
            if (result is not null) updated++;
        }
        return Ok(new { updated, requested = request.Ids.Count });
    }

    public record BulkDeleteRequest(List<Guid> Ids);

    [HttpPost("bulk-delete")]
    [Authorize("assets.delete")]
    public async Task<IActionResult> BulkDelete([FromBody] BulkDeleteRequest request, CancellationToken ct)
    {
        var deleted = 0;
        foreach (var id in request.Ids.Distinct())
            if (await _mediator.Send(new DeleteAssetCommand(id), ct)) deleted++;
        return Ok(new { deleted, requested = request.Ids.Count });
    }
}
