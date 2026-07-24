using Asp.Versioning;
using Esar.Application.Abstractions;
using Esar.Application.Auditing;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Esar.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/asset-groups")]
public class AssetGroupsController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly IAuditService _audit;
    private readonly ICurrentUserService _user;

    public AssetGroupsController(IUnitOfWork uow, IAuditService audit, ICurrentUserService user)
    {
        _uow = uow;
        _audit = audit;
        _user = user;
    }

    [HttpGet]
    [Authorize("assets.read")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var groups = await _uow.AssetGroups.ListAsync(null, ct);
        var counts = (await _uow.AssetGroupMembers.ListAsync(null, ct))
            .GroupBy(m => m.AssetGroupId).ToDictionary(g => g.Key, g => g.Count());
        return Ok(groups.OrderBy(g => g.Name).Select(g => new
        {
            g.Id, g.Name, g.Description, MemberCount = counts.GetValueOrDefault(g.Id), g.UpdatedAt, g.UpdatedBy
        }));
    }

    [HttpGet("{id:guid}/members")]
    [Authorize("assets.read")]
    public async Task<IActionResult> Members(Guid id, CancellationToken ct)
    {
        if (await _uow.AssetGroups.GetByIdAsync(id, ct) is null) return NotFound();
        var memberIds = (await _uow.AssetGroupMembers.ListAsync(m => m.AssetGroupId == id, ct))
            .Select(m => m.AssetId).ToList();
        if (memberIds.Count == 0) return Ok(Array.Empty<object>());
        var assets = await _uow.Assets.ListAsync(a => memberIds.Contains(a.Id), ct);
        return Ok(assets.OrderBy(a => a.Hostname).Select(a => new
        {
            a.Id, a.Hostname, AssetType = a.AssetType.ToString(), Environment = a.Environment.ToString(),
            Criticality = a.Criticality.ToString(), Status = a.Status.ToString()
        }));
    }

    public record GroupRequest(string Name, string? Description);

    [HttpPost]
    [Authorize("assets.write")]
    public async Task<IActionResult> Create([FromBody] GroupRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest(new { error = "Name is required." });
        var name = request.Name.Trim();
        if (await _uow.AssetGroups.FirstOrDefaultAsync(g => g.Name == name, ct) is not null)
            return BadRequest(new { error = $"A group named '{name}' already exists." });

        var group = new AssetGroup { Name = name, Description = request.Description, CreatedBy = _user.UserName };
        await _uow.AssetGroups.AddAsync(group, ct);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.ConfigurationChanged, nameof(AssetGroup), group.Id.ToString(), request, ct);
        return Ok(new { group.Id, group.Name, group.Description, MemberCount = 0 });
    }

    [HttpPut("{id:guid}")]
    [Authorize("assets.write")]
    public async Task<IActionResult> Update(Guid id, [FromBody] GroupRequest request, CancellationToken ct)
    {
        var group = await _uow.AssetGroups.GetByIdAsync(id, ct);
        if (group is null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest(new { error = "Name is required." });
        group.Name = request.Name.Trim();
        group.Description = request.Description;
        group.UpdatedAt = DateTime.UtcNow;
        group.UpdatedBy = _user.UserName;
        _uow.AssetGroups.Update(group);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.ConfigurationChanged, nameof(AssetGroup), id.ToString(), request, ct);
        return Ok(new { group.Id, group.Name, group.Description });
    }

    [HttpDelete("{id:guid}")]
    [Authorize("assets.write")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var group = await _uow.AssetGroups.GetByIdAsync(id, ct);
        if (group is null) return NotFound();
        _uow.AssetGroups.Remove(group); // members cascade-delete
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.ConfigurationChanged, nameof(AssetGroup), id.ToString(), null, ct);
        return NoContent();
    }

    public record MemberIds(List<Guid> AssetIds);

    [HttpPost("{id:guid}/members")]
    [Authorize("assets.write")]
    public async Task<IActionResult> AddMembers(Guid id, [FromBody] MemberIds request, CancellationToken ct)
    {
        var group = await _uow.AssetGroups.GetByIdAsync(id, ct);
        if (group is null) return NotFound();
        var ids = (request.AssetIds ?? new List<Guid>()).Distinct().ToList();
        if (ids.Count == 0) return Ok(new { added = 0 });

        // Only add assets that exist (not soft-deleted) and are not already members.
        var existing = (await _uow.Assets.ListAsync(a => ids.Contains(a.Id), ct)).Select(a => a.Id).ToHashSet();
        var already = (await _uow.AssetGroupMembers.ListAsync(m => m.AssetGroupId == id, ct))
            .Select(m => m.AssetId).ToHashSet();
        var toAdd = existing.Where(a => !already.Contains(a)).ToList();
        foreach (var assetId in toAdd)
            await _uow.AssetGroupMembers.AddAsync(
                new AssetGroupMember { AssetGroupId = id, AssetId = assetId, AddedBy = _user.UserName }, ct);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.ConfigurationChanged, nameof(AssetGroup), id.ToString(),
            new { addedMembers = toAdd.Count }, ct);
        return Ok(new { added = toAdd.Count });
    }

    [HttpDelete("{id:guid}/members/{assetId:guid}")]
    [Authorize("assets.write")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid assetId, CancellationToken ct)
    {
        var member = await _uow.AssetGroupMembers.FirstOrDefaultAsync(
            m => m.AssetGroupId == id && m.AssetId == assetId, ct);
        if (member is null) return NotFound();
        _uow.AssetGroupMembers.Remove(member);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.ConfigurationChanged, nameof(AssetGroup), id.ToString(),
            new { removedMember = assetId }, ct);
        return NoContent();
    }
}
