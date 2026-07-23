using Esar.Domain.Common;
using Esar.Domain.Enums;

namespace Esar.Domain.Entities;

public class User : AuditableEntity
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>BCrypt hash; null for federated (Entra ID / LDAP) accounts.</summary>
    public string? PasswordHash { get; set; }
    public AuthProvider AuthProvider { get; set; } = AuthProvider.Local;
    /// <summary>Object id in the external identity provider.</summary>
    public string? ExternalObjectId { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Break-glass bootstrap account: protected from admin-side management actions
    /// (deactivate, delete, role change, password reset by others). The owner can still rotate
    /// their own password via self-service change-password.</summary>
    public bool IsProtected { get; set; }
    public bool MfaEnabled { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockedOutUntil { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}

public class Role : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSystem { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}

public class Permission : BaseEntity
{
    /// <summary>Machine code, e.g. "assets.read", "connectors.manage".</summary>
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }

    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}

public class UserRole
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
}

public class RolePermission
{
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
    public Guid PermissionId { get; set; }
    public Permission? Permission { get; set; }
}
