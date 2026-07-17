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
    public AuthController(IAuthService auth) => _auth = auth;

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

    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    /// <summary>Self-service password change for the authenticated local user.</summary>
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request,
        [FromServices] ICurrentUserService currentUser, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId) return Unauthorized();
        var result = await _auth.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword, ct);
        return result.Success ? Ok(new { changed = true }) : BadRequest(new { error = result.Error });
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
}
