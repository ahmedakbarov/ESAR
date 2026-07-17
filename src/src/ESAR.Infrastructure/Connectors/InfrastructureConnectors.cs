using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Esar.Application.Abstractions;
using Esar.Application.Contracts;
using Esar.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Esar.Infrastructure.Connectors;

/// <summary>
/// VMware vCenter connector using the vSphere Automation REST API.
/// Settings: baseUrl (https://vcenter.example.com), username, password.
/// </summary>
public class VmwareVCenterConnector : RestConnectorBase
{
    public VmwareVCenterConnector(IHttpClientFactory httpFactory, ILogger<VmwareVCenterConnector> logger)
        : base(httpFactory, logger) { }

    public override ConnectorType Type => ConnectorType.VmwareVCenter;

    public override async Task<ConnectorHealth> CheckHealthAsync(ConnectorSettings settings, CancellationToken ct = default)
    {
        try
        {
            await AcquireSessionAsync(settings, ct);
            return new ConnectorHealth(true, "vCenter session created");
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
        var session = await AcquireSessionAsync(settings, ct);
        var client = CreateClient();

        using var listResponse = await SendWithRetryAsync(client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/vcenter/vm");
            request.Headers.Add("vmware-api-session-id", session);
            return request;
        }, ct: ct);
        using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(ct));

        foreach (var vm in listDoc.RootElement.EnumerateArray())
        {
            await RateLimitAsync(context, ct);
            var vmId = GetString(vm, "vm");
            if (vmId is null) continue;

            // Per-VM detail: identity (BIOS UUID), guest OS, power state.
            JsonElement detail = default;
            var hasDetail = false;
            try
            {
                using var detailResponse = await SendWithRetryAsync(client, () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/vcenter/vm/{vmId}");
                    request.Headers.Add("vmware-api-session-id", session);
                    return request;
                }, ct: ct);
                using var detailDoc = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync(ct));
                detail = detailDoc.RootElement.Clone();
                hasDetail = true;
            }
            catch (HttpRequestException ex)
            {
                Logger.LogWarning(ex, "vCenter detail fetch failed for {VmId}", vmId);
            }

            var asset = new DiscoveredAsset
            {
                Source = ConnectorType.VmwareVCenter,
                ExternalId = vmId,
                Hostname = GetString(vm, "name"),
                AssetType = AssetType.VirtualMachine,
                RawJson = hasDetail ? detail.GetRawText() : vm.GetRawText()
            };
            if (hasDetail)
            {
                if (GetString(detail, "identity", "bios_uuid") is { } biosUuid)
                {
                    asset.BiosUuid = biosUuid;
                    asset.Identifiers[MatchAttributes.VmwareUuid] = biosUuid;
                }
                asset.OperatingSystem = GetString(detail, "guest_OS");
                if (GetString(vm, "power_state") is { } power)
                    asset.Tags["vm_power_state"] = power;
            }
            yield return asset;
        }
    }

    private async Task<string> AcquireSessionAsync(ConnectorSettings settings, CancellationToken ct)
    {
        var client = CreateClient();
        var auth = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{settings.Get("username")}:{settings.Get("password")}"));
        using var response = await SendWithRetryAsync(client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{settings.Get("baseUrl").TrimEnd('/')}/api/session");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
            return request;
        }, ct: ct);
        var session = (await response.Content.ReadAsStringAsync(ct)).Trim('"', ' ', '\n');
        return session;
    }
}

/// <summary>
/// ServiceNow CMDB connector reading cmdb_ci_computer (configurable table).
/// Settings: instanceUrl, username, password, table (optional).
/// </summary>
public class ServiceNowCmdbConnector : RestConnectorBase
{
    public ServiceNowCmdbConnector(IHttpClientFactory httpFactory, ILogger<ServiceNowCmdbConnector> logger)
        : base(httpFactory, logger) { }

    public override ConnectorType Type => ConnectorType.ServiceNowCmdb;

