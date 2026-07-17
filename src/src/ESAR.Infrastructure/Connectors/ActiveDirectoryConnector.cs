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
/// Settings: server, port (636), baseDn, username, password, useSsl (true/false).
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

    public Task<ConnectorHealth> CheckHealthAsync(ConnectorSettings settings, CancellationToken ct = default)
    {
        try
        {
            using var connection = Connect(settings);
            connection.Bind();
            return Task.FromResult(new ConnectorHealth(true, "LDAP bind succeeded"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ConnectorHealth(false, ex.Message));
        }
    }

    public async IAsyncEnumerable<DiscoveredAsset> DiscoverAsync(ConnectorSettings settings, SyncContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // LDAP library is synchronous — run paged search on a worker thread and stream results.
        var results = await Task.Run(() => Search(settings, ct), ct);
        foreach (var asset in results)
        {
            ct.ThrowIfCancellationRequested();
            yield return asset;
        }
    }

    private List<DiscoveredAsset> Search(ConnectorSettings settings, CancellationToken ct)
    {
        var baseDn = settings.Get("baseDn");
        var domain = string.Join('.', baseDn.Split(',')
            .Where(p => p.Trim().StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Trim()[3..]));

        using var connection = Connect(settings);
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
                        disabled
                    })
                };
                asset.Identifiers[MatchAttributes.ObjectGuid] = objectGuid;
                if (disabled) asset.Tags["ad_disabled"] = "true";

                var lastLogon = GetValue(entry, "lastLogonTimestamp");
                if (lastLogon is not null && long.TryParse(lastLogon, out var fileTime) && fileTime > 0)
                    asset.SeenAt = DateTime.FromFileTimeUtc(fileTime);

                assets.Add(asset);
            }

            var pageResponse = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
            if (pageResponse is null || pageResponse.Cookie.Length == 0) break;
            pageControl.Cookie = pageResponse.Cookie;
        }

        _logger.LogInformation("Active Directory search returned {Count} computer objects", assets.Count);
        return assets;
    }

    private static LdapConnection Connect(ConnectorSettings settings)
    {
        var useSsl = !string.Equals(settings.GetOptional("useSsl"), "false", StringComparison.OrdinalIgnoreCase);
        var port = settings.GetInt("port", useSsl ? 636 : 389);
        var identifier = new LdapDirectoryIdentifier(settings.Get("server"), port);
        var credential = new NetworkCredential(settings.Get("username"), settings.Get("password"));
        var connection = new LdapConnection(identifier, credential, AuthType.Negotiate)
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        connection.SessionOptions.ProtocolVersion = 3;
        if (useSsl) connection.SessionOptions.SecureSocketLayer = true;
        return connection;
    }

    private static string? GetValue(SearchResultEntry entry, string attribute)
        => entry.Attributes.Contains(attribute) ? entry.Attributes[attribute][0]?.ToString() : null;
}
