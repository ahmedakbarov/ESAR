using Esar.Domain.Entities;

namespace Esar.Application.Assets;

public class AssetDto
{
    public Guid Id { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string? Fqdn { get; set; }
    public string? Domain { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OsVersion { get; set; }
    public string AssetType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string LifecycleStatus { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string Criticality { get; set; } = string.Empty;
    public string? OwnerName { get; set; }
    public string? Department { get; set; }
    public string? BusinessUnit { get; set; }
    public string? Location { get; set; }
    public string? Classification { get; set; }
    public string? CloudProvider { get; set; }
    public string? CloudRegion { get; set; }
    public decimal ComplianceScore { get; set; }
    public string ComplianceStatus { get; set; } = string.Empty;
    public bool PolicyExempt { get; set; }
    public int HealthScore { get; set; }
    public decimal DataQualityScore { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public string? PrimaryIp { get; set; }
    public List<string> Sources { get; set; } = new();

    public static AssetDto From(Asset a) => new()
    {
        Id = a.Id,
        Hostname = a.Hostname,
        Fqdn = a.Fqdn,
        Domain = a.Domain,
        OperatingSystem = a.OperatingSystem,
        OsVersion = a.OsVersion,
        AssetType = a.AssetType.ToString(),
        Status = a.Status.ToString(),
        LifecycleStatus = a.LifecycleStatus.ToString(),
        Environment = a.Environment.ToString(),
        Criticality = a.Criticality.ToString(),
        OwnerName = a.OwnerName,
        Department = a.Department,
        BusinessUnit = a.BusinessUnit,
        Location = a.Location,
        Classification = a.Classification,
        CloudProvider = a.CloudProvider,
        CloudRegion = a.CloudRegion,
        ComplianceScore = a.ComplianceScore,
        ComplianceStatus = a.ComplianceStatus.ToString(),
        PolicyExempt = a.PolicyExempt,
        HealthScore = a.HealthScore,
        DataQualityScore = a.DataQualityScore,
        FirstSeen = a.FirstSeen,
        LastSeen = a.LastSeen,
        PrimaryIp = a.IpAddresses.FirstOrDefault(i => i.IsActive && i.IsPrimary)?.IpAddress
                    ?? a.IpAddresses.FirstOrDefault(i => i.IsActive)?.IpAddress,
        Sources = a.Sources.Select(s => s.ConnectorType.ToString()).Distinct().ToList()
    };
}

public class AssetDetailDto : AssetDto
{
    public string? SerialNumber { get; set; }
    public string? BiosUuid { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? CloudResourceId { get; set; }
    public string? CloudSubscriptionId { get; set; }
    public string? OwnerEmail { get; set; }
    public List<AssetIpDto> IpAddresses { get; set; } = new();
    public List<AssetSourceDto> SourceDetails { get; set; } = new();
    public List<AssetTagDto> Tags { get; set; } = new();
    public List<AssetComplianceDto> Compliance { get; set; } = new();
    public List<AssetSoftwareDto> Software { get; set; } = new();
    public AssetRiskDto? Risk { get; set; }
    public string DataQualityIssuesJson { get; set; } = "[]";

    public static AssetDetailDto FromDetailed(Asset a)
    {
        var dto = new AssetDetailDto();
        var baseDto = From(a);
        foreach (var prop in typeof(AssetDto).GetProperties().Where(p => p.CanWrite))
            prop.SetValue(dto, prop.GetValue(baseDto));

        dto.SerialNumber = a.SerialNumber;
        dto.BiosUuid = a.BiosUuid;
        dto.Manufacturer = a.Manufacturer;
        dto.Model = a.Model;
        dto.CloudResourceId = a.CloudResourceId;
        dto.CloudSubscriptionId = a.CloudSubscriptionId;
        dto.OwnerEmail = a.OwnerEmail;
        dto.IpAddresses = a.IpAddresses.Where(i => i.IsActive)
            .Select(i => new AssetIpDto(i.IpAddress, i.MacAddress, i.IsPrimary,
            i.Source.ToString(), i.LastSeen)).ToList();
        dto.SourceDetails = a.Sources.Select(s => new AssetSourceDto(s.ConnectorType.ToString(), s.ExternalId,
            s.FirstSeen, s.LastSeen)).ToList();
        dto.Tags = a.Tags.Select(t => new AssetTagDto(t.Key, t.Value, t.Source.ToString())).ToList();
        dto.Compliance = a.ComplianceRecords.Select(c => new AssetComplianceDto(c.Control.ToString(),
            c.Status.ToString(), c.Details, c.EvidenceSource?.ToString(), c.CheckedAt)).ToList();
        dto.Software = a.Software.Select(s => new AssetSoftwareDto(s.Name, s.Version, s.Vendor,
            s.Source.ToString(), s.LastSeen)).ToList();
        dto.Risk = a.Risk is null ? null : new AssetRiskDto(a.Risk.RiskScore, a.Risk.VulnerabilitiesCritical,
            a.Risk.VulnerabilitiesHigh, a.Risk.VulnerabilitiesMedium, a.Risk.VulnerabilitiesLow, a.Risk.LastCalculatedAt);
        dto.DataQualityIssuesJson = a.DataQualityIssuesJson;
        return dto;
    }
}

public record AssetIpDto(string IpAddress, string? MacAddress, bool IsPrimary, string Source, DateTime LastSeen);
public record AssetSourceDto(string Connector, string ExternalId, DateTime FirstSeen, DateTime LastSeen);
public record AssetTagDto(string Key, string Value, string Source);
public record AssetComplianceDto(string Control, string Status, string? Details, string? EvidenceSource, DateTime CheckedAt);
public record AssetSoftwareDto(string Name, string? Version, string? Vendor, string Source, DateTime LastSeen);
public record AssetRiskDto(decimal RiskScore, int Critical, int High, int Medium, int Low, DateTime LastCalculatedAt);
public record AssetHistoryDto(string FieldName, string? OldValue, string? NewValue, string ChangedBy,
    string? Source, DateTime ChangedAt);
