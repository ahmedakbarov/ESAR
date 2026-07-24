using System.Globalization;
using Asp.Versioning;
using Esar.Application.Abstractions;
using Esar.Application.Auditing;
using Esar.Application.Matching;
using Esar.Domain.Common;
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
    private readonly ICurrentUserService _currentUser;

    public UsersController(IUnitOfWork uow, IPasswordHasher hasher, IAuditService audit, ICurrentUserService currentUser)
    {
        _uow = uow;
        _hasher = hasher;
        _audit = audit;
        _currentUser = currentUser;
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
            u.IsActive, u.IsProtected, u.MfaEnabled, u.LastLoginAt, u.FailedLoginAttempts, u.LockedOutUntil,
            Roles = userRoles.TryGetValue(u.Id, out var r) ? r : new List<string>()
        }));
    }

    public record CreateUserRequest(string Username, string Email, string DisplayName, string? Password,
        List<string> Roles, AuthProvider Provider = AuthProvider.Local);

    [HttpPost]
    [Authorize("users.manage")]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        if (request.Provider == AuthProvider.Local)
        {
            var minPasswordLength = await GetMinPasswordLengthAsync(ct);
            if (string.IsNullOrEmpty(request.Password) || request.Password.Length < minPasswordLength)
                return BadRequest(new { error = $"Password must be at least {minPasswordLength} characters." });
        }
        else if (!string.IsNullOrEmpty(request.Password))
        {
            // A federated account's credential lives with the external IdP (Entra ID/AD), not
            // here — accepting one would be silently ignored, which is worse than rejecting it.
            return BadRequest(new { error = "Password must not be set for a federated (Entra ID/AD) account." });
        }

        var existing = await _uow.Users.FirstOrDefaultAsync(
            u => u.Username == request.Username || u.Email == request.Email, ct);
        if (existing is not null) return Conflict(new { error = "Username or email already exists." });

        var user = new User
        {
            Username = request.Username.Trim(),
            Email = request.Email.Trim(),
            DisplayName = request.DisplayName.Trim(),
            PasswordHash = request.Provider == AuthProvider.Local ? _hasher.Hash(request.Password!) : null,
            AuthProvider = request.Provider
            // ExternalObjectId stays null for federated accounts — linked automatically on their
            // first successful Entra ID/AD login (AuthService.ResolveOrProvisionUserAsync).
        };
        await _uow.Users.AddAsync(user, ct);
        var roles = await _uow.Roles.ListAsync(r => request.Roles.Contains(r.Name), ct);
        foreach (var role in roles)
            await _uow.UserRoles.AddAsync(new UserRole { UserId = user.Id, RoleId = role.Id }, ct);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.UserCreated, nameof(User), user.Id.ToString(),
            new { user.Username, request.Roles, provider = request.Provider.ToString() }, ct);
        return Ok(new { user.Id, user.Username });
    }

    public record UpdateUserRequest(string? Email, string? DisplayName, bool? IsActive, List<string>? Roles,
        string? NewPassword);

    [HttpPut("{id:guid}")]
    [Authorize("users.manage")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        var user = await _uow.Users.GetByIdAsync(id, ct);
        if (user is null) return NotFound();
        if (user.IsProtected)
            return BadRequest(new { error = "This is a protected account and cannot be modified. " +
                "Its owner can change its own password from the top-bar account menu." });
        if (request.Email is not null)
        {
            var email = request.Email.Trim();
            if (string.IsNullOrWhiteSpace(email))
                return BadRequest(new { error = "Email is required." });
            var existing = await _uow.Users.FirstOrDefaultAsync(
                u => u.Id != id && u.Email.ToLower() == email.ToLower(), ct);
            if (existing is not null)
                return Conflict(new { error = "Email already exists." });
            user.Email = email;
        }
        if (request.DisplayName is not null) user.DisplayName = request.DisplayName.Trim();
        if (request.IsActive is { } active) user.IsActive = active;
        if (!string.IsNullOrEmpty(request.NewPassword))
        {
            var minPasswordLength = await GetMinPasswordLengthAsync(ct);
            if (request.NewPassword.Length < minPasswordLength)
                return BadRequest(new { error = $"Password must be at least {minPasswordLength} characters." });
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

    [HttpPost("{id:guid}/unlock")]
    [Authorize("users.manage")]
    public async Task<IActionResult> Unlock(Guid id, CancellationToken ct)
    {
        var user = await _uow.Users.GetByIdAsync(id, ct);
        if (user is null) return NotFound();
        if (user.AuthProvider != AuthProvider.Local)
            return BadRequest(new { error = "Only local password accounts can be unlocked in ESAR." });

        user.FailedLoginAttempts = 0;
        user.LockedOutUntil = null;
        user.UpdatedAt = DateTime.UtcNow;
        _uow.Users.Update(user);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.UserUpdated, nameof(User), user.Id.ToString(),
            new { action = "account_unlocked", user.Username }, ct);
        return Ok(new { user.Id });
    }

    [HttpDelete("{id:guid}")]
    [Authorize("users.manage")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var user = await _uow.Users.GetByIdAsync(id, ct);
        if (user is null) return NotFound();
        if (user.IsProtected)
            return BadRequest(new { error = "This is a protected account and cannot be deleted." });
        if (id == _currentUser.UserId) return BadRequest(new { error = "You cannot delete your own account." });
        if (await IsLastUserManagerAsync(id, ct))
            return BadRequest(new { error = "Cannot delete the last user with user-management permission." });

        var links = await _uow.UserRoles.ListAsync(ur => ur.UserId == id, ct);
        foreach (var link in links) _uow.UserRoles.Remove(link);
        _uow.Users.Remove(user);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.UserDeleted, nameof(User), user.Id.ToString(),
            new { user.Username, user.Email }, ct);
        return NoContent();
    }

    /// <summary>True if removing `excludingUserId` would leave zero active users holding "users.manage".
    /// Prevents locking the admin panel via self-service deletion of the last account able to reverse it.</summary>
    private async Task<bool> IsLastUserManagerAsync(Guid excludingUserId, CancellationToken ct)
    {
        var managePermission = await _uow.Permissions.FirstOrDefaultAsync(p => p.Code == "users.manage", ct);
        if (managePermission is null) return false;
        var roleIds = (await _uow.RolePermissions.ListAsync(rp => rp.PermissionId == managePermission.Id, ct))
            .Select(rp => rp.RoleId).ToHashSet();
        if (roleIds.Count == 0) return false;
        var managerUserIds = (await _uow.UserRoles.ListAsync(ur => roleIds.Contains(ur.RoleId), ct))
            .Select(ur => ur.UserId).ToHashSet();
        var activeManagerCount = (await _uow.Users.ListAsync(
            u => u.IsActive && managerUserIds.Contains(u.Id), ct)).Count;
        return activeManagerCount <= 1 && managerUserIds.Contains(excludingUserId);
    }

    private async Task<int> GetMinPasswordLengthAsync(CancellationToken ct)
    {
        var setting = await _uow.Settings.FirstOrDefaultAsync(
            s => s.Key == SettingKeys.SecurityPasswordMinLength, ct);
        return setting is not null && int.TryParse(setting.Value, out var value) && value > 0 ? value : 12;
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

    [HttpDelete("{id:guid}")]
    [Authorize("roles.manage")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var role = await _uow.Roles.GetByIdAsync(id, ct);
        if (role is null) return NotFound();
        if (role.IsSystem) return BadRequest(new { error = "Built-in roles cannot be deleted." });
        var assignees = await _uow.UserRoles.ListAsync(ur => ur.RoleId == id, ct);
        if (assignees.Count > 0)
            return BadRequest(new { error = $"Role is assigned to {assignees.Count} user(s); unassign it first." });

        var permissionLinks = await _uow.RolePermissions.ListAsync(rp => rp.RoleId == id, ct);
        foreach (var link in permissionLinks) _uow.RolePermissions.Remove(link);
        _uow.Roles.Remove(role);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.RoleChanged, nameof(Role), role.Id.ToString(),
            new { action = "deleted", role.Name }, ct);
        return NoContent();
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
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] bool excludeApiCalls = false,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        var hasAction = Enum.TryParse<AuditAction>(action, true, out var act);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var result = await _uow.AuditLogs.PageAsync(q =>
        {
            if (from != null) q = q.Where(l => l.Timestamp >= from);
            if (to != null) q = q.Where(l => l.Timestamp <= to);
            if (hasAction) q = q.Where(l => l.Action == act);
            // ApiCall rows mirror the underlying domain event and otherwise drown out real user
            // actions; the audit UI hides them by default via this flag.
            else if (excludeApiCalls) q = q.Where(l => l.Action != AuditAction.ApiCall);
            if (!string.IsNullOrWhiteSpace(user))
            {
                var term = user.ToLower();
                q = q.Where(l => l.UserName.ToLower().Contains(term));
            }
            return q.OrderByDescending(l => l.Timestamp);
        }, page, pageSize, ct);
        var items = result.Items.ToList();
        var names = await ResolveEntityNamesAsync(items, ct);
        return Ok(new
        {
            totalCount = result.TotalCount,
            page = result.Page,
            pageSize = result.PageSize,
            items = items.Select(l => new
            {
                l.Id, l.UserName, Action = l.Action.ToString(), l.EntityType, l.EntityId,
                EntityName = l.EntityType != null && l.EntityId != null &&
                    names.TryGetValue((l.EntityType, l.EntityId), out var nm) ? nm : null,
                l.Details, l.IpAddress, l.Timestamp
            })
        });
    }

    // Audit rows reference an entity by type + GUID; resolve a human-readable name per referenced
    // entity so the UI shows "web-prod-01" instead of a raw id. Batched: one query per type on the page.
    private async Task<Dictionary<(string Type, string Id), string>> ResolveEntityNamesAsync(
        IReadOnlyList<AuditLog> items, CancellationToken ct)
    {
        var byType = items
            .Where(i => i.EntityType != null && i.EntityId != null)
            .GroupBy(i => i.EntityType!)
            .ToDictionary(g => g.Key, g => g.Select(i => i.EntityId!).ToHashSet());

        var names = new Dictionary<(string, string), string>();

        async Task Add<T>(string type, IRepository<T> repo, Func<T, string?> name) where T : BaseEntity
        {
            if (!byType.TryGetValue(type, out var idStrings)) return;
            var ids = idStrings
                .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                .Where(g => g.HasValue).Select(g => g!.Value).Distinct().ToList();
            if (ids.Count == 0) return;
            foreach (var row in await repo.ListAsync(e => ids.Contains(e.Id), ct))
            {
                var label = name(row);
                if (!string.IsNullOrWhiteSpace(label)) names[(type, row.Id.ToString())] = label!;
            }
        }

        await Add<Asset>(nameof(Asset), _uow.Assets, a => a.Hostname);
        await Add<ConnectorConfig>(nameof(ConnectorConfig), _uow.Connectors, c => c.Name);
        await Add<CompliancePolicy>(nameof(CompliancePolicy), _uow.CompliancePolicies, p => p.Name);
        await Add<Incident>(nameof(Incident), _uow.Incidents, i => i.Title);
        await Add<User>(nameof(User), _uow.Users, u => u.UserName);
        await Add<Role>(nameof(Role), _uow.Roles, r => r.Name);
        await Add<MatchingRule>(nameof(MatchingRule), _uow.MatchingRules, r => r.Name);
        await Add<Report>(nameof(Report), _uow.Reports, r => r.Name);
        await Add<MatchRecord>(nameof(MatchRecord), _uow.MatchRecords, m => m.CandidateHostname);

        return names;
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
            s.Id, s.Key, Value = s.IsEncrypted ? "***" : s.Value, s.Description, s.IsEncrypted, s.UpdatedBy, s.UpdatedAt
        }));
    }

    public record SettingUpdate(string Value);

    [HttpPut("{key}")]
    [Authorize("settings.manage")]
    public async Task<IActionResult> Update(string key, [FromBody] SettingUpdate update, CancellationToken ct)
    {
        var setting = await _uow.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null) return NotFound();
        if (RequiresPositiveInteger(key) &&
            (!int.TryParse(update.Value, out var numericValue) || numericValue <= 0))
            return BadRequest(new { error = "Value must be a positive whole number." });
        if (key is SettingKeys.MatchAutoMergeThreshold or SettingKeys.MatchReviewThreshold
            or SettingKeys.MatchAmbiguityDelta)
        {
            if (!decimal.TryParse(update.Value, NumberStyles.Number, CultureInfo.InvariantCulture,
                    out var threshold) || threshold is < 0 or > 1)
                return BadRequest(new { error = "Matching thresholds must be decimal values between 0 and 1." });

            if (key == SettingKeys.MatchAmbiguityDelta)
            {
                setting.Value = update.Value;
                setting.UpdatedBy = _user.UserName;
                setting.UpdatedAt = DateTime.UtcNow;
                _uow.Settings.Update(setting);
                await _uow.SaveChangesAsync(ct);
                await _audit.LogAsync(AuditAction.ConfigurationChanged, nameof(Setting), key, null, ct);
                return Ok(new { setting.Key });
            }

            var otherKey = key == SettingKeys.MatchAutoMergeThreshold
                ? SettingKeys.MatchReviewThreshold
                : SettingKeys.MatchAutoMergeThreshold;
            var other = await _uow.Settings.FirstOrDefaultAsync(s => s.Key == otherKey, ct);
            if (other is not null &&
                decimal.TryParse(other.Value, NumberStyles.Number, CultureInfo.InvariantCulture,
                    out var otherThreshold))
            {
                var autoMerge = key == SettingKeys.MatchAutoMergeThreshold ? threshold : otherThreshold;
                var review = key == SettingKeys.MatchReviewThreshold ? threshold : otherThreshold;
                if (review > autoMerge)
                    return BadRequest(new
                    {
                        error = "Review threshold cannot be greater than the automatic merge threshold."
                    });
            }
        }
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
        if (update.Priority <= 0)
            return BadRequest(new { error = "Priority must be a positive whole number." });
        priority.Priority = update.Priority;
        priority.UpdatedAt = DateTime.UtcNow;
        _uow.SourcePriorities.Update(priority);
        await _uow.SaveChangesAsync(ct);
        await _cache.RemoveAsync(Esar.Application.Matching.CacheKeys.SourcePriorities, ct);
        await _audit.LogAsync(AuditAction.ConfigurationChanged, nameof(SourcePriority), id.ToString(), update, ct);
        return Ok(new { priority.Id, priority.Priority });
    }

    private static bool RequiresPositiveInteger(string key) =>
        key == SettingKeys.SecurityPasswordMinLength ||
        key == SettingKeys.SecurityLoginMaxFailedAttempts ||
        key == SettingKeys.SecurityLoginLockoutMinutes ||
        key == SettingKeys.SecuritySessionTokenLifetimeMinutes ||
        key == SettingKeys.SecuritySessionIdleTimeoutMinutes ||
        key == SettingKeys.SecurityAuditRetentionDays ||
        key == SettingKeys.MatchNetworkEvidenceMaxAgeDays;
}
