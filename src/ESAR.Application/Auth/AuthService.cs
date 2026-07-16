using Esar.Application.Abstractions;
using Esar.Application.Auditing;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Esar.Application.Auth;

public interface IAuthService
{
    Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetPermissionsAsync(Guid userId, CancellationToken ct = default);
}

public record LoginResult(bool Success, string? Token, DateTime? ExpiresAt, string? Error,
    string? DisplayName = null, IReadOnlyList<string>? Roles = null);

public class AuthService : IAuthService
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly IUnitOfWork _uow;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenService _jwt;
    private readonly IAuditService _audit;
    private readonly ILogger<AuthService> _logger;

    public AuthService(IUnitOfWork uow, IPasswordHasher hasher, IJwtTokenService jwt, IAuditService audit,
        ILogger<AuthService> logger)
    {
        _uow = uow;
        _hasher = hasher;
        _jwt = jwt;
        _audit = audit;
        _logger = logger;
    }

    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var user = await _uow.Users.FirstOrDefaultAsync(
            u => u.Username == username && u.AuthProvider == AuthProvider.Local, ct);

        if (user is null || !user.IsActive)
            return new LoginResult(false, null, null, "Invalid credentials");

        if (user.LockedOutUntil is { } lockedUntil && lockedUntil > DateTime.UtcNow)
            return new LoginResult(false, null, null, "Account temporarily locked");

        if (user.PasswordHash is null || !_hasher.Verify(password, user.PasswordHash))
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= MaxFailedAttempts)
            {
                user.LockedOutUntil = DateTime.UtcNow.Add(LockoutDuration);
                user.FailedLoginAttempts = 0;
                _logger.LogWarning("User {User} locked out after repeated failures", username);
            }
            _uow.Users.Update(user);
            await _uow.SaveChangesAsync(ct);
            return new LoginResult(false, null, null, "Invalid credentials");
        }

        user.FailedLoginAttempts = 0;
        user.LockedOutUntil = null;
        user.LastLoginAt = DateTime.UtcNow;
        _uow.Users.Update(user);

        var roles = await GetRolesAsync(user.Id, ct);
        var permissions = await GetPermissionsAsync(user.Id, ct);
        var (token, expires) = _jwt.CreateToken(user, roles, permissions);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.Login, nameof(User), user.Id.ToString(), new { user.Username }, ct);
        return new LoginResult(true, token, expires, null, user.DisplayName, roles);
    }

    public async Task<IReadOnlyList<string>> GetPermissionsAsync(Guid userId, CancellationToken ct = default)
    {
        var roles = await _uow.Roles.ListAsync(r => r.UserRoles.Any(ur => ur.UserId == userId), ct);
        var roleIds = roles.Select(r => r.Id).ToList();
        var permissions = await _uow.Permissions.ListAsync(
            p => p.RolePermissions.Any(rp => roleIds.Contains(rp.RoleId)), ct);
        return permissions.Select(p => p.Code).Distinct().ToList();
    }

    private async Task<IReadOnlyList<string>> GetRolesAsync(Guid userId, CancellationToken ct)
    {
        var roles = await _uow.Roles.ListAsync(r => r.UserRoles.Any(ur => ur.UserId == userId), ct);
        return roles.Select(r => r.Name).ToList();
    }
}
