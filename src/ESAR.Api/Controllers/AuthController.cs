using Asp.Versioning;
using Esar.Application.Abstractions;
using Esar.Application.Auth;
using Esar.Application.Matching;
using Esar.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Esar.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;

    public AuthController(IAuthService auth, ICurrentUserService currentUser, IUnitOfWork uow)
    {
        _auth = auth;
        _currentUser = currentUser;
        _uow = uow;
    }

    private static object ToResponse(LoginResult result) => new
    {
        token = result.Token,
        expiresAt = result.ExpiresAt,
        displayName = result.DisplayName,
        roles = result.Roles,
        userId = result.UserId
    };

    public record LoginRequest(string Username, string Password);

    /// <summary>Authenticates a local user and returns a JWT access token.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { error = "Username and password are required." });

        var result = await _auth.LoginAsync(request.Username.Trim(), request.Password, ct);
        if (!result.Success) return Unauthorized(new { error = result.Error });
        return Ok(ToResponse(result));
    }

    public record EntraLoginRequest(string IdToken);

    /// <summary>Exchanges a validated Entra ID (Azure AD) ID token for an ESAR JWT.</summary>
    [HttpPost("login/entra")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginEntra([FromBody] EntraLoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.IdToken))
            return BadRequest(new { error = "idToken is required." });

        var result = await _auth.LoginWithEntraIdAsync(request.IdToken, ct);
        if (!result.Success) return Unauthorized(new { error = result.Error });
        return Ok(ToResponse(result));
    }

    public record LdapLoginRequest(string Username, string Password);

    /// <summary>Authenticates against Active Directory (LDAP bind) and returns an ESAR JWT.</summary>
    [HttpPost("login/ldap")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginLdap([FromBody] LdapLoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { error = "Username and password are required." });

        var result = await _auth.LoginWithLdapAsync(request.Username.Trim(), request.Password, ct);
        if (result.Success) return Ok(ToResponse(result));
        var unavailable = result.Error?.StartsWith("AD login is temporarily", StringComparison.Ordinal) == true;
        return unavailable ? StatusCode(503, new { error = result.Error }) : Unauthorized(new { error = result.Error });
    }

    /// <summary>Tells the frontend which login methods are available, without a rebuild — driven by
    /// the Entra tenant/client Settings (UI-editable) and whether an AD connector is enabled.</summary>
    [HttpGet("config")]
    [AllowAnonymous]
    public async Task<IActionResult> Config(CancellationToken ct)
    {
        var entraTenantId = (await _uow.Settings.FirstOrDefaultAsync(s => s.Key == SettingKeys.AuthEntraTenantId, ct))?.Value;
        var entraClientId = (await _uow.Settings.FirstOrDefaultAsync(s => s.Key == SettingKeys.AuthEntraClientId, ct))?.Value;
        var idleTimeoutMinutes = await GetIntSettingAsync(
            SettingKeys.SecuritySessionIdleTimeoutMinutes, fallback: 30, ct);
        var entraEnabled = !string.IsNullOrWhiteSpace(entraTenantId) && !string.IsNullOrWhiteSpace(entraClientId);
        var ldapEnabled = await _uow.Connectors.FirstOrDefaultAsync(
            c => c.Type == ConnectorType.ActiveDirectory && c.Enabled, ct) is not null;
        return Ok(new
        {
            entraEnabled,
            ldapEnabled,
            idleTimeoutMinutes,
            entraTenantId = entraEnabled ? entraTenantId : null,
            entraClientId = entraEnabled ? entraClientId : null
        });
    }

    /// <summary>Returns the current principal's identity, roles and permissions.</summary>
    [HttpGet("me")]
    public IActionResult Me()
    {
        return Ok(new
        {
            name = User.Identity?.Name,
            roles = User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role).Select(c => c.Value),
            permissions = User.Claims.Where(c => c.Type == "permission").Select(c => c.Value)
        });
    }

    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    /// <summary>Lets the authenticated user change their own password (local accounts only).</summary>
    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        if (_currentUser.UserId is not { } userId)
            return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
            return BadRequest(new { error = "Current and new password are required." });

        var result = await _auth.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword, ct);
        if (!result.Success) return BadRequest(new { error = result.Error });
        return NoContent();
    }

    private async Task<int> GetIntSettingAsync(string key, int fallback, CancellationToken ct)
    {
        var setting = await _uow.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null || !int.TryParse(setting.Value, out var value) || value <= 0)
            return fallback;
        return value;
    }
}
