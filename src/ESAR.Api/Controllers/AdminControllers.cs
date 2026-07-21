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
[Route("api/v{version:apiVersion}/users")]
public class UsersController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly IPasswordHasher _hasher;
    private readonly IAuditService _audit;

    public UsersController(IUnitOfWork uow, IPasswordHasher hasher, IAuditService audit)
    {
        _uow = uow;
        _hasher = hasher;
        _audit = audit;
    }

    [HttpGet]
    [Authorize("users.manage")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var users = await _uow.Users.ListAsync(null, ct);
        var roles = (await _uow.Roles.ListAsync(null, ct)).ToDictionary(r => r.Id, r => r.Name);
        var links = await _uow.UserRoles.ListAsync(null, ct);
        var userRoles = links.GroupBy(l => l.UserId).ToDictionary(g => g.Key,
            g => g.Select(l => roles.TryGetValue(l.RoleId, out var name) ? name : null)
                .Where(n => n is not null).Cast<string>().ToList());

        return Ok(users.Select(u => new
        {
            u.Id, u.Username, u.Email, u.DisplayName, Provider = u.AuthProvider.ToString(),
            u.IsActive, u.MfaEnabled, u.LastLoginAt,
            Roles = userRoles.TryGetValue(u.Id, out var r) ? r : new List<string>()
        }));
    }

    public record CreateUserRequest(string Username, string Email, string DisplayName, string Password,
        List<string> Roles);

    [HttpPost]
    [Authorize("users.manage")]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        if (request.Password.Length < 12)
            return BadRequest(new { error = "Password must be at least 12 characters." });
        var existing = await _uow.Users.FirstOrDefaultAsync(
            u => u.Username == request.Username || u.Email == request.Email, ct);
        if (existing is not null) return Conflict(new { error = "Username or email already exists." });

        var user = new User
        {
            Username = request.Username.Trim(),
            Email = request.Email.Trim(),
            DisplayName = request.DisplayName.Trim(),
            PasswordHash = _hasher.Hash(request.Password),
            AuthProvider = AuthProvider.Local
        };
        await _uow.Users.AddAsync(user, ct);
        var roles = await _uow.Roles.ListAsync(r => request.Roles.Contains(r.Name), ct);
        foreach (var role in roles)
            await _uow.UserRoles.AddAsync(new UserRole { UserId = user.Id, RoleId = role.Id }, ct);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.UserCreated, nameof(User), user.Id.ToString(),
            new { user.Username, request.Roles }, ct);
        return Ok(new { user.Id, user.Username });
    }

    public record UpdateUserRequest(string? DisplayName, bool? IsActive, List<string>? Roles, string? NewPassword);

    [HttpPut("{id:guid}")]
    [Authorize("users.manage")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        var user = await _uow.Users.GetByIdAsync(id, ct);
        if (user is null) return NotFound();
        if (request.DisplayName is not null) user.DisplayName = request.DisplayName;
        if (request.IsActive is { } active) user.IsActive = active;
        if (!string.IsNullOrEmpty(request.NewPassword))
        {
            if (request.NewPassword.Length < 12)
                return BadRequest(new { error = "Password must be at least 12 characters." });
            user.PasswordHash = _hasher.Hash(request.NewPassword);
        }
        if (request.Roles is not null)
        {
            var allRoles = await _uow.Roles.ListAsync(null, ct);
            var links = await _uow.UserRoles.ListAsync(ur => ur.UserId == user.Id, ct);
            foreach (var role in allRoles)
            {
                var link = links.FirstOrDefault(ur => ur.RoleId == role.Id);
                var shouldHave = request.Roles.Contains(role.Name);
                if (shouldHave && link is null)
                    await _uow.UserRoles.AddAsync(new UserRole { UserId = user.Id, RoleId = role.Id }, ct);
                else if (!shouldHave && link is not null)
                    _uow.UserRoles.Remove(link);
            }
        }
        user.UpdatedAt = DateTime.UtcNow;
        _uow.Users.Update(user);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.UserUpdated, nameof(User), user.Id.ToString(), null, ct);
        return Ok(new { user.Id });
    }
}

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/roles")]
public class RolesController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly IAuditService _audit;

    public RolesController(IUnitOfWork uow, IAuditService audit)
    {
        _uow = uow;
        _audit = audit;
    }

    [HttpGet]
    [Authorize("roles.manage")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var roles = await _uow.Roles.ListAsync(null, ct);
        var permissions = await _uow.Permissions.ListAsync(null, ct);
        var links = await _uow.RolePermissions.ListAsync(null, ct);
        var permissionsById = permissions.ToDictionary(p => p.Id, p => p.Code);
        return Ok(roles.Select(r => new
        {
            r.Id, r.Name, r.Description, r.IsSystem,
            Permissions = links.Where(l => l.RoleId == r.Id)
                .Select(rp => permissionsById.TryGetValue(rp.PermissionId, out var code) ? code : null)
                .Where(c => c is not null)
        }));
    }

    [HttpGet("permissions")]
    [Authorize("roles.manage")]
    public async Task<IActionResult> Permissions(CancellationToken ct)
        => Ok((await _uow.Permissions.ListAsync(null, ct)).Select(p => new { p.Id, p.Code, p.Description }));

    public record RoleRequest(string Name, string? Description, List<string> Permissions);

    [HttpPost]
    [Authorize("roles.manage")]
    public async Task<IActionResult> Create([FromBody] RoleRequest request, CancellationToken ct)
    {
        if (await _uow.Roles.FirstOrDefaultAsync(r => r.Name == request.Name, ct) is not null)
            return Conflict(new { error = "Role already exists." });
        var role = new Role { Name = request.Name.Trim(), Description = request.Description };
        await _uow.Roles.AddAsync(role, ct);
        var permissions = await _uow.Permissions.ListAsync(p => request.Permissions.Contains(p.Code), ct);
        foreach (var permission in permissions)
            await _uow.RolePermissions.AddAsync(
                new RolePermission { RoleId = role.Id, PermissionId = permission.Id }, ct);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.RoleChanged, nameof(Role), role.Id.ToString(),
            new { action = "created", role.Name }, ct);
        return Ok(new { role.Id, role.Name });
    }

    [HttpPut("{id:guid}")]
    [Authorize("roles.manage")]
    public async Task<IActionResult> Update(Guid id, [FromBody] RoleRequest request, CancellationToken ct)
    {
        var role = await _uow.Roles.GetByIdAsync(id, ct);
        if (role is null) return NotFound();
        if (role.IsSystem) return BadRequest(new { error = "Built-in roles cannot be modified." });

        role.Description = request.Description;
        var permissions = await _uow.Permissions.ListAsync(null, ct);
        var existingLinks = await _uow.RolePermissions.ListAsync(rp => rp.RoleId == role.Id, ct);
        foreach (var link in existingLinks) _uow.RolePermissions.Remove(link);
        foreach (var permission in permissions.Where(p => request.Permissions.Contains(p.Code)))
            await _uow.RolePermissions.AddAsync(
                new RolePermission { RoleId = role.Id, PermissionId = permission.Id }, ct);
        role.UpdatedAt = DateTime.UtcNow;
        _uow.Roles.Update(role);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.RoleChanged, nameof(Role), role.Id.ToString(),
            new { action = "updated" }, ct);
        return Ok(new { role.Id });
    }
}

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/audit")]
public class AuditController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    public AuditController(IUnitOfWork uow) => _uow = uow;

    [HttpGet]
    [Authorize("audit.read")]
    public async Task<IActionResult> List([FromQuery] string? action, [FromQuery] string? user,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        var hasAction = Enum.TryParse<AuditAction>(action, true, out var act);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var result = await _uow.AuditLogs.PageAsync(q =>
        {
            if (from != null) q = q.Where(l => l.Timestamp >= from);
            if (to != null) q = q.Where(l => l.Timestamp <= to);
            if (hasAction) q = q.Where(l => l.Action == act);
            if (!string.IsNullOrWhiteSpace(user))
            {
                var term = user.ToLower();
                q = q.Where(l => l.UserName.ToLower().Contains(term));
            }
            return q.OrderByDescending(l => l.Timestamp);
        }, page, pageSize, ct);
        return Ok(new
        {
            totalCount = result.TotalCount,
            page = result.Page,
            pageSize = result.PageSize,
            items = result.Items.Select(l => new
            {
                l.Id, l.UserName, Action = l.Action.ToString(), l.EntityType, l.EntityId,
                l.Details, l.IpAddress, l.Timestamp
            })
        });
    }
}

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/settings")]
public class SettingsController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly IAuditService _audit;
    private readonly ICurrentUserService _user;
    private readonly ICacheService _cache;

    public SettingsController(IUnitOfWork uow, IAuditService audit, ICurrentUserService user, ICacheService cache)
    {
        _uow = uow;
        _audit = audit;
        _user = user;
        _cache = cache;
    }

    [HttpGet]
    [Authorize("settings.manage")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var settings = await _uow.Settings.ListAsync(null, ct);
        return Ok(settings.Select(s => new
        {
            s.Id, s.Key, Value = s.IsEncrypted ? "***" : s.Value, s.Description, s.UpdatedBy, s.UpdatedAt
        }));
    }

    public record SettingUpdate(string Value);

    [HttpPut("{key}")]
    [Authorize("settings.manage")]
    public async Task<IActionResult> Update(string key, [FromBody] SettingUpdate update, CancellationToken ct)
    {
        var setting = await _uow.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null) return NotFound();
        setting.Value = update.Value;
        setting.UpdatedBy = _user.UserName;
        setting.UpdatedAt = DateTime.UtcNow;
        _uow.Settings.Update(setting);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.ConfigurationChanged, nameof(Setting), key, null, ct);
        return Ok(new { setting.Key });
    }

    /// <summary>Source priority configuration (which source wins per attribute).</summary>
    [HttpGet("source-priorities")]
    [Authorize("settings.manage")]
    public async Task<IActionResult> SourcePriorities(CancellationToken ct)
    {
        var priorities = await _uow.SourcePriorities.ListAsync(null, ct);
        return Ok(priorities.OrderBy(p => p.Priority).Select(p => new
        {
            p.Id, Connector = p.ConnectorType.ToString(), p.Attribute, p.Priority
        }));
    }

    public record PriorityUpdate(int Priority);

    [HttpPut("source-priorities/{id:guid}")]
    [Authorize("settings.manage")]
    public async Task<IActionResult> UpdatePriority(Guid id, [FromBody] PriorityUpdate update, CancellationToken ct)
    {
        var priority = await _uow.SourcePriorities.GetByIdAsync(id, ct);
        if (priority is null) return NotFound();
        priority.Priority = update.Priority;
        priority.UpdatedAt = DateTime.UtcNow;
        _uow.SourcePriorities.Update(priority);
        await _uow.SaveChangesAsync(ct);
        await _cache.RemoveAsync(Esar.Application.Matching.CacheKeys.SourcePriorities, ct);
        await _audit.LogAsync(AuditAction.ConfigurationChanged, nameof(SourcePriority), id.ToString(), update, ct);
        return Ok(new { priority.Id, priority.Priority });
    }
}
