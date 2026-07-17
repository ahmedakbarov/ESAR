using Esar.Domain.Enums;

namespace Esar.Application.Contracts;

/// <summary>
/// Canonical, source-agnostic representation of an asset emitted by every connector.
/// This is the single contract between the discovery layer and the ingestion pipeline.
/// </summary>
public class DiscoveredAsset
{
    public ConnectorType Source { get; set; }
    /// <summary>Unique id of the asset inside the source system (required).</summary>
    public string ExternalId { get; set; } = string.Empty;

    public string? Hostname { get; set; }
    public string? Fqdn { get; set; }
    public string? Domain { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OsVersion { get; set; }
    public AssetType? AssetType { get; set; }

    public List<DiscoveredInterface> Interfaces { get; set; } = new();

    public string? SerialNumber { get; set; }
    public string? BiosUuid { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }

    public string? CloudProvider { get; set; }
    public string? CloudResourceId { get; set; }
    public string? CloudRegion { get; set; }
    public string? CloudSubscriptionId { get; set; }
    public string? CloudAccountId { get; set; }

    public string? OwnerName { get; set; }
    public string? OwnerEmail { get; set; }
    public string? Department { get; set; }
    public string? BusinessUnit { get; set; }
    public string? Location { get; set; }
    public string? Classification { get; set; }
    public EnvironmentType? Environment { get; set; }
    public CriticalityLevel? Criticality { get; set; }

    /// <summary>
    /// Matching identifiers keyed by <see cref="MatchAttributes"/> names
    /// (AzureResourceId, AwsInstanceId, VmwareUuid, ObjectGuid, EndpointId, ...).
    /// </summary>
    public Dictionary<string, string> Identifiers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Free-form normalized attributes (patch_status, disk_encryption, monitoring_agent, ...).</summary>
    public Dictionary<string, string> Attributes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DiscoveredSoftware> Software { get; set; } = new();

    /// <summary>Raw source payload preserved for enrichment/forensics.</summary>
    public string? RawJson { get; set; }
    public DateTime SeenAt { get; set; } = DateTime.UtcNow;
}

public class DiscoveredInterface
{
    public string? IpAddress { get; set; }
    public string? MacAddress { get; set; }
    public bool IsPrimary { get; set; }
}

public class DiscoveredSoftware
{
    public string Name { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Vendor { get; set; }
}

/// <summary>Well-known matching identifier names, in default hard-match priority order.</summary>
public static class MatchAttributes
{
    public const string AzureResourceId = "AzureResourceId";
    public const string AwsInstanceId = "AwsInstanceId";
    public const string VmwareUuid = "VmwareUuid";
    public const string BiosUuid = "BiosUuid";
    public const string SerialNumber = "SerialNumber";
    public const string ObjectGuid = "ObjectGuid";
    public const string EndpointId = "EndpointId";
    public const string MacAddress = "MacAddress";
    public const string Hostname = "Hostname";
    public const string IpAddress = "IpAddress";
    public const string OperatingSystem = "OperatingSystem";
    public const string Domain = "Domain";
}
