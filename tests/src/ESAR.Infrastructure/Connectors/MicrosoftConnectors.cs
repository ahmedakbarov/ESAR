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

/// <summary>
/// Discovers Azure resources via Azure Resource Graph: VMs enriched with NIC data
/// (private IPs, MACs, public-IP presence), tags mapped to ESAR fields, plus Arc machines.
/// Optional setting: subscriptionIds — comma-separated list or JSON array.
/// </summary>
public class AzureConnector : AadConnectorBase
{
    private const string ArmScope = "https://management.azure.com/.default";

    private const string VmQuery = @"Resources
        | where type =~ 'microsoft.compute/virtualmachines'
        | extend osType = tostring(properties.storageProfile.osDisk.osType),
                 vmId = tostring(properties.vmId),
                 computerName = tostring(properties.osProfile.computerName)
        | project id, name, computerName, osType, vmId, location, subscriptionId, resourceGroup, tags";

    private const string ArcQuery = @"Resources
        | where type =~ 'microsoft.hybridcompute/machines'
        | extend osType = tostring(properties.osName),
                 computerName = tostring(properties.machineFqdn)
        | project id, name, computerName, osType, location, subscriptionId, resourceGroup, tags";

    private const string NicQuery = @"Resources
        | where type =~ 'microsoft.network/networkinterfaces'
        | extend vmResourceId = tolower(tostring(properties.virtualMachine.id)),
                 macAddress = tostring(properties.macAddress)
        | where isnotempty(vmResourceId)
        | mv-expand ipconfig = properties.ipConfigurations
        | extend privateIp = tostring(ipconfig.properties.privateIPAddress),
                 publicIpId = tolower(tostring(ipconfig.properties.publicIPAddress.id))
        | join kind=leftouter (
            Resources
            | where type =~ 'microsoft.network/publicipaddresses'
            | extend publicIpId = tolower(id), publicIp = tostring(properties.ipAddress)
            | project publicIpId, publicIp
        ) on publicIpId
        | project vmResourceId, macAddress, privateIp, publicIp";

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
        var subscriptions = AzureAssetMapper.ParseSubscriptionIds(settings.GetOptional("subscriptionIds"));

        // 1. Virtual machines (aggregated by resource id, NIC rows applied afterwards).
        var vms = new Dictionary<string, DiscoveredAsset>(StringComparer.OrdinalIgnoreCase);
        await foreach (var row in QueryAsync(token, VmQuery, subscriptions, context, ct))
        {
            var asset = AzureAssetMapper.MapMachine(row, isCloudVm: true);
            if (asset is not null) vms[asset.ExternalId.ToLowerInvariant()] = asset;
        }

        // 2. NIC enrichment is best-effort: partial RBAC on network resources must not block discovery.
        try
        {
            await foreach (var nic in QueryAsync(token, NicQuery, subscriptions, context, ct))
            {
                var vmResourceId = AzureAssetMapper.GetString(nic, "vmResourceId");
                if (vmResourceId is not null && vms.TryGetValue(vmResourceId, out var asset))
                    AzureAssetMapper.ApplyNicRow(asset, nic);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Azure NIC enrichment failed — yielding VMs without IP/MAC data");
        }

        foreach (var asset in vms.Values) yield return asset;

        // 3. Azure Arc machines (no NIC join available).
        await foreach (var row in QueryAsync(token, ArcQuery, subscriptions, context, ct))
        {
            var arc = AzureAssetMapper.MapMachine(row, isCloudVm: false);
            if (arc is not null) yield return arc;
        }
    }

    private async IAsyncEnumerable<JsonElement> QueryAsync(string token, string query,
        IReadOnlyList<string> subscriptions, SyncContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var client = CreateClient();
        string? skipToken = null;
        do
        {
            await RateLimitAsync(context, ct);
            object options = new { resultFormat = "objectArray", skipToken };
            var body = JsonSerializer.Serialize(subscriptions.Count > 0
                ? new { query, options, subscriptions }
                : (object)new { query, options });
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
                foreach (var row in data.EnumerateArray())
                    yield return row.Clone();
            }
        } while (!string.IsNullOrEmpty(skipToken));
    }
}

