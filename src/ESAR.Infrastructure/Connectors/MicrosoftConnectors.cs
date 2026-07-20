using System.Runtime.CompilerServices;
using System.Text.Json;
using Esar.Application.Abstractions;
using Esar.Application.Contracts;
using Esar.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Esar.Infrastructure.Connectors;

/// <summary>Shared client-credentials OAuth2 flow for Microsoft (Entra ID protected) APIs.</summary>
public abstract class AadConnectorBase : RestConnectorBase
{
    protected AadConnectorBase(IHttpClientFactory httpFactory, ILogger logger) : base(httpFactory, logger) { }

    protected async Task<string> AcquireTokenAsync(ConnectorSettings settings, string scope, CancellationToken ct)
    {
        var tenantId = settings.Get("tenantId");
        var client = CreateClient();
        using var response = await SendWithRetryAsync(client, () => new HttpRequestMessage(HttpMethod.Post,
            $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = settings.Get("clientId"),
                ["client_secret"] = settings.Get("clientSecret"),
                ["scope"] = scope
            })
        }, ct: ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("access_token").GetString()
               ?? throw new InvalidOperationException("Token endpoint returned no access_token.");
    }

    protected async IAsyncEnumerable<JsonElement> PageGraphAsync(string token, string initialUrl,
        SyncContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var client = CreateClient();
        var url = initialUrl;
        while (url is not null)
        {
            await RateLimitAsync(context, ct);
            using var response = await SendWithRetryAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new("Bearer", token);
                return request;
            }, ct: ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("value", out var items))
            {
                foreach (var item in items.EnumerateArray())
                    yield return item.Clone();
            }
            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }
    }
}

/// <summary>Discovers device objects from Microsoft Entra ID via Microsoft Graph.</summary>
public class EntraIdConnector : AadConnectorBase
{
    private const string GraphScope = "https://graph.microsoft.com/.default";
    public EntraIdConnector(IHttpClientFactory httpFactory, ILogger<EntraIdConnector> logger)
        : base(httpFactory, logger) { }

    public override ConnectorType Type => ConnectorType.EntraId;

    public override async Task<ConnectorHealth> CheckHealthAsync(ConnectorSettings settings, CancellationToken ct = default)
    {
        try
        {
            await AcquireTokenAsync(settings, GraphScope, ct);
            return new ConnectorHealth(true, "Token acquired from Entra ID");
        }
        catch (Exception ex)
        {
            return new ConnectorHealth(false, ex.Message);
        }
    }

    public override async IAsyncEnumerable<DiscoveredAsset> DiscoverAsync(ConnectorSettings settings,
        SyncContext context, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var token = await AcquireTokenAsync(settings, GraphScope, ct);
        var url = "https://graph.microsoft.com/v1.0/devices?$top=999" +
                  "&$select=id,deviceId,displayName,operatingSystem,operatingSystemVersion," +
                  "trustType,accountEnabled,approximateLastSignInDateTime";
        await foreach (var device in PageGraphAsync(token, url, context, ct))
        {
            var asset = new DiscoveredAsset
            {
                Source = ConnectorType.EntraId,
                ExternalId = GetString(device, "id") ?? string.Empty,
                Hostname = GetString(device, "displayName"),
                OperatingSystem = GetString(device, "operatingSystem"),
                OsVersion = GetString(device, "operatingSystemVersion"),
                RawJson = device.GetRawText()
            };
            asset.Identifiers[MatchAttributes.ObjectGuid] = asset.ExternalId;
            if (GetString(device, "approximateLastSignInDateTime") is { } lastSignIn &&
                DateTime.TryParse(lastSignIn, out var seen))
                asset.SeenAt = seen.ToUniversalTime();
            yield return asset;
        }
    }
}

/// <summary>Discovers managed devices from Microsoft Intune via Microsoft Graph.</summary>
public class IntuneConnector : AadConnectorBase
{
    private const string GraphScope = "https://graph.microsoft.com/.default";
    public IntuneConnector(IHttpClientFactory httpFactory, ILogger<IntuneConnector> logger)
        : base(httpFactory, logger) { }

    public override ConnectorType Type => ConnectorType.Intune;

    public override async Task<ConnectorHealth> CheckHealthAsync(ConnectorSettings settings, CancellationToken ct = default)
    {
        try
        {
            await AcquireTokenAsync(settings, GraphScope, ct);
            return new ConnectorHealth(true, "Token acquired");
        }
        catch (Exception ex)
        {
            return new ConnectorHealth(false, ex.Message);
        }
    }

