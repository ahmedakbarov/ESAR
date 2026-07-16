using Esar.Domain.Common;
using Esar.Domain.Enums;
using MatchType = Esar.Domain.Enums.MatchType;

namespace Esar.Domain.Entities;

/// <summary>Configurable weighted matching rule used by the matching engine.</summary>
public class MatchingRule : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Identifier/attribute this rule evaluates (e.g. AzureResourceId, MacAddress, Hostname).</summary>
    public string Attribute { get; set; } = string.Empty;
    public MatchType MatchType { get; set; } = MatchType.Soft;
    /// <summary>Weight contributed to the confidence score when the attribute matches (0..1).</summary>
    public decimal Weight { get; set; }
    /// <summary>Evaluation order; hard rules with lower order win first.</summary>
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Records every matching decision (explainability + audit + manual review queue).
/// Decision == QueuedForReview items form the manual review queue.
/// </summary>
public class MatchRecord : BaseEntity
{
    public ConnectorType SourceConnector { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string? CandidateHostname { get; set; }
    /// <summary>The golden asset the candidate was matched to (null when a new asset was created).</summary>
    public Guid? MatchedAssetId { get; set; }
    public Asset? MatchedAsset { get; set; }
    /// <summary>Asset created for the candidate when no match was found.</summary>
    public Guid? CreatedAssetId { get; set; }
    public decimal ConfidenceScore { get; set; }
    public MatchType? MatchType { get; set; }
    public MatchDecision Decision { get; set; }
    /// <summary>Per-rule score breakdown (jsonb) — explainable matching.</summary>
    public string ExplanationJson { get; set; } = "[]";
    /// <summary>Candidate payload snapshot (jsonb) used for review/merge on approval.</summary>
    public string? CandidateJson { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewComment { get; set; }
}
