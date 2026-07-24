using Esar.Domain.Common;
using Esar.Domain.Enums;

namespace Esar.Domain.Entities;

/// <summary>
/// Aggregate root: a single canonical (golden-record) enterprise asset.
/// </summary>
public class Asset : AuditableEntity
{
    public string Hostname { get; set; } = string.Empty;
    public string NormalizedHostname { get; set; } = string.Empty;
    public string? Fqdn { get; set; }
    public string? Domain { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OsVersion { get; set; }
    public AssetType AssetType { get; set; } = AssetType.Unknown;
    public AssetStatus Status { get; set; } = AssetStatus.Active;
    public LifecycleStatus LifecycleStatus { get; set; } = LifecycleStatus.Active;
    public EnvironmentType Environment { get; set; } = EnvironmentType.Unknown;
    public CriticalityLevel Criticality { get; set; } = CriticalityLevel.Unknown;

    public string? OwnerName { get; set; }
    public string? OwnerEmail { get; set; }
    public string? Department { get; set; }
    public string? BusinessUnit { get; set; }
    public string? Location { get; set; }
    public string? Classification { get; set; }

    // Hardware / identity
    public string? SerialNumber { get; set; }
    public string? BiosUuid { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }

    // Cloud metadata
    public string? CloudProvider { get; set; }
    public string? CloudResourceId { get; set; }
    public string? CloudRegion { get; set; }
    public string? CloudSubscriptionId { get; set; }
    public string? CloudAccountId { get; set; }

    // Health / compliance / quality summary (denormalized for fast dashboards)
    public int HealthScore { get; set; } = 100;
    public decimal ComplianceScore { get; set; }
    public ComplianceStatus ComplianceStatus { get; set; } = ComplianceStatus.Unknown;
    /// <summary>0–100 completeness/consistency score computed by the data quality engine.</summary>
    public decimal DataQualityScore { get; set; } = 100;
    /// <summary>Open data quality issues (jsonb array of issue codes with details).</summary>
    public string DataQualityIssuesJson { get; set; } = "[]";

    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public bool IsDeleted { get; set; }
    /// <summary>When this record was merged into another golden record.</summary>
    public Guid? MergedIntoAssetId { get; set; }
    /// <summary>When true, ComplianceEngine skips evaluation entirely (score/status reset to
    /// Unknown) — an explicit, visible exception, distinct from a policy simply not matching.</summary>
    public bool PolicyExempt { get; set; }

    /// <summary>
    /// Per-attribute provenance map (jsonb): attribute name -> connector that currently
    /// owns the value. Used by the source-priority engine during merges.
    /// </summary>
    public string AttributeSourcesJson { get; set; } = "{}";

    public ICollection<AssetSource> Sources { get; set; } = new List<AssetSource>();
    public ICollection<AssetIdentifier> Identifiers { get; set; } = new List<AssetIdentifier>();
    public ICollection<AssetIp> IpAddresses { get; set; } = new List<AssetIp>();
    public ICollection<AssetTag> Tags { get; set; } = new List<AssetTag>();
    public ICollection<AssetHistory> Histories { get; set; } = new List<AssetHistory>();
    public ICollection<AssetSoftware> Software { get; set; } = new List<AssetSoftware>();
    public ICollection<AssetCompliance> ComplianceRecords { get; set; } = new List<AssetCompliance>();
    public ICollection<AssetEvent> Events { get; set; } = new List<AssetEvent>();
    public AssetRisk? Risk { get; set; }
}