/// <summary>Pure mapping/aggregation logic for Azure Resource Graph rows — unit-testable.</summary>
public static class AzureAssetMapper
{
    /// <summary>Accepts a comma-separated list or a JSON array of subscription ids.</summary>
    public static List<string> ParseSubscriptionIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                return (JsonSerializer.Deserialize<List<string>>(trimmed) ?? new List<string>())
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
            }
            catch (JsonException)
            {
                return new List<string>();
            }
        }
        return trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    public static DiscoveredAsset? MapMachine(JsonElement row, bool isCloudVm)
    {
        var resourceId = GetString(row, "id");
        if (string.IsNullOrEmpty(resourceId)) return null;

        var asset = new DiscoveredAsset
        {
            Source = ConnectorType.Azure,
            ExternalId = resourceId,
            Hostname = GetString(row, "computerName") ?? GetString(row, "name"),
            OperatingSystem = GetString(row, "osType"),
            AssetType = AssetType.CloudInstance,
            CloudProvider = "Azure",
            CloudResourceId = resourceId,
            CloudRegion = GetString(row, "location"),
            CloudSubscriptionId = GetString(row, "subscriptionId"),
            RawJson = row.GetRawText()
        };
        asset.Identifiers[MatchAttributes.AzureResourceId] = resourceId;
        if (isCloudVm && GetString(row, "vmId") is { } vmId && !string.IsNullOrEmpty(vmId))
            asset.Identifiers[MatchAttributes.BiosUuid] = vmId;
        if (GetString(row, "resourceGroup") is { } resourceGroup)
            asset.Tags["azure_resource_group"] = resourceGroup;
        if (row.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            ApplyTagMappings(asset, tags);
        return asset;
    }

    /// <summary>Applies one NIC/ipconfig row: interface + exposure tags when a public IP exists.</summary>
    public static void ApplyNicRow(DiscoveredAsset asset, JsonElement nicRow)
    {
        var privateIp = GetString(nicRow, "privateIp");
        var mac = GetString(nicRow, "macAddress");
        if (!string.IsNullOrEmpty(privateIp) || !string.IsNullOrEmpty(mac))
        {
            asset.Interfaces.Add(new DiscoveredInterface
            {
                IpAddress = privateIp,
                MacAddress = mac,
                IsPrimary = asset.Interfaces.Count == 0
            });
        }
        if (!string.IsNullOrEmpty(GetString(nicRow, "publicIp")))
        {
            asset.Tags["public_ip"] = "true";
            asset.Tags["internet_facing"] = "true"; // consumed by the risk scoring engine
        }
    }

    /// <summary>Case-insensitive tag → ESAR field mapping; unmapped tags are preserved as-is.</summary>
    public static void ApplyTagMappings(DiscoveredAsset asset, JsonElement tags)
    {
        foreach (var tag in tags.EnumerateObject())
        {
            var value = tag.Value.ValueKind == JsonValueKind.String
                ? tag.Value.GetString() ?? string.Empty
                : tag.Value.GetRawText();
            switch (tag.Name.ToLowerInvariant())
            {
                case "environment" when ParseEnvironment(value) is { } environment:
                    asset.Environment = environment;
                    break;
                case "criticality" when ParseCriticality(value) is { } criticality:
                    asset.Criticality = criticality;
                    break;
                case "owner":
                case "ownername":
                    asset.OwnerName = value;
                    break;
                case "owneremail":
                    asset.OwnerEmail = value;
                    break;
                case "businessunit":
                    asset.BusinessUnit = value;
                    break;
                case "department":
                    asset.Department = value;
                    break;
                case "classification":
                    asset.Classification = value;
                    break;
                default:
                    asset.Tags[tag.Name] = value;
                    break;
            }
        }
    }

    private static EnvironmentType? ParseEnvironment(string value) => value.Trim().ToLowerInvariant() switch
    {
        "prod" or "production" => EnvironmentType.Production,
        "staging" or "stage" => EnvironmentType.Staging,
        "test" or "qa" or "uat" => EnvironmentType.Test,
        "dev" or "development" => EnvironmentType.Development,
        "dr" or "disasterrecovery" => EnvironmentType.DisasterRecovery,
        _ => null
    };

    private static CriticalityLevel? ParseCriticality(string value) => value.Trim().ToLowerInvariant() switch
    {
        "critical" => CriticalityLevel.Critical,
        "high" => CriticalityLevel.High,
        "medium" or "med" => CriticalityLevel.Medium,
        "low" => CriticalityLevel.Low,
        _ => null
    };

    public static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;
}
