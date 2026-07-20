using System.Text.Json;
using Esar.Application.Abstractions;
using Esar.Application.Contracts;

namespace Esar.Infrastructure.Connectors;

internal sealed record AzureNicObservation(
    string VmResourceId,
    string? MacAddress,
    string? PrivateIpAddress,
    bool IsPrimary,
    string? PublicIpResourceId);

internal sealed record AzureNicEnrichmentResult(int MatchedVms, int InterfacesAdded, int PublicIpReferences);

/// <summary>
/// Parsing and mapping helpers for Azure Resource Graph network data.
/// Kept separate from HTTP calls so all IP/MAC behavior is unit-testable.
/// </summary>
internal static class AzureResourceGraph
{
    public static IReadOnlyList<string> ParseSubscriptionIds(ConnectorSettings settings)
    {
        var raw = settings.GetOptional("subscriptionIds");
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        var values = raw.TrimStart().StartsWith('[')
            ? ParseSubscriptionArray(raw)
            : raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var subscriptions = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (!Guid.TryParse(value, out var subscriptionId))
                throw new InvalidOperationException("Each 'subscriptionIds' value must be a valid Azure subscription GUID.");

            var normalized = subscriptionId.ToString();
            if (seen.Add(normalized)) subscriptions.Add(normalized);
        }

        if (subscriptions.Count == 0)
            throw new InvalidOperationException("Connector setting 'subscriptionIds' must contain at least one subscription ID.");

