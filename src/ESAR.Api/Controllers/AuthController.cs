using Asp.Versioning;
using Esar.Application.Abstractions;
using Esar.Application.Auth;
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

    public AuthController(IAuthService auth, ICurrentUserService currentUser)
    {
        _auth = auth;
        _currentUser = currentUser;
    }

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
        return Ok(new
        {
            token = result.Token,
            expiresAt = result.ExpiresAt,
            displayName = result.DisplayName,
            roles = result.Roles
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
}
