using System.Runtime.CompilerServices;
using System.Text.Json;
using Esar.Application.Abstractions;
using Esar.Application.Contracts;
using Esar.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Esar.Infrastructure.Connectors;

/// <summary>
/// CrowdStrike Falcon connector. Settings: baseUrl (https://api.crowdstrike.com), clientId, clientSecret.
/// </summary>
public class CrowdStrikeConnector : RestConnectorBase
{
    public CrowdStrikeConnector(IHttpClientFactory httpFactory, ILogger<CrowdStrikeConnector> logger)
        : base(httpFactory, logger) { }

    public override ConnectorType Type => ConnectorType.CrowdStrike;

    public override async Task<ConnectorHealth> CheckHealthAsync(ConnectorSettings settings, CancellationToken ct = default)
    {
        try
        {
            await AcquireTokenAsync(settings, ct);
            return new ConnectorHealth(true, "OAuth2 token acquired");
        }
        catch (Exception ex)
        {
            return new ConnectorHealth(false, ex.Message);
        }
    }

    public override async IAsyncEnumerable<DiscoveredAsset> DiscoverAsync(ConnectorSettings settings,
        SyncContext context, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var baseUrl = settings.Get("baseUrl").TrimEnd('/');
        var token = await AcquireTokenAsync(settings, ct);
        var client = CreateClient();
        var offset = 0;
        const int limit = 500;

        while (true)
        {
            await RateLimitAsync(context, ct);
            var filter = context.Mode == SyncMode.Incremental && context.LastSuccessfulSyncAt is { } since
                ? $"&filter=last_seen:>='{since:yyyy-MM-ddTHH:mm:ssZ}'"
                : string.Empty;
            using var queryResponse = await SendWithRetryAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{baseUrl}/devices/queries/devices/v1?limit={limit}&offset={offset}{filter}");
                request.Headers.Authorization = new("Bearer", token);
                return request;
            }, ct: ct);
            using var queryDoc = JsonDocument.Parse(await queryResponse.Content.ReadAsStringAsync(ct));
            var ids = queryDoc.RootElement.TryGetProperty("resources", out var resources)
                ? resources.EnumerateArray().Select(e => e.GetString()).Where(s => s is not null).Cast<string>().ToList()
                : new List<string>();
            if (ids.Count == 0) yield break;

            var idsQuery = string.Join("&", ids.Select(id => $"ids={Uri.EscapeDataString(id)}"));
            using var detailResponse = await SendWithRetryAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{baseUrl}/devices/entities/devices/v2?{idsQuery}");
                request.Headers.Authorization = new("Bearer", token);
                return request;
            }, ct: ct);
            using var detailDoc = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync(ct));
            if (detailDoc.RootElement.TryGetProperty("resources", out var devices))
            {
                foreach (var device in devices.EnumerateArray())
                {
                    var asset = new DiscoveredAsset
                    {
                        Source = ConnectorType.CrowdStrike,
                        ExternalId = GetString(device, "device_id") ?? string.Empty,
                        Hostname = GetString(device, "hostname"),
                        OperatingSystem = GetString(device, "os_version"),
                        SerialNumber = GetString(device, "serial_number"),
                        BiosUuid = GetString(device, "bios_id"),
                        Manufacturer = GetString(device, "system_manufacturer"),
                        Model = GetString(device, "system_product_name"),
                        RawJson = device.GetRawText()
                    };
                    asset.Identifiers[MatchAttributes.EndpointId] = asset.ExternalId;
                    var ip = GetString(device, "local_ip");
                    var mac = GetString(device, "mac_address");
                    if (ip is not null || mac is not null)
                        asset.Interfaces.Add(new DiscoveredInterface { IpAddress = ip, MacAddress = mac, IsPrimary = true });
                    asset.Tags["antivirus"] = "true";
                    if (GetString(device, "last_seen") is { } lastSeen && DateTime.TryParse(lastSeen, out var seen))
                        asset.SeenAt = seen.ToUniversalTime();
                    yield return asset;
                }
            }

            if (ids.Count < limit) yield break;
            offset += limit;
        }
    }

    private async Task<string> AcquireTokenAsync(ConnectorSettings settings, CancellationToken ct)
    {
        var client = CreateClient();
        using var response = await SendWithRetryAsync(client, () => new HttpRequestMessage(HttpMethod.Post,
            $"{settings.Get("baseUrl").TrimEnd('/')}/oauth2/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = settings.Get("clientId"),
                ["client_secret"] = settings.Get("clientSecret")
            })
        }, ct: ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }
}

