using Esar.Domain.Common;
using Esar.Domain.Enums;

namespace Esar.Domain.Entities;

/// <summary>Directed relationship between two assets (dependency/topology graph edge).</summary>
public class AssetRelationship : AuditableEntity
{
    public Guid SourceAssetId { get; set; }
    public Asset? SourceAsset { get; set; }
    public Guid TargetAssetId { get; set; }
    public Asset? TargetAsset { get; set; }
    public RelationshipType Type { get; set; }
    public string? Description { get; set; }
    /// <summary>Connector that discovered the relationship; ManualImport for operator-created edges.</summary>
    public ConnectorType Source { get; set; } = ConnectorType.ManualImport;
    public bool IsActive { get; set; } = true;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Security baseline policy: which controls a class of assets must satisfy.
/// Compliance is evaluated dynamically against enabled policies (no hardcoded logic).
/// </summary>
public class CompliancePolicy : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Lower value = evaluated first; the first matching policy wins per asset.</summary>
    public int Priority { get; set; } = 100;

    // --- Scope (null/empty = matches everything) ---
    /// <summary>jsonb array of AssetType names this policy applies to.</summary>
    public string AppliesToAssetTypesJson { get; set; } = "[]";
    /// <summary>jsonb array of EnvironmentType names; empty = all environments.</summary>
    public string AppliesToEnvironmentsJson { get; set; } = "[]";
    /// <summary>Minimum criticality for the policy to apply; null = any.</summary>
    public CriticalityLevel? MinCriticality { get; set; }
    /// <summary>jsonb array of ConnectorType names; empty = any source. Matches if any of the asset's sources is listed.</summary>
    public string AppliesToConnectorsJson { get; set; } = "[]";
    /// <summary>jsonb array of "key" or "key=value" tag filters; empty = no tag constraint. Matches if any entry matches any asset tag.</summary>
    public string AppliesToTagsJson { get; set; } = "[]";
    /// <summary>jsonb array of hostname glob patterns (*, ?); empty = any hostname. Matched against NormalizedHostname.</summary>
    public string AppliesToHostnamePatternsJson { get; set; } = "[]";
    /// <summary>jsonb array of CIDR ranges; empty = any IP. Matches if any asset IP falls in any listed range.</summary>
    public string AppliesToIpRangesJson { get; set; } = "[]";
    /// <summary>jsonb array of cloud subscription ids; empty = any subscription.</summary>
    public string AppliesToSubscriptionsJson { get; set; } = "[]";
    /// <summary>jsonb array of AssetGroup ids (as strings); empty = no group constraint. Matches if the
    /// asset belongs to any listed group.</summary>
    public string AppliesToGroupsJson { get; set; } = "[]";

    // --- Requirements ---
    /// <summary>jsonb array of ControlType names that are evaluated for matching assets.</summary>
    public string RequiredControlsJson { get; set; } = "[]";
    /// <summary>jsonb array of ControlType names whose failure makes the asset NonCompliant outright.</summary>
    public string MandatoryControlsJson { get; set; } = "[]";
    /// <summary>Incremented on every change — policy versioning.</summary>
    public int Version { get; set; } = 1;
}

/// <summary>Approval workflow item (new asset activation, merges, ownership changes).</summary>
public class ApprovalRequest : AuditableEntity
{
    public ApprovalType Type { get; set; }
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public Guid? AssetId { get; set; }
    public Asset? Asset { get; set; }
    /// <summary>Type-specific payload (jsonb) — e.g. duplicate id for merges, proposed metadata.</summary>
    public string PayloadJson { get; set; } = "{}";
    public string RequestedBy { get; set; } = "system";
    public string? Justification { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionComment { get; set; }
}
