using Esar.Application.Abstractions;
using Esar.Application.Auditing;
using Esar.Application.Matching;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Esar.Application.Auth;

public interface IAuthService
{
    Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default);
    Task<LoginResult> LoginWithEntraIdAsync(string idToken, CancellationToken ct = default);
    Task<LoginResult> LoginWithLdapAsync(string username, string password, CancellationToken ct = default);
    Task<ChangePasswordResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetPermissionsAsync(Guid userId, CancellationToken ct = default);
}

public record LoginResult(bool Success, string? Token, DateTime? ExpiresAt, string? Error,
    string? DisplayName = null, IReadOnlyList<string>? Roles = null, Guid? UserId = null);

public record ChangePasswordResult(bool Success, string? Error);

public class AuthService : IAuthService
{
    private const int DefaultMaxFailedAttempts = 5;
    private const int DefaultMinPasswordLength = 12;
    private const int DefaultLockoutMinutes = 15;
    private const int DefaultTokenLifetimeMinutes = 60;

    private readonly IUnitOfWork _uow;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenService _jwt;
    private readonly IAuditService _audit;
    private readonly ILdapLoginService _ldap;
    private readonly IEntraTokenValidator _entra;
    private readonly ILogger<AuthService> _logger;

    public AuthService(IUnitOfWork uow, IPasswordHasher hasher, IJwtTokenService jwt, IAuditService audit,
        ILdapLoginService ldap, IEntraTokenValidator entra, ILogger<AuthService> logger)
    {
        _uow = uow;
        _hasher = hasher;
        _jwt = jwt;
        _audit = audit;
        _ldap = ldap;
        _entra = entra;
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

        var maxFailedAttempts = await GetIntSettingAsync(
            SettingKeys.SecurityLoginMaxFailedAttempts, DefaultMaxFailedAttempts, ct);
        var lockoutMinutes = await GetIntSettingAsync(
            SettingKeys.SecurityLoginLockoutMinutes, DefaultLockoutMinutes, ct);

        if (user.PasswordHash is null || !_hasher.Verify(password, user.PasswordHash))
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= maxFailedAttempts)
            {
                user.LockedOutUntil = DateTime.UtcNow.AddMinutes(lockoutMinutes);
                user.FailedLoginAttempts = 0;
                _logger.LogWarning("User {User} locked out after repeated failures", username);
            }
            _uow.Users.Update(user);
            await _uow.SaveChangesAsync(ct);
            return new LoginResult(false, null, null, "Invalid credentials");
        }

        user.FailedLoginAttempts = 0;
        user.LockedOutUntil = null;
        return await IssueTokenAsync(user, ct);
    }

    public async Task<LoginResult> LoginWithEntraIdAsync(string idToken, CancellationToken ct = default)
    {
        EntraTokenClaims claims;
        try
        {
            claims = await _entra.ValidateAsync(idToken, ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Entra ID login: token validation failed");
            // "not configured" vs "bad token" both surface generically — the frontend already
            // hides the Microsoft button when /auth/config reports Entra as disabled.
            return new LoginResult(false, null, null, "Entra ID sign-in failed.");
        }

        var user = await ResolveOrProvisionUserAsync(AuthProvider.EntraId, claims.ObjectId, claims.Email,
            claims.DisplayName, ct);
        if (user is null)
            return new LoginResult(false, null, null,
                "No ESAR account is provisioned for this identity — contact an administrator.");
        return await IssueTokenAsync(user, ct);
    }

    public async Task<LoginResult> LoginWithLdapAsync(string username, string password, CancellationToken ct = default)
    {
        var bind = await _ldap.TryBindAsync(username, password, ct);
        if (!bind.Success)
        {
            _logger.LogWarning("AD login failed for {Username}: {Reason}", username, bind.FailureReason);
            var message = bind.FailureReason == LdapBindFailureReason.InvalidCredentials
                ? "Invalid username or password."
                : "AD login is temporarily unavailable — contact your administrator.";
            return new LoginResult(false, null, null, message);
        }

        var user = await ResolveOrProvisionUserAsync(AuthProvider.Ldap, bind.ObjectGuid!,
            bind.Email ?? $"{username}@ad.local", bind.DisplayName ?? username, ct);
        if (user is null)
            return new LoginResult(false, null, null,
                "No ESAR account is provisioned for this identity — contact an administrator.");
        return await IssueTokenAsync(user, ct);
    }

    /// <summary>Shared success tail for every login path (local password, Entra SSO, AD login) —
    /// none of them care how the user was authenticated once a User row and its roles are known.</summary>
    private async Task<LoginResult> IssueTokenAsync(User user, CancellationToken ct)
    {
        user.LastLoginAt = DateTime.UtcNow;
        _uow.Users.Update(user);
        var roles = await GetRolesAsync(user.Id, ct);
        var permissions = await GetPermissionsAsync(user.Id, ct);
        var tokenLifetime = await GetIntSettingAsync(
            SettingKeys.SecuritySessionTokenLifetimeMinutes, DefaultTokenLifetimeMinutes, ct);
        var (token, expires) = _jwt.CreateToken(user, roles, permissions, tokenLifetime);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.Login, nameof(User), user.Id.ToString(),
            new { user.Username, provider = user.AuthProvider.ToString() }, ct);
        return new LoginResult(true, token, expires, null, user.DisplayName, roles, user.Id);
    }

    /// <summary>Three-tier resolution shared by Entra SSO and AD login: (a) an already-linked
    /// account (AuthProvider+ExternalObjectId match) — normal return visit; (b) an admin
    /// pre-provisioned placeholder (AuthProvider+Email match, ExternalObjectId still null) — link
    /// it now; (c) no match — auto-provision a new Viewer account, but only if
    /// auth.federated.autoProvision is enabled (defaults to off; a wide-open JIT policy silently
    /// hands out ESAR accounts to anyone with a valid domain credential).</summary>
    private async Task<User?> ResolveOrProvisionUserAsync(AuthProvider provider, string externalObjectId,
        string email, string displayName, CancellationToken ct)
    {
        var user = await _uow.Users.FirstOrDefaultAsync(
            u => u.AuthProvider == provider && u.ExternalObjectId == externalObjectId, ct);
        if (user is not null) return user.IsActive ? user : null;

        var normalizedEmail = email.Trim().ToLowerInvariant();
        user = await _uow.Users.FirstOrDefaultAsync(
            u => u.AuthProvider == provider && u.ExternalObjectId == null &&
                 u.Email.ToLower() == normalizedEmail, ct);
        if (user is not null)
        {
            if (!user.IsActive) return null;
            user.ExternalObjectId = externalObjectId;
            _uow.Users.Update(user);
            return user;
        }

        var autoProvision = await _uow.Settings.FirstOrDefaultAsync(
            s => s.Key == SettingKeys.AuthFederatedAutoProvision, ct);
        if (autoProvision?.Value != "true") return null;

        // Guards against a silent unique-constraint crash if the email is already taken under a
        // different provider (e.g. a Local account with the same address).
        if (await _uow.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail, ct) is not null)
        {
            _logger.LogWarning(
                "Federated auto-provision skipped for {Email}: email already in use by another account", email);
            return null;
        }

        var viewerRole = await _uow.Roles.FirstOrDefaultAsync(r => r.Name == "Viewer", ct);
        var newUser = new User
        {
            Username = email.Trim(),
            Email = email.Trim(),
            DisplayName = displayName,
            AuthProvider = provider,
            ExternalObjectId = externalObjectId,
            PasswordHash = null
        };
        await _uow.Users.AddAsync(newUser, ct);
        if (viewerRole is not null)
            await _uow.UserRoles.AddAsync(new UserRole { UserId = newUser.Id, RoleId = viewerRole.Id }, ct);
        await _audit.LogAsync(AuditAction.UserCreated, nameof(User), newUser.Id.ToString(),
            new { newUser.Username, provider = provider.ToString(), autoProvisioned = true }, ct);
        return newUser;
    }

    public async Task<ChangePasswordResult> ChangePasswordAsync(Guid userId, string currentPassword,
        string newPassword, CancellationToken ct = default)
    {
        var user = await _uow.Users.GetByIdAsync(userId, ct);
        if (user is null || !user.IsActive)
            return new ChangePasswordResult(false, "User not found");

        // Only local accounts have a password here; federated (Entra ID/LDAP) accounts
        // are managed by their upstream identity provider.
        if (user.AuthProvider != AuthProvider.Local || user.PasswordHash is null)
            return new ChangePasswordResult(false, "Password changes are only available for local accounts");

        if (!_hasher.Verify(currentPassword, user.PasswordHash))
            return new ChangePasswordResult(false, "Current password is incorrect");

        var minPasswordLength = await GetIntSettingAsync(
            SettingKeys.SecurityPasswordMinLength, DefaultMinPasswordLength, ct);
        if (newPassword.Length < minPasswordLength)
            return new ChangePasswordResult(false, $"Password must be at least {minPasswordLength} characters");

        if (_hasher.Verify(newPassword, user.PasswordHash))
            return new ChangePasswordResult(false, "New password must be different from the current password");

        user.PasswordHash = _hasher.Hash(newPassword);
        user.UpdatedAt = DateTime.UtcNow;
        // A successful password change clears any prior lockout/failure state.
        user.FailedLoginAttempts = 0;
        user.LockedOutUntil = null;
        _uow.Users.Update(user);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.UserUpdated, nameof(User), user.Id.ToString(),
            new { action = "password_changed" }, ct);
        return new ChangePasswordResult(true, null);
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

    private async Task<int> GetIntSettingAsync(string key, int fallback, CancellationToken ct)
    {
        var setting = await _uow.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null || !int.TryParse(setting.Value, out var value) || value <= 0)
            return fallback;
        return value;
    }
}
