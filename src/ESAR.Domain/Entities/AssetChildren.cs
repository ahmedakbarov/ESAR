using Esar.Domain.Common;
using Esar.Domain.Enums;

namespace Esar.Domain.Entities;

/// <summary>Link between a golden asset record and one originating data source.</summary>
public class AssetSource : BaseEntity
{
    public Guid AssetId { get; set; }
    public Asset? Asset { get; set; }
    public ConnectorType ConnectorType { get; set; }
    /// <summary>Unique identifier of the asset inside the source system.</summary>
    public string ExternalId { get; set; } = string.Empty;
    public string? SourceHostname { get; set; }
    /// <summary>Raw source payload (jsonb) kept for enrichment and audit.</summary>
    public string? RawData { get; set; }
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public bool IsAuthoritative { get; set; }
}

public class AssetIp : BaseEntity
{
    public Guid AssetId { get; set; }
    public Asset? Asset { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string? MacAddress { get; set; }
    public string? Network { get; set; }
    public bool IsPrimary { get; set; }
    public ConnectorType Source { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}

public class AssetTag : BaseEntity
{
    public Guid AssetId { get; set; }
    public Asset? Asset { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public ConnectorType Source { get; set; }
}

/// <summary>Field-level change history of an asset (who/what/when/old/new).</summary>
public class AssetHistory : BaseEntity
{
    public Guid AssetId { get; set; }
    public Asset? Asset { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string ChangedBy { get; set; } = "system";
    public ConnectorType? Source { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}

public class AssetSoftware : BaseEntity
{
    public Guid AssetId { get; set; }
    public Asset? Asset { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Vendor { get; set; }
    public DateTime? InstallDate { get; set; }
    public ConnectorType Source { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}

/// <summary>Result of one compliance control evaluation for an asset.</summary>
public class AssetCompliance : BaseEntity
{
    public Guid AssetId { get; set; }
    public Asset? Asset { get; set; }
    public ControlType Control { get; set; }
    public ComplianceStatus Status { get; set; } = ComplianceStatus.Unknown;
    public string? Details { get; set; }
    public ConnectorType? EvidenceSource { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}

public class AssetEvent : BaseEntity
{
    public Guid AssetId { get; set; }
    public Asset? Asset { get; set; }
    public string EventType { get; set; } = string.Empty;
    /// <summary>Event payload (jsonb).</summary>
    public string? Payload { get; set; }
    public ConnectorType? Source { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

public class AssetRisk : BaseEntity
{
    public Guid AssetId { get; set; }
    public Asset? Asset { get; set; }
    public decimal RiskScore { get; set; }
    public int VulnerabilitiesCritical { get; set; }
    public int VulnerabilitiesHigh { get; set; }
    public int VulnerabilitiesMedium { get; set; }
    public int VulnerabilitiesLow { get; set; }
    public decimal ExposureScore { get; set; }
    public DateTime LastCalculatedAt { get; set; } = DateTime.UtcNow;
}