    public override async IAsyncEnumerable<DiscoveredAsset> DiscoverAsync(ConnectorSettings settings,
        SyncContext context, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var token = await AcquireTokenAsync(settings, GraphScope, ct);
        var url = "https://graph.microsoft.com/v1.0/deviceManagement/managedDevices?$top=1000";
        await foreach (var device in PageGraphAsync(token, url, context, ct))
        {
            var asset = new DiscoveredAsset
            {
                Source = ConnectorType.Intune,
                ExternalId = GetString(device, "id") ?? string.Empty,
                Hostname = GetString(device, "deviceName"),
                OperatingSystem = GetString(device, "operatingSystem"),
                OsVersion = GetString(device, "osVersion"),
                SerialNumber = GetString(device, "serialNumber"),
                Manufacturer = GetString(device, "manufacturer"),
                Model = GetString(device, "model"),
                OwnerName = GetString(device, "userDisplayName"),
                OwnerEmail = GetString(device, "emailAddress"),
                RawJson = device.GetRawText()
            };
            if (GetString(device, "azureADDeviceId") is { } aadId)
                asset.Identifiers[MatchAttributes.ObjectGuid] = aadId;
            var mac = GetString(device, "wiFiMacAddress") ?? GetString(device, "ethernetMacAddress");
            if (mac is not null) asset.Interfaces.Add(new DiscoveredInterface { MacAddress = mac });
            if (GetString(device, "complianceState") is { } compliance)
                asset.Tags["intune_compliance"] = compliance;
            if (GetString(device, "isEncrypted") is { } encrypted)
                asset.Tags["disk_encryption"] = encrypted;
            if (GetString(device, "lastSyncDateTime") is { } sync && DateTime.TryParse(sync, out var seen))
                asset.SeenAt = seen.ToUniversalTime();
            yield return asset;
        }
    }
}

/// <summary>Discovers onboarded machines from Microsoft Defender for Endpoint.</summary>
public class MicrosoftDefenderConnector : AadConnectorBase
{
    private const string DefenderScope = "https://api.securitycenter.microsoft.com/.default";
    public MicrosoftDefenderConnector(IHttpClientFactory httpFactory, ILogger<MicrosoftDefenderConnector> logger)
        : base(httpFactory, logger) { }

    public override ConnectorType Type => ConnectorType.MicrosoftDefender;

    public override async Task<ConnectorHealth> CheckHealthAsync(ConnectorSettings settings, CancellationToken ct = default)
    {
        try
        {
            await AcquireTokenAsync(settings, DefenderScope, ct);
            return new ConnectorHealth(true, "Token acquired");
        }
        catch (Exception ex)
        {
            return new ConnectorHealth(false, ex.Message);
        }
    }

    public override async IAsyncEnumerable<DiscoveredAsset> DiscoverAsync(ConnectorSettings settings,
        SyncContext context, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var token = await AcquireTokenAsync(settings, DefenderScope, ct);
        var url = "https://api.securitycenter.microsoft.com/api/machines";
        if (context.Mode == SyncMode.Incremental && context.LastSuccessfulSyncAt is { } since)
            url += $"?$filter=lastSeen ge {since:yyyy-MM-ddTHH:mm:ssZ}";

        await foreach (var machine in PageGraphAsync(token, url, context, ct))
        {
            var asset = new DiscoveredAsset
            {
                Source = ConnectorType.MicrosoftDefender,
                ExternalId = GetString(machine, "id") ?? string.Empty,
                Hostname = GetString(machine, "computerDnsName"),
                OperatingSystem = GetString(machine, "osPlatform"),
                OsVersion = GetString(machine, "osVersion"),
                RawJson = machine.GetRawText()
            };
            asset.Identifiers[MatchAttributes.EndpointId] = asset.ExternalId;
            if (GetString(machine, "lastIpAddress") is { } ip)
                asset.Interfaces.Add(new DiscoveredInterface { IpAddress = ip, IsPrimary = true });
            if (GetString(machine, "healthStatus") is { } health) asset.Tags["defender_health"] = health;
            asset.Tags["antivirus"] = "true";
            if (GetString(machine, "lastSeen") is { } lastSeen && DateTime.TryParse(lastSeen, out var seen))
                asset.SeenAt = seen.ToUniversalTime();
            yield return asset;
        }
    }
}

/// <summary>Discovers Azure resources (VMs and network devices) via Azure Resource Graph.</summary>
public class AzureConnector : AadConnectorBase
{
    private const string ArmScope = "https://management.azure.com/.default";
    public AzureConnector(IHttpClientFactory httpFactory, ILogger<AzureConnector> logger)
        : base(httpFactory, logger) { }

    public override ConnectorType Type => ConnectorType.Azure;

    public override async Task<ConnectorHealth> CheckHealthAsync(ConnectorSettings settings, CancellationToken ct = default)
    {
        try
        {
            await AcquireTokenAsync(settings, ArmScope, ct);
            return new ConnectorHealth(true, "Token acquired for Azure Resource Manager");
        }
        catch (Exception ex)
        {
            return new ConnectorHealth(false, ex.Message);
        }
    }