    public override async Task<ConnectorHealth> CheckHealthAsync(ConnectorSettings settings, CancellationToken ct = default)
    {
        try
        {
            var client = CreateClient();
            using var response = await SendWithRetryAsync(client,
                () => BuildRequest(settings, BuildUrl(settings, limit: 1, offset: 0)), ct: ct);
            return new ConnectorHealth(true, "ServiceNow Table API reachable");
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
                () => BuildRequest(settings, BuildUrl(settings, limit, offset)), ct: ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("result", out var items)) yield break;

            var count = 0;
            foreach (var item in items.EnumerateArray())
            {
                count++;
                var asset = new DiscoveredAsset
                {
                    Source = ConnectorType.ServiceNowCmdb,
                    ExternalId = GetString(item, "sys_id") ?? string.Empty,
                    Hostname = GetString(item, "name"),
                    Fqdn = GetString(item, "fqdn"),
                    OperatingSystem = GetString(item, "os"),
                    OsVersion = GetString(item, "os_version"),
                    SerialNumber = GetString(item, "serial_number"),
                    Manufacturer = GetString(item, "manufacturer", "display_value") ?? GetString(item, "manufacturer"),
                    OwnerName = GetString(item, "assigned_to", "display_value"),
                    Department = GetString(item, "department", "display_value"),
                    BusinessUnit = GetString(item, "u_business_unit", "display_value"),
                    Location = GetString(item, "location", "display_value"),
                    RawJson = item.GetRawText()
                };
                if (GetString(item, "ip_address") is { } ip)
                    asset.Interfaces.Add(new DiscoveredInterface { IpAddress = ip, IsPrimary = true });
                if (GetString(item, "mac_address") is { } mac)
                {
                    if (asset.Interfaces.Count > 0) asset.Interfaces[0].MacAddress = mac;
                    else asset.Interfaces.Add(new DiscoveredInterface { MacAddress = mac });
                }
                if (GetString(item, "sys_updated_on") is { } updated && DateTime.TryParse(updated, out var seen))
                    asset.SeenAt = seen.ToUniversalTime();
                yield return asset;
            }
            if (count < limit) yield break;
            offset += limit;
        }
    }

    private static string BuildUrl(ConnectorSettings settings, int limit, int offset)
    {
        var table = settings.GetOptional("table") ?? "cmdb_ci_computer";
        return $"{settings.Get("instanceUrl").TrimEnd('/')}/api/now/table/{table}" +
               $"?sysparm_limit={limit}&sysparm_offset={offset}&sysparm_display_value=all";
    }

    private static HttpRequestMessage BuildRequest(ConnectorSettings settings, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var auth = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{settings.Get("username")}:{settings.Get("password")}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
}

/// <summary>
/// Generic REST connector for sources without a dedicated implementation.
/// Settings: url, authHeader (optional, e.g. "Authorization: Bearer xyz"),
/// itemsPath (dot path to the array, default root), and field mappings:
/// idField, hostnameField, osField, ipField, macField, serialField.
/// </summary>
public class GenericRestConnector : RestConnectorBase
{
    public GenericRestConnector(IHttpClientFactory httpFactory, ILogger<GenericRestConnector> logger)
        : base(httpFactory, logger) { }

    public override ConnectorType Type => ConnectorType.GenericRest;

    public override async Task<ConnectorHealth> CheckHealthAsync(ConnectorSettings settings, CancellationToken ct = default)
    {
        try
        {
            var client = CreateClient();
            using var response = await SendWithRetryAsync(client, () => BuildRequest(settings), ct: ct);
            return new ConnectorHealth(true, $"Endpoint returned {(int)response.StatusCode}");
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
        using var response = await SendWithRetryAsync(client, () => BuildRequest(settings), ct: ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        var items = doc.RootElement;
        var itemsPath = settings.GetOptional("itemsPath");
        if (!string.IsNullOrEmpty(itemsPath))
        {
            foreach (var segment in itemsPath.Split('.'))
            {
                if (!items.TryGetProperty(segment, out items))
                    throw new InvalidOperationException($"itemsPath segment '{segment}' not found in response.");
            }
        }
        if (items.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Resolved itemsPath is not a JSON array.");

        var idField = settings.GetOptional("idField") ?? "id";
        var hostnameField = settings.GetOptional("hostnameField") ?? "hostname";
        foreach (var item in items.EnumerateArray())
        {
            var asset = new DiscoveredAsset
            {
                Source = ConnectorType.GenericRest,
                ExternalId = GetString(item, idField) ?? string.Empty,
                Hostname = GetString(item, hostnameField),
                OperatingSystem = settings.GetOptional("osField") is { } osField ? GetString(item, osField) : null,
                SerialNumber = settings.GetOptional("serialField") is { } serialField ? GetString(item, serialField) : null,
                RawJson = item.GetRawText()
            };
            var ip = settings.GetOptional("ipField") is { } ipField ? GetString(item, ipField) : null;
            var mac = settings.GetOptional("macField") is { } macField ? GetString(item, macField) : null;
            if (ip is not null || mac is not null)
                asset.Interfaces.Add(new DiscoveredInterface { IpAddress = ip, MacAddress = mac });
            yield return asset;
        }
    }

    private static HttpRequestMessage BuildRequest(ConnectorSettings settings)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, settings.Get("url"));
        if (settings.GetOptional("authHeader") is { } header && header.Contains(':'))
        {
            var parts = header.Split(':', 2);
            request.Headers.TryAddWithoutValidation(parts[0].Trim(), parts[1].Trim());
        }
        return request;
    }
}
