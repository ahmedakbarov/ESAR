using System.DirectoryServices.Protocols;
using System.Net;
using Esar.Application.Abstractions;
using Esar.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Esar.Infrastructure.Connectors;

/// <summary>
/// Interactive AD login: binds against whichever ActiveDirectory connector is configured and
/// enabled, using the credentials the person logging in just typed — never the connector's own
/// stored service account, which exists only for asset-discovery syncs.
/// </summary>
public class LdapLoginService : ILdapLoginService
{
    private readonly IUnitOfWork _uow;
    private readonly ISecretProtector _secrets;
    private readonly ILogger<LdapLoginService> _logger;

    public LdapLoginService(IUnitOfWork uow, ISecretProtector secrets, ILogger<LdapLoginService> logger)
    {
        _uow = uow;
        _secrets = secrets;
        _logger = logger;
    }

    public async Task<LdapBindResult> TryBindAsync(string username, string password, CancellationToken ct = default)
    {
        var connector = await _uow.Connectors.FirstOrDefaultAsync(
            c => c.Type == ConnectorType.ActiveDirectory && c.Enabled, ct);
        if (connector is null)
            return LdapBindResult.Fail(LdapBindFailureReason.NoConnectorConfigured);

        ActiveDirectoryConnectionOptions options;
        try
        {
            var settings = ConnectorSettingsCodec.Decrypt(connector.SettingsJson, _secrets);
            options = ActiveDirectoryConnectionOptions.Parse(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AD login: failed to parse the configured Active Directory connector's settings");
            return LdapBindResult.Fail(LdapBindFailureReason.Other);
        }

        // System.DirectoryServices.Protocols is synchronous — run on a worker thread.
        return await Task.Run(() => BindAndLookup(options, username, password), ct);
    }

    private LdapBindResult BindAndLookup(ActiveDirectoryConnectionOptions options, string username, string password)
    {
        try
        {
            var identifier = new LdapDirectoryIdentifier(options.Server, options.Port);
            var credential = new NetworkCredential(username, password);
            using var connection = new LdapConnection(identifier, credential, options.AuthType)
            {
                Timeout = options.Timeout
            };
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.SecureSocketLayer = options.UseSsl;
            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
            connection.Bind();

            // A bind alone proves the credential but returns no attributes — a follow-up search
            // on the now-authenticated connection resolves the stable identity + contact info.
            var escaped = EscapeLdapFilterValue(username);
            var filter = $"(|(userPrincipalName={escaped})(sAMAccountName={escaped}))";
            var request = new SearchRequest(options.BaseDn, filter, SearchScope.Subtree,
                "objectGUID", "mail", "displayName");
            var response = (SearchResponse)connection.SendRequest(request);
            var entry = response.Entries.Count > 0 ? response.Entries[0] : null;

            string? objectGuid = null;
            if (entry?.Attributes["objectGUID"]?[0] is byte[] guidBytes)
                objectGuid = new Guid(guidBytes).ToString();
            if (objectGuid is null)
            {
                _logger.LogWarning(
                    "AD login: bind succeeded for {Username} but the directory search found no objectGUID",
                    username);
                return LdapBindResult.Fail(LdapBindFailureReason.Other);
            }

            var email = entry?.Attributes["mail"]?[0] as string;
            var displayName = entry?.Attributes["displayName"]?[0] as string ?? username;
            return LdapBindResult.Ok(objectGuid, email, displayName);
        }
        catch (LdapException ex) when (ex.ErrorCode == 49) // invalidCredentials
        {
            return LdapBindResult.Fail(LdapBindFailureReason.InvalidCredentials);
        }
        catch (LdapException ex)
        {
            _logger.LogWarning(ex, "AD login: LDAP error {ErrorCode} binding for {Username}", ex.ErrorCode, username);
            return LdapBindResult.Fail(LdapBindFailureReason.ServerUnreachable);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AD login: unexpected error binding for {Username}", username);
            return LdapBindResult.Fail(LdapBindFailureReason.ServerUnreachable);
        }
    }

    /// <summary>RFC 4515 filter escaping — unlike the discovery connector's static filters
    /// (e.g. "(objectClass=*)"), this interpolates the submitted username directly.</summary>
    private static string EscapeLdapFilterValue(string value) => value
        .Replace("\\", "\\5c")
        .Replace("*", "\\2a")
        .Replace("(", "\\28")
        .Replace(")", "\\29")
        .Replace("\0", "\\00");
}
