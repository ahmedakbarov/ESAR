using System.DirectoryServices.Protocols;
using System.Net;
using System.Runtime.CompilerServices;
using Esar.Application.Abstractions;
using Esar.Application.Contracts;
using Esar.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Esar.Infrastructure.Connectors;

/// <summary>
/// Discovers computer objects from on-premises Active Directory over LDAP(S).
/// Settings: server, port (636), baseDn, username, password, useSsl=true, authType=Basic.
/// </summary>
public class ActiveDirectoryConnector : IConnector
{
    private static readonly string[] Attributes =
    {
        "objectGUID", "dNSHostName", "name", "operatingSystem", "operatingSystemVersion",
        "lastLogonTimestamp", "whenCreated", "distinguishedName", "userAccountControl", "description"
    };

    private readonly ILogger<ActiveDirectoryConnector> _logger;
    public ActiveDirectoryConnector(ILogger<ActiveDirectoryConnector> logger) => _logger = logger;

    public ConnectorType Type => ConnectorType.ActiveDirectory;

    public async Task<ConnectorHealth> CheckHealthAsync(ConnectorSettings settings, CancellationToken ct = default)
    {
        try
        {
            var options = ActiveDirectoryConnectionOptions.Parse(settings);
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var connection = Connect(options);
                connection.Bind();
                var request = new SearchRequest(options.BaseDn, "(objectClass=*)",
                    SearchScope.Base, "distinguishedName");
                var response = (SearchResponse)connection.SendRequest(request);
                if (response.ResultCode != ResultCode.Success)
                    throw new InvalidOperationException($"LDAP Base DN check returned {response.ResultCode}.");
            }, ct);

            return new ConnectorHealth(true, "LDAPS bind and search succeeded");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return new ConnectorHealth(false, ex.Message);
        }
        catch (LdapException ex)
        {
            _logger.LogWarning(ex, "Active Directory health check failed for {Server}", settings.GetOptional("server"));
            return new ConnectorHealth(false,
                $"LDAP health check failed (LDAP error {ex.ErrorCode}). Verify the LDAPS certificate, credentials, Base DN, and private network path.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Active Directory health check failed for {Server}", settings.GetOptional("server"));
            return new ConnectorHealth(false,
                "LDAP health check failed. Verify the LDAPS certificate, credentials, Base DN, and private network path.");
        }
    }

    public async IAsyncEnumerable<DiscoveredAsset> DiscoverAsync(ConnectorSettings settings, SyncContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // LDAP library is synchronous — run paged search on a worker thread and stream results.
        var options = ActiveDirectoryConnectionOptions.Parse(settings);
        var results = await Task.Run(() => Search(options, ct), ct);
        foreach (var asset in results)
        {
            ct.ThrowIfCancellationRequested();
            yield return asset;
        }
    }

    private List<DiscoveredAsset> Search(ActiveDirectoryConnectionOptions options, CancellationToken ct)
    {
        var baseDn = options.BaseDn;
        var domain = string.Join('.', baseDn.Split(',')
            .Where(p => p.Trim().StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Trim()[3..]));

        using var connection = Connect(options);
        connection.Bind();

        var assets = new List<DiscoveredAsset>();
        var pageControl = new PageResultRequestControl(500);
        var request = new SearchRequest(baseDn, "(objectCategory=computer)", SearchScope.Subtree, Attributes);
        request.Controls.Add(pageControl);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var response = (SearchResponse)connection.SendRequest(request);

            foreach (SearchResultEntry entry in response.Entries)
            {
                var guidBytes = entry.Attributes["objectGUID"]?[0] as byte[];
                if (guidBytes is null) continue;
                var objectGuid = new Guid(guidBytes).ToString();

                var uac = GetValue(entry, "userAccountControl");
                var disabled = uac is not null && int.TryParse(uac, out var flags) && (flags & 0x2) != 0;

                var asset = new DiscoveredAsset
                {
                    Source = ConnectorType.ActiveDirectory,
                    ExternalId = objectGuid,
                    Hostname = GetValue(entry, "dNSHostName") ?? GetValue(entry, "name"),
                    Domain = domain,
                    OperatingSystem = GetValue(entry, "operatingSystem"),
                    OsVersion = GetValue(entry, "operatingSystemVersion"),
                    RawJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        distinguishedName = GetValue(entry, "distinguishedName"),
                        description = GetValue(entry, "description"),
                        adLastLogonTimestamp = GetValue(entry, "lastLogonTimestamp"),
                        whenCreated = GetValue(entry, "whenCreated"),
                        disabled
                    })
                };
                asset.Identifiers[MatchAttributes.ObjectGuid] = objectGuid;
                if (disabled) asset.Tags["ad_disabled"] = "true";

                assets.Add(asset);
            }

            var pageResponse = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
            if (pageResponse is null || pageResponse.Cookie.Length == 0) break;
            pageControl.Cookie = pageResponse.Cookie;
        }

        _logger.LogInformation("Active Directory search returned {Count} computer objects", assets.Count);
        return assets;
    }

    private static LdapConnection Connect(ActiveDirectoryConnectionOptions options)
    {
        var identifier = new LdapDirectoryIdentifier(options.Server, options.Port);
        var credential = new NetworkCredential(options.Username, options.Password);
        var connection = new LdapConnection(identifier, credential, options.AuthType)
        {
            Timeout = options.Timeout
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = options.UseSsl;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        return connection;
    }

    private static string? GetValue(SearchResultEntry entry, string attribute)
    {
        if (!entry.Attributes.Contains(attribute)) return null;
        var values = entry.Attributes[attribute].GetValues(typeof(string));
        return values.Length > 0 ? values[0] as string : null;
    }
}