/// <summary>SentinelOne connector. Settings: baseUrl (https://<tenant>.sentinelone.net), apiToken.</summary>
public class SentinelOneConnector : RestConnectorBase
{
    public SentinelOneConnector(IHttpClientFactory httpFactory, ILogger<SentinelOneConnector> logger)
        : base(httpFactory, logger) { }

    public override ConnectorType Type => ConnectorType.SentinelOne;

    public override async Task<ConnectorHealth> CheckHealthAsync(ConnectorSettings settings, CancellationToken ct = default)
    {
        try
        {
            var client = CreateClient();
            using var response = await SendWithRetryAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{settings.Get("baseUrl").TrimEnd('/')}/web/api/v2.1/system/status");
                request.Headers.Authorization = new("ApiToken", settings.Get("apiToken"));
                return request;
            }, ct: ct);
            return new ConnectorHealth(true, "SentinelOne API reachable");
        }
        catch (Exception ex)
        {
            return new ConnectorHealth(false, ex.Message);
        }
    }

    public override async IAsyncEnumerable<DiscoveredAsset> DiscoverAsync(ConnectorSettings settings,
        SyncContext context, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var baseUrl = settings.Get("baseUrl").TrimEnd('/');
        var client = CreateClient();
        string? cursor = null;

        do
        {
            await RateLimitAsync(context, ct);
            var url = $"{baseUrl}/web/api/v2.1/agents?limit=200";
            if (cursor is not null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
            if (context.Mode == SyncMode.Incremental && context.LastSuccessfulSyncAt is { } since)
                url += $"&updatedAt__gte={since:yyyy-MM-ddTHH:mm:ssZ}";

            using var response = await SendWithRetryAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new("ApiToken", settings.Get("apiToken"));
                return request;
            }, ct: ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            if (doc.RootElement.TryGetProperty("data", out var agents))
            {
                foreach (var agent in agents.EnumerateArray())
                {
                    var asset = new DiscoveredAsset
                    {
                        Source = ConnectorType.SentinelOne,
                        ExternalId = GetString(agent, "id") ?? string.Empty,
                        Hostname = GetString(agent, "computerName"),
                        Domain = GetString(agent, "domain"),
                        OperatingSystem = GetString(agent, "osName"),
                        OsVersion = GetString(agent, "osRevision"),
                        SerialNumber = GetString(agent, "serialNumber"),
                        BiosUuid = GetString(agent, "uuid"),
                        Manufacturer = GetString(agent, "modelName"),
                        RawJson = agent.GetRawText()
                    };
                    asset.Identifiers[MatchAttributes.EndpointId] = asset.ExternalId;
                    if (GetString(agent, "externalIp") is { } ip)
                        asset.Interfaces.Add(new DiscoveredInterface { IpAddress = ip });
                    asset.Tags["antivirus"] = "true";
                    if (GetString(agent, "lastActiveDate") is { } lastActive && DateTime.TryParse(lastActive, out var seen))
                        asset.SeenAt = seen.ToUniversalTime();
                    yield return asset;
                }
            }
            cursor = GetString(doc.RootElement, "pagination", "nextCursor");
        } while (cursor is not null);
    }
}

/// <summary>Tenable Vulnerability Management connector. Settings: accessKey, secretKey.</summary>
public class TenableConnector : RestConnectorBase
{
    private const string BaseUrl = "https://cloud.tenable.com";
    public TenableConnector(IHttpClientFactory httpFactory, ILogger<TenableConnector> logger)
        : base(httpFactory, logger) { }

    public override ConnectorType Type => ConnectorType.Tenable;

    public override async Task<ConnectorHealth> CheckHealthAsync(ConnectorSettings settings, CancellationToken ct = default)
    {
        try
        {
            var client = CreateClient();
            using var response = await SendWithRetryAsync(client,
                () => BuildRequest(settings, $"{BaseUrl}/server/status"), ct: ct);
            return new ConnectorHealth(true, "Tenable API reachable");
        }
        catch (Exception ex)
        {
            return new ConnectorHealth(false, ex.Message);
        }
    }

