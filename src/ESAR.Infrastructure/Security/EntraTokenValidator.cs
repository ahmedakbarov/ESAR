using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Esar.Application.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Esar.Infrastructure.Security;

public class EntraIdOptions
{
    public string TenantId { get; set; } = string.Empty;
    /// <summary>Client ID of the Azure AD App Registration (Single-page application platform).</summary>
    public string ClientId { get; set; } = string.Empty;
}

/// <summary>
/// Validates Entra ID-issued ID tokens by fetching Microsoft's own OIDC signing keys and checking
/// the token's signature/issuer/audience/lifetime before trusting any of its claims — the same
/// verification ASP.NET's JwtBearer middleware would do, just invoked manually so the result feeds
/// ESAR's own token-exchange flow instead of the authentication pipeline directly.
/// </summary>
public class EntraTokenValidator : IEntraTokenValidator
{
    private readonly EntraIdOptions _options;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configManager;

    public EntraTokenValidator(IOptions<EntraIdOptions> options)
    {
        _options = options.Value;
        if (IsConfigured)
        {
            _configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                $"https://login.microsoftonline.com/{_options.TenantId}/v2.0/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = true });
        }
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.TenantId) && !string.IsNullOrWhiteSpace(_options.ClientId);

    public async Task<EntraTokenClaims> ValidateAsync(string idToken, CancellationToken ct = default)
    {
        if (!IsConfigured || _configManager is null)
            throw new InvalidOperationException("Entra ID SSO is not configured.");

        var config = await _configManager.GetConfigurationAsync(ct);
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = config.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.ClientId,
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
}