        return subscriptions;
    }

    public static string BuildRequestBody(string query, IReadOnlyCollection<string> subscriptionIds, string? skipToken)
    {
        var options = new Dictionary<string, object?>
        {
            ["resultFormat"] = "objectArray"
        };
        if (!string.IsNullOrWhiteSpace(skipToken)) options["$skipToken"] = skipToken;

        var body = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["options"] = options
        };
        if (subscriptionIds.Count > 0) body["subscriptions"] = subscriptionIds;
        return JsonSerializer.Serialize(body);
    }

    /// <summary>
    /// Returns the continuation token for an Azure Resource Graph page and rejects
    /// truncated responses that cannot be continued safely.
    /// </summary>
    public static string? GetNextPageToken(JsonElement response, ISet<string> observedSkipTokens)
    {
        var nextSkipToken = GetString(response, "$skipToken");
        if (GetBoolean(response, "resultTruncated") == true && string.IsNullOrWhiteSpace(nextSkipToken))
            throw new InvalidOperationException(
                "Azure Resource Graph returned a truncated result without a continuation token.");

        if (!string.IsNullOrWhiteSpace(nextSkipToken) && !observedSkipTokens.Add(nextSkipToken))
            throw new InvalidOperationException("Azure Resource Graph returned a repeated pagination token.");

        return nextSkipToken;
    }

    public static IReadOnlyList<AzureNicObservation> ParseNicObservations(IEnumerable<JsonElement> nicRows)
    {
        var observations = new List<AzureNicObservation>();
        foreach (var nic in nicRows)
        {
            var vmResourceId = GetString(nic, "vmResourceId");
            if (string.IsNullOrWhiteSpace(vmResourceId)) continue;

            var mac = GetString(nic, "mac");
            var nicIsPrimary = GetBoolean(nic, "isNicPrimary") == true;
            var configurations = GetIpConfigurations(nic);
            if (configurations is null || configurations.Count == 0)
            {
                // ARG's REST response has historically returned dynamic values in
                // different shapes. The scalar fields are deliberately projected by
                // the connector as a compatibility fallback for the primary config.
                var privateIp = GetString(nic, "primaryPrivateIp") ?? GetString(nic, "privateIp");
                var publicIpId = GetString(nic, "primaryPublicIpResourceId") ?? GetString(nic, "publicIpId");
                if (!string.IsNullOrWhiteSpace(privateIp) || !string.IsNullOrWhiteSpace(mac) ||
                    !string.IsNullOrWhiteSpace(publicIpId))
                    observations.Add(new AzureNicObservation(vmResourceId, mac, privateIp, nicIsPrimary, publicIpId));
                continue;
            }

            var foundConfiguration = false;
            var configurationIndex = 0;
            foreach (var configuration in configurations)
            {
                foundConfiguration = true;
                var privateIp = GetString(configuration, "properties", "privateIPAddress");
                var publicIpId = GetString(configuration, "properties", "publicIPAddress", "id");
                var primary = nicIsPrimary &&
                    (GetBoolean(configuration, "properties", "primary") ?? configurationIndex == 0);
                if (string.IsNullOrWhiteSpace(privateIp) && string.IsNullOrWhiteSpace(mac) && string.IsNullOrWhiteSpace(publicIpId))
                {
                    configurationIndex++;
                    continue;
                }

                observations.Add(new AzureNicObservation(vmResourceId, mac, privateIp, primary, publicIpId));
                configurationIndex++;
            }

            if (!foundConfiguration && !string.IsNullOrWhiteSpace(mac))
                observations.Add(new AzureNicObservation(vmResourceId, mac, null, nicIsPrimary, null));
        }

        return observations;
    }

    public static IReadOnlyDictionary<string, string> ParsePublicIpAddresses(IEnumerable<JsonElement> publicIpRows)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var publicIp in publicIpRows)
        {
            var id = GetString(publicIp, "id");
            var address = GetString(publicIp, "publicIp");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(address))
                values[id] = address;
        }
        return values;
    }

    public static AzureNicEnrichmentResult EnrichVmAssets(
        IReadOnlyDictionary<string, DiscoveredAsset> assets,
        IEnumerable<AzureNicObservation> observations,
        IReadOnlyDictionary<string, string> publicIpAddresses)
    {
        var matchedVms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var publicIpReferences = 0;
        var interfacesAdded = 0;
        var resolvedPublicIps = new Dictionary<DiscoveredAsset, HashSet<string>>();

        foreach (var observation in observations.OrderByDescending(observation => observation.IsPrimary))
        {
            if (!assets.TryGetValue(observation.VmResourceId, out var asset)) continue;
            matchedVms.Add(observation.VmResourceId);

            if (!string.IsNullOrWhiteSpace(observation.PrivateIpAddress) || !string.IsNullOrWhiteSpace(observation.MacAddress))
            {
                var existing = asset.Interfaces.FirstOrDefault(i =>
                    string.Equals(i.IpAddress, observation.PrivateIpAddress, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(i.MacAddress, observation.MacAddress, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    var hasPrimary = asset.Interfaces.Any(@interface => @interface.IsPrimary);
                    asset.Interfaces.Add(new DiscoveredInterface
                    {
                        IpAddress = observation.PrivateIpAddress,
                        MacAddress = observation.MacAddress,
                        IsPrimary = !hasPrimary && (observation.IsPrimary || asset.Interfaces.Count == 0)
                    });
                    interfacesAdded++;
                }
                else
                {
                    if (!asset.Interfaces.Any(@interface => @interface.IsPrimary))
                        existing.IsPrimary = observation.IsPrimary || asset.Interfaces.Count == 1;
                }
            }

            if (string.IsNullOrWhiteSpace(observation.PublicIpResourceId)) continue;
            publicIpReferences++;
            asset.Tags["public_ip"] = "true";
            asset.Tags["internet_facing"] = "true";
            if (!publicIpAddresses.TryGetValue(observation.PublicIpResourceId, out var publicIp)) continue;
            if (!resolvedPublicIps.TryGetValue(asset, out var addresses))
            {
                addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                resolvedPublicIps[asset] = addresses;
            }
            addresses.Add(publicIp);
        }

        foreach (var (asset, addresses) in resolvedPublicIps)
            asset.Tags["azure_public_ips"] = string.Join(',', addresses.Order(StringComparer.OrdinalIgnoreCase));

        return new AzureNicEnrichmentResult(matchedVms.Count, interfacesAdded, publicIpReferences);
    }

    private static IEnumerable<string> ParseSubscriptionArray(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Connector setting 'subscriptionIds' must be a comma-separated list or a JSON array.");

            return document.RootElement.EnumerateArray().Select(value =>
                value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : throw new InvalidOperationException("Each 'subscriptionIds' array item must be a string."))
                .ToArray();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Connector setting 'subscriptionIds' must be a valid JSON array.", ex);
        }
    }

    private static string? GetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool? GetBoolean(JsonElement element, params string[] path)
    {
        var value = GetString(element, path);
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static IReadOnlyList<JsonElement>? GetIpConfigurations(JsonElement nic)
    {
        if (!nic.TryGetProperty("ipConfigurations", out var configurations)) return null;

        if (configurations.ValueKind == JsonValueKind.Array)
            return configurations.EnumerateArray().Select(configuration => configuration.Clone()).ToArray();

        // Resource Graph's objectArray response can serialize a dynamic field as a
        // JSON string. Clone the values before disposing the temporary document.
        if (configurations.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(configurations.GetString()))
            return null;

        try
        {
            using var document = JsonDocument.Parse(configurations.GetString()!);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(configuration => configuration.Clone()).ToArray()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
