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
/// Settings: baseUrl (https://vcenter.example.com), username, password,
/// allowSelfSignedCert (optional "true" for appliances with a self-signed cert).
///
/// Per VM it collects: BIOS/instance UUID (hard-match identity), guest hostname/FQDN and OS,
/// network interfaces (IP + MAC, for MAC/IP correlation), CPU/memory, VMware Tools state and
/// hardware version — surfaced as tags so assets stay filterable by any of them.
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
            using var client = CreateVCenterClient(settings);
            var baseUrl = settings.Get("baseUrl").TrimEnd('/');
            var session = await AcquireSessionAsync(client, baseUrl, settings, ct);
            await CloseSessionAsync(client, baseUrl, session, ct);
            return new ConnectorHealth(true, "vCenter session created and closed");
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
        using var client = CreateVCenterClient(settings);
        var session = await AcquireSessionAsync(client, baseUrl, settings, ct);

        try
        {
            var list = await TryGetAsync(client, $"{baseUrl}/api/vcenter/vm", session, ct)
                ?? throw new InvalidOperationException("vCenter returned no VM list.");

            foreach (var vm in list.EnumerateArray())
            {
                await RateLimitAsync(context, ct);
                var vmId = GetString(vm, "vm");
                if (vmId is null) continue;
                yield return await BuildAssetAsync(client, baseUrl, session, vm, vmId, ct);
            }
        }
        finally
        {
            await CloseSessionAsync(client, baseUrl, session, ct);
        }
    }

    private async Task<DiscoveredAsset> BuildAssetAsync(HttpClient client, string baseUrl, string session,
        JsonElement vm, string vmId, CancellationToken ct)
    {
        var asset = new DiscoveredAsset
        {
            Source = ConnectorType.VmwareVCenter,
            ExternalId = vmId,
            Hostname = GetString(vm, "name"),
            AssetType = AssetType.VirtualMachine,
            Manufacturer = "VMware, Inc.",
            Model = "VMware Virtual Machine",
            RawJson = vm.GetRawText(),
        };

        // Summary fields returned by the VM list itself — always available, no extra call.
        if (GetString(vm, "power_state") is { } power) asset.Tags["vm_power_state"] = power;
        if (GetString(vm, "cpu_count") is { } cpu) asset.Tags["cpu_count"] = cpu;
        if (GetString(vm, "memory_size_MiB") is { } mem) asset.Tags["memory_mib"] = mem;

        // Per-VM detail: identity (BIOS/instance UUID), guest OS code, hardware version.
        var detail = await TryGetAsync(client, $"{baseUrl}/api/vcenter/vm/{vmId}", session, ct);
        if (detail is { } d)
        {
            asset.RawJson = d.GetRawText();
            if (GetString(d, "identity", "bios_uuid") is { } biosUuid)
            {
                asset.BiosUuid = biosUuid;
                asset.Identifiers[MatchAttributes.VmwareUuid] = biosUuid;
            }
            if (GetString(d, "identity", "instance_uuid") is { } instanceUuid)
                asset.Tags["instance_uuid"] = instanceUuid;
            asset.OperatingSystem = GetString(d, "guest_OS");
            if (GetString(d, "hardware", "version") is { } hw) asset.Tags["hardware_version"] = hw;
        }

        // Guest identity (needs VMware Tools): configured hostname/FQDN, friendly OS name, IP family.
        var guest = await TryGetAsync(client, $"{baseUrl}/api/vcenter/vm/{vmId}/guest/identity", session, ct);
        if (guest is { } g)
        {
            asset.Fqdn = GetString(g, "host_name");
            if (GetString(g, "full_name", "default_message") is { } friendlyOs) asset.OperatingSystem = friendlyOs;
            if (GetString(g, "family") is { } family) asset.Tags["guest_os_family"] = family;
        }

        await CollectInterfacesAsync(client, baseUrl, session, vmId, asset, ct);

        // VMware Tools health — useful as a filter (e.g. VMs with tools not running).
        var tools = await TryGetAsync(client, $"{baseUrl}/api/vcenter/vm/{vmId}/tools", session, ct);
        if (tools is { } t)
        {
            if (GetString(t, "run_state") is { } runState) asset.Tags["vmware_tools_state"] = runState;
            if (GetString(t, "version_status") is { } versionStatus) asset.Tags["vmware_tools_version"] = versionStatus;
        }

        return asset;
    }

    // Populates asset.Interfaces (IP + MAC) and the primary-MAC match identifier. Prefers guest
    // networking (real IPs, needs Tools); falls back to virtual hardware for MACs when Tools is off.
    private async Task CollectInterfacesAsync(HttpClient client, string baseUrl, string session,
        string vmId, DiscoveredAsset asset, CancellationToken ct)
    {
        string? primaryMac = null;
        var hasMac = false;

        void Add(string? ip, string? mac)
        {
            asset.Interfaces.Add(new DiscoveredInterface { IpAddress = ip, MacAddress = mac, IsPrimary = asset.Interfaces.Count == 0 });
            if (mac is not null) { hasMac = true; primaryMac ??= mac; }
        }

        var nics = await TryGetAsync(client, $"{baseUrl}/api/vcenter/vm/{vmId}/guest/networking/interfaces", session, ct);
        if (nics is { ValueKind: JsonValueKind.Array })
        {
            foreach (var nic in nics.Value.EnumerateArray())
            {
                var mac = GetString(nic, "mac_address");
                var added = false;
                if (nic.TryGetProperty("ip", out var ipObj) &&
                    ipObj.TryGetProperty("ip_addresses", out var addrs) && addrs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var addr in addrs.EnumerateArray())
                    {
                        if (GetString(addr, "ip_address") is { } ip) { Add(ip, mac); added = true; }
                    }
                }
                if (!added && mac is not null) Add(null, mac);
            }
        }

        // Fallback: virtual NIC MACs from hardware config when the guest reported none.
        if (!hasMac)
        {
            var eth = await TryGetAsync(client, $"{baseUrl}/api/vcenter/vm/{vmId}/hardware/ethernet", session, ct);
            if (eth is { ValueKind: JsonValueKind.Array })
            {
                foreach (var e in eth.Value.EnumerateArray())
                {
                    var nicId = GetString(e, "nic");
                    if (nicId is null) continue;
                    var nicDetail = await TryGetAsync(client,
                        $"{baseUrl}/api/vcenter/vm/{vmId}/hardware/ethernet/{nicId}", session, ct);
                    if (nicDetail is { } nd && GetString(nd, "mac_address") is { } mac) Add(null, mac);
                }
            }
        }

        if (primaryMac is not null) asset.Identifiers[MatchAttributes.MacAddress] = primaryMac;
    }

    /// <summary>GET a vSphere resource, returning a cloned root element, or null on HTTP/JSON failure.</summary>
    private async Task<JsonElement?> TryGetAsync(HttpClient client, string url, string session, CancellationToken ct)
    {
        try
        {
            using var response = await SendWithRetryAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("vmware-api-session-id", session);
                return request;
            }, ct: ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            Logger.LogDebug(ex, "vCenter GET {Url} returned no usable data", url);
            return null;
        }
    }

    private async Task<string> AcquireSessionAsync(HttpClient client, string baseUrl,
        ConnectorSettings settings, CancellationToken ct)
    {
        var auth = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{settings.Get("username")}:{settings.Get("password")}"));
        using var response = await SendWithRetryAsync(client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/session");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
            return request;
        }, ct: ct);
        return (await response.Content.ReadAsStringAsync(ct)).Trim('"', ' ', '\n');
    }

    // vCenter caps concurrent sessions, so always release the one we opened. Best-effort.
    private async Task CloseSessionAsync(HttpClient client, string baseUrl, string session, CancellationToken ct)
    {
        try
        {
            using var response = await SendWithRetryAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/api/session");
                request.Headers.Add("vmware-api-session-id", session);
                return request;
            }, ct: ct);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "vCenter session close failed");
        }
    }

    // vCenter appliances ship with a self-signed cert by default; opt in per connector rather than
    // trusting it globally. When off, uses the pooled factory client with normal validation.
    private HttpClient CreateVCenterClient(ConnectorSettings settings)
    {
        if (string.Equals(settings.GetOptional("allowSelfSignedCert"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            });
        }
        return CreateClient();
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
