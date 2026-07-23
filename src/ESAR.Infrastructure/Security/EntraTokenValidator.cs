using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Esar.Application.Abstractions;
using Esar.Application.Matching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Esar.Infrastructure.Security;

/// <summary>
/// Validates Entra ID-issued ID tokens by fetching Microsoft's own OIDC signing keys and checking
/// the token's signature/issuer/audience/lifetime before trusting any of its claims. Tenant and
/// client IDs live in the Settings table (editable in the UI Settings page), read fresh on each
/// call so an admin can change them without an app restart. The per-tenant ConfigurationManager
/// (which caches Microsoft's signing keys) is memoized across calls since it is expensive to build.
/// </summary>
public class EntraTokenValidator : IEntraTokenValidator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers = new();

    public EntraTokenValidator(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<EntraTokenClaims> ValidateAsync(string idToken, CancellationToken ct = default)
    {
        var (tenantId, clientId) = await ReadConfigAsync(ct);
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Entra ID SSO is not configured.");

        var manager = _managers.GetOrAdd(tenantId, tid => new ConfigurationManager<OpenIdConnectConfiguration>(
            $"https://login.microsoftonline.com/{tid}/v2.0/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true }));

        var config = await manager.GetConfigurationAsync(ct);
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = config.Issuer,
            ValidateAudience = true,
            ValidAudience = clientId,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            IssuerSigningKeys = config.SigningKeys
        };

        ClaimsPrincipal principal;
        try
        {
            principal = new JwtSecurityTokenHandler().ValidateToken(idToken, parameters, out _);
        }
        catch (SecurityTokenException ex)
        {
            throw new InvalidOperationException("Invalid Entra ID token.", ex);
        }

        var objectId = principal.FindFirstValue("oid")
            ?? throw new InvalidOperationException("Entra ID token is missing the 'oid' claim.");
        var email = principal.FindFirstValue("preferred_username") ?? principal.FindFirstValue(ClaimTypes.Email)
            ?? throw new InvalidOperationException("Entra ID token is missing an email claim.");
        var displayName = principal.FindFirstValue("name") ?? email;

        return new EntraTokenClaims(objectId, email, displayName);
    }

    private async Task<(string? TenantId, string? ClientId)> ReadConfigAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var tenantId = (await uow.Settings.FirstOrDefaultAsync(s => s.Key == SettingKeys.AuthEntraTenantId, ct))?.Value;
        var clientId = (await uow.Settings.FirstOrDefaultAsync(s => s.Key == SettingKeys.AuthEntraClientId, ct))?.Value;
        return (tenantId, clientId);
    }
}