    public override async IAsyncEnumerable<DiscoveredAsset> DiscoverAsync(ConnectorSettings settings,
        SyncContext context, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var token = await AcquireTokenAsync(settings, ArmScope, ct);
        var client = CreateClient();
        // Buffer VMs keyed by resource id (case-insensitive) so NIC rows can enrich them.
        var assets = new Dictionary<string, DiscoveredAsset>(StringComparer.OrdinalIgnoreCase);
        const string query = @"Resources
            | where type in~ ('microsoft.compute/virtualmachines','microsoft.hybridcompute/machines')
            | extend os = tostring(properties.storageProfile.osDisk.osType),
                     vmId = tostring(properties.vmId),
                     computerName = tostring(properties.osProfile.computerName)
            | project id, name, computerName, os, vmId, location, subscriptionId, resourceGroup, tags";

        string? skipToken = null;
        do
        {
            await RateLimitAsync(context, ct);
            var body = JsonSerializer.Serialize(new
            {
                query,
                options = new { resultFormat = "objectArray", skipToken }
            });
            using var response = await SendWithRetryAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01")
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new("Bearer", token);
                return request;
            }, ct: ct);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            skipToken = GetString(doc.RootElement, "$skipToken");

            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var resource in data.EnumerateArray())
                {
                    var resourceId = GetString(resource, "id") ?? string.Empty;
                    var asset = new DiscoveredAsset
                    {
                        Source = ConnectorType.Azure,
                        ExternalId = resourceId,
                        Hostname = GetString(resource, "computerName") ?? GetString(resource, "name"),
                        OperatingSystem = GetString(resource, "os"),
                        AssetType = AssetType.CloudInstance,
                        CloudProvider = "Azure",
                        CloudResourceId = resourceId,
                        CloudRegion = GetString(resource, "location"),
                        CloudSubscriptionId = GetString(resource, "subscriptionId"),
                        RawJson = resource.GetRawText()
                    };
                    asset.Identifiers[MatchAttributes.AzureResourceId] = resourceId;
                    if (GetString(resource, "vmId") is { } vmId)
                        asset.Identifiers[MatchAttributes.BiosUuid] = vmId;
                    if (GetString(resource, "resourceGroup") is { } resourceGroup)
                        asset.Tags["azure_resource_group"] = resourceGroup;
                    if (resource.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var tag in tags.EnumerateObject())
                            asset.Tags[tag.Name] = tag.Value.ValueKind == JsonValueKind.String
                                ? tag.Value.GetString() ?? string.Empty
                                : tag.Value.GetRawText();
                    }
                    assets[resourceId] = asset;
                }
            }
        } while (!string.IsNullOrEmpty(skipToken));

        // Enrich VMs with NIC data (private IP, MAC, public-IP presence). Best-effort:
        // partial network RBAC must not drop the VMs already discovered.
        const string nicQuery = @"Resources
            | where type =~ 'microsoft.network/networkinterfaces'
            | extend vmResourceId = tolower(tostring(properties.virtualMachine.id)),
                     mac = tostring(properties.macAddress)
            | where isnotempty(vmResourceId)
            | mv-expand ipconfig = properties.ipConfigurations
            | extend privateIp = tostring(ipconfig.properties.privateIPAddress),
                     publicIpId = tostring(ipconfig.properties.publicIPAddress.id)
            | project vmResourceId, mac, privateIp, publicIpId";
        List<JsonElement> nics;
        try
        {
            nics = await QueryAllAsync(token, nicQuery, context, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Azure NIC enrichment failed — returning VMs without IP/MAC");
            nics = new List<JsonElement>();
        }
        foreach (var nic in nics)
        {
            var vmResourceId = GetString(nic, "vmResourceId");
            if (vmResourceId is null || !assets.TryGetValue(vmResourceId, out var asset)) continue;
            var privateIp = GetString(nic, "privateIp");
            var mac = GetString(nic, "mac");
            if (!string.IsNullOrEmpty(privateIp) || !string.IsNullOrEmpty(mac))
                asset.Interfaces.Add(new DiscoveredInterface
                {
                    IpAddress = string.IsNullOrEmpty(privateIp) ? null : privateIp,
                    MacAddress = string.IsNullOrEmpty(mac) ? null : mac,
                    IsPrimary = asset.Interfaces.Count == 0
                });
            if (!string.IsNullOrEmpty(GetString(nic, "publicIpId")))
            {
                asset.Tags["public_ip"] = "true";
                asset.Tags["internet_facing"] = "true"; // consumed by the risk scoring engine
            }
        }

        foreach (var asset in assets.Values) yield return asset;
    }

    /// <summary>Runs a Resource Graph query and buffers every page into a list.</summary>
    private async Task<List<JsonElement>> QueryAllAsync(string token, string query, SyncContext context,
        CancellationToken ct)
    {
        var results = new List<JsonElement>();
        var client = CreateClient();
        string? skipToken = null;
        do
        {
            await RateLimitAsync(context, ct);
            var body = JsonSerializer.Serialize(new
            {
                query,
                options = new { resultFormat = "objectArray", skipToken }
            });
            using var response = await SendWithRetryAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01")
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new("Bearer", token);
                return request;
            }, ct: ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            skipToken = GetString(doc.RootElement, "$skipToken");
            if (doc.RootElement.TryGetProperty("data", out var data))
                foreach (var row in data.EnumerateArray())
                    results.Add(row.Clone());
        } while (!string.IsNullOrEmpty(skipToken));
        return results;
    }
}