    public override async IAsyncEnumerable<DiscoveredAsset> DiscoverAsync(ConnectorSettings settings,
        SyncContext context, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var client = CreateClient();
        var offset = 0;
        const int limit = 500;
        while (true)
        {
            await RateLimitAsync(context, ct);
            using var response = await SendWithRetryAsync(client,
                () => BuildRequest(settings, $"{BaseUrl}/assets?limit={limit}&offset={offset}"), ct: ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("assets", out var assets)) yield break;

            var count = 0;
            foreach (var item in assets.EnumerateArray())
            {
                count++;
                var asset = new DiscoveredAsset
                {
                    Source = ConnectorType.Tenable,
                    ExternalId = GetString(item, "id") ?? string.Empty,
                    Hostname = FirstArrayValue(item, "hostname") ?? FirstArrayValue(item, "netbios_name"),
                    Fqdn = FirstArrayValue(item, "fqdn"),
                    OperatingSystem = FirstArrayValue(item, "operating_system"),
                    RawJson = item.GetRawText()
                };
                var ip = FirstArrayValue(item, "ipv4");
                var mac = FirstArrayValue(item, "mac_address");
                if (ip is not null || mac is not null)
                    asset.Interfaces.Add(new DiscoveredInterface { IpAddress = ip, MacAddress = mac, IsPrimary = true });
                if (GetString(item, "last_seen") is { } lastSeen && DateTime.TryParse(lastSeen, out var seen))
                    asset.SeenAt = seen.ToUniversalTime();
                yield return asset;
            }
            if (count < limit) yield break;
            offset += limit;
        }
    }

    private static HttpRequestMessage BuildRequest(ConnectorSettings settings, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-ApiKeys",
            $"accessKey={settings.Get("accessKey")};secretKey={settings.Get("secretKey")}");
        return request;
    }

    private static string? FirstArrayValue(JsonElement element, string property)
        => element.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array &&
           arr.GetArrayLength() > 0
            ? arr[0].GetString()
            : null;
}

/// <summary>
/// Qualys Global AssetView connector. Settings: baseUrl (https://gateway.qg1.apps.qualys.com), username, password.
/// </summary>
public class QualysConnector : RestConnectorBase
{
    public QualysConnector(IHttpClientFactory httpFactory, ILogger<QualysConnector> logger)
        : base(httpFactory, logger) { }

    public override ConnectorType Type => ConnectorType.Qualys;

    public override async Task<ConnectorHealth> CheckHealthAsync(ConnectorSettings settings, CancellationToken ct = default)
    {
        try
        {
            await AcquireTokenAsync(settings, ct);
            return new ConnectorHealth(true, "Qualys JWT acquired");
        }
        catch (Exception ex)
        {
            return new ConnectorHealth(false, ex.Message);
        }
    }

    public override async IAsyncEnumerable<DiscoveredAsset> DiscoverAsync(ConnectorSettings settings,
        SyncContext context, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var baseUrl = settings.Get("baseUrl").TrimEnd('/');
        var token = await AcquireTokenAsync(settings, ct);
        var client = CreateClient();
        string? lastSeenId = null;

        while (true)
        {
            await RateLimitAsync(context, ct);
            var url = $"{baseUrl}/rest/2.0/search/am/asset?pageSize=300";
            if (lastSeenId is not null) url += $"&lastSeenAssetId={lastSeenId}";
            using var response = await SendWithRetryAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new("Bearer", token);
                return request;
            }, ct: ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("assetListData", out var listData) ||
                !listData.TryGetProperty("asset", out var assets))
                yield break;

            var count = 0;
            foreach (var item in assets.EnumerateArray())
            {
                count++;
                var externalId = GetString(item, "assetId") ?? string.Empty;
                lastSeenId = externalId;
                var asset = new DiscoveredAsset
                {
                    Source = ConnectorType.Qualys,
                    ExternalId = externalId,
                    Hostname = GetString(item, "assetName") ?? GetString(item, "dnsName"),
                    Fqdn = GetString(item, "fqdn"),
                    OperatingSystem = GetString(item, "operatingSystem", "osName"),
                    SerialNumber = GetString(item, "hardware", "serialNumber"),
                    BiosUuid = GetString(item, "biosUuid"),
                    RawJson = item.GetRawText()
                };
                if (GetString(item, "address") is { } ip)
                    asset.Interfaces.Add(new DiscoveredInterface { IpAddress = ip, IsPrimary = true });
                if (GetString(item, "sensorLastUpdatedDate") is { } updated && DateTime.TryParse(updated, out var seen))
                    asset.SeenAt = seen.ToUniversalTime();
                yield return asset;
            }
            if (count < 300) yield break;
        }
    }

    private async Task<string> AcquireTokenAsync(ConnectorSettings settings, CancellationToken ct)
    {
        var client = CreateClient();
        using var response = await SendWithRetryAsync(client, () => new HttpRequestMessage(HttpMethod.Post,
            $"{settings.Get("baseUrl").TrimEnd('/')}/auth")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = settings.Get("username"),
                ["password"] = settings.Get("password"),
                ["token"] = "true"
            })
        }, ct: ct);
        return (await response.Content.ReadAsStringAsync(ct)).Trim();
    }
}
