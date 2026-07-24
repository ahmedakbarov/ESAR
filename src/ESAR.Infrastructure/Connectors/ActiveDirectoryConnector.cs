using System.DirectoryServices.Protocols;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Esar.Application.Abstractions;
using Esar.Application.Contracts;
using Esar.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Esar.Infrastructure.Connectors;

/// <summary>
/// Discovers computer objects from on-premises Active Directory over LDAP(S).
/// Settings: server, port (636), baseDn, username, password, useSsl=true, authType=Basic.
/// Optional network enrichment: resolveDns, dnsTimeoutSeconds, dnsMaxConcurrency, macAttributes.
/// </summary>
public class ActiveDirectoryConnector : IConnector
{
    private static readonly string[] DefaultAttributes =
    {
        "objectGUID", "dNSHostName", "name", "operatingSystem", "operatingSystemVersion",
        "lastLogonTimestamp", "whenCreated", "distinguishedName", "userAccountControl", "description",
        "memberOf"
    };

    /// <summary>Safety cap on AD group tags per asset — mirrors the MAC-attribute enrichment cap.</summary>
    private const int MaxGroupTags = 50;

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
        if (options.ResolveDns)
            await EnrichDnsAsync(results, options, ct);

        foreach (var asset in results)
        {
            ct.ThrowIfCancellationRequested();
            yield return asset;
        }
    }

    private List<DiscoveredAsset> Search(ActiveDirectoryConnectionOptions options, CancellationToken ct)
    {
        var baseDn = options.BaseDn;

        using var connection = Connect(options);
        connection.Bind();

        var assets = new List<DiscoveredAsset>();
        var pageControl = new PageResultRequestControl(500);
        var attributes = DefaultAttributes
            .Concat(options.MacAttributes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var request = new SearchRequest(baseDn, "(objectCategory=computer)", SearchScope.Subtree, attributes);
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
                var dnsHostName = GetValue(entry, "dNSHostName");

                var asset = new DiscoveredAsset
                {
                    Source = ConnectorType.ActiveDirectory,
                    ExternalId = objectGuid,
                    Hostname = dnsHostName ?? GetValue(entry, "name"),
                    Fqdn = dnsHostName,
                    Domain = options.BaseDnDomain,
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
                asset.Identifiers[MatchAttributes.AdComputerObjectGuid] = objectGuid;
                if (disabled) asset.Tags["ad_disabled"] = "true";
                // AD group membership, surfaced as tags so compliance policies can scope by group
                // (e.g. "adgroup:domain admins") without a dedicated schema field — see PolicyScopeMatcher.
                foreach (var groupDn in GetValues(entry, "memberOf").Take(MaxGroupTags))
                {
                    var cn = ExtractCn(groupDn);
                    if (string.IsNullOrWhiteSpace(cn)) continue;
                    var key = $"adgroup:{cn.ToLowerInvariant()}";
                    // AssetTag.Key is varchar(128) — truncate rather than fail the whole sync on one long CN.
                    if (key.Length > 128) key = key[..128];
                    asset.Tags[key] = "true";
                }
                foreach (var macAttribute in options.MacAttributes)
                    ActiveDirectoryNetworkEnrichment.AppendMacOnlyInterfaces(asset, GetValues(entry, macAttribute));

                assets.Add(asset);
            }

            var pageResponse = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
            if (pageResponse is null || pageResponse.Cookie.Length == 0) break;
            pageControl.Cookie = pageResponse.Cookie;
        }

        _logger.LogInformation("Active Directory search returned {Count} computer objects", assets.Count);
        return assets;
    }

    private async Task EnrichDnsAsync(
        IReadOnlyList<DiscoveredAsset> assets,
        ActiveDirectoryConnectionOptions options,
        CancellationToken ct)
    {
        var targets = ActiveDirectoryNetworkEnrichment.GetDnsTargets(assets, options.BaseDnDomain);
        if (targets.Count == 0)
        {
            _logger.LogInformation("Active Directory DNS enrichment found no in-domain computer dNSHostName values to resolve");
            return;
        }

        var failures = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var resolvedTargets = 0;
        var interfacesAdded = 0;
        await Parallel.ForEachAsync(targets, new ParallelOptions
        {
            MaxDegreeOfParallelism = options.DnsMaxConcurrency,
            CancellationToken = ct
        }, async (target, token) =>
        {
            try
            {
                using var lookupTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                lookupTimeout.CancelAfter(options.DnsTimeout);
                var addresses = await Dns.GetHostAddressesAsync(target.Hostname, lookupTimeout.Token);
                var added = ActiveDirectoryNetworkEnrichment.AppendDnsIpOnlyInterfaces(target.Asset, addresses);
                Interlocked.Increment(ref resolvedTargets);
                Interlocked.Add(ref interfacesAdded, added);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                failures.AddOrUpdate("timeout", 1, (_, count) => count + 1);
            }
            catch (Exception ex)
            {
                failures.AddOrUpdate(ex.GetType().Name, 1, (_, count) => count + 1);
            }
        });

        if (!failures.IsEmpty)
        {
            var summary = string.Join(", ", failures.OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Key}: {pair.Value}"));
            _logger.LogWarning(
                "Active Directory DNS enrichment completed with {FailureCount} failed lookup(s) out of {TargetCount}. Failure types: {FailureSummary}",
                failures.Values.Sum(), targets.Count, summary);
        }

        _logger.LogInformation(
            "Active Directory DNS enrichment resolved {ResolvedTargetCount} of {TargetCount} eligible computer hostnames and added {InterfaceCount} IP-only interface(s)",
            resolvedTargets, targets.Count, interfacesAdded);
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

    private static IEnumerable<string?> GetValues(SearchResultEntry entry, string attribute)
    {
        if (!entry.Attributes.Contains(attribute)) return Array.Empty<string?>();
        return entry.Attributes[attribute].GetValues(typeof(string)).OfType<string>();
    }

    /// <summary>Extracts the CN from a distinguished name (e.g. "CN=Domain Admins,OU=Groups,DC=esar,DC=local"
    /// -> "Domain Admins"), honoring backslash-escaped commas inside the CN component (RFC 4514).</summary>
    private static string? ExtractCn(string? dn)
    {
        if (dn is null || !dn.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return null;
        var sb = new StringBuilder();
        for (var i = 3; i < dn.Length; i++)
        {
            if (dn[i] == '\\' && i + 1 < dn.Length) { sb.Append(dn[++i]); continue; }
            if (dn[i] == ',') break;
            sb.Append(dn[i]);
        }
        return sb.Length == 0 ? null : sb.ToString().Trim();
    }
}
