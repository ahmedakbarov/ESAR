using System.Text.Json;
using System.Text.RegularExpressions;
using Esar.Domain.Entities;
using Esar.Domain.Enums;

namespace Esar.Application.Scoring;

// ============================================================================
// Data Quality Engine — completeness/consistency validation with a 0–100 score.
// ============================================================================

public record DataQualityIssue(string Code, string Message, int Penalty);

public interface IDataQualityEngine
{
    /// <summary>Evaluates one asset and writes DataQualityScore/DataQualityIssuesJson onto it.</summary>
    IReadOnlyList<DataQualityIssue> Evaluate(Asset asset);
}

public class DataQualityEngine : IDataQualityEngine
{
    private static readonly Regex ValidHostname =
        new(@"^[a-z0-9]([a-z0-9\-\.]{0,252}[a-z0-9])?$", RegexOptions.Compiled);

    public IReadOnlyList<DataQualityIssue> Evaluate(Asset asset)
    {
        var issues = new List<DataQualityIssue>();
        void Add(string code, string message, int penalty) => issues.Add(new DataQualityIssue(code, message, penalty));

        if (string.IsNullOrWhiteSpace(asset.OwnerName))
            Add("MISSING_OWNER", "Asset has no owner assigned", 12);
        if (string.IsNullOrWhiteSpace(asset.BusinessUnit))
            Add("MISSING_BUSINESS_UNIT", "Asset has no business unit", 10);
        if (asset.Criticality == CriticalityLevel.Unknown)
            Add("MISSING_CRITICALITY", "Asset criticality is not defined", 12);
        if (asset.Environment == EnvironmentType.Unknown)
            Add("MISSING_ENVIRONMENT", "Asset environment is not defined", 8);
        if (string.IsNullOrWhiteSpace(asset.Classification))
            Add("MISSING_CLASSIFICATION", "Asset has no data classification", 8);
        if (string.IsNullOrWhiteSpace(asset.OperatingSystem))
            Add("MISSING_OS", "Operating system is unknown", 8);
        if (asset.AssetType == AssetType.Unknown)
            Add("MISSING_ASSET_TYPE", "Asset type could not be determined", 8);
        if (!ValidHostname.IsMatch(asset.NormalizedHostname))
            Add("INVALID_HOSTNAME", $"Hostname '{asset.Hostname}' violates naming rules", 10);
        if (asset.IpAddresses.Count == 0)
            Add("NO_IP_ADDRESS", "No IP address is known for this asset", 8);
        if (asset.LastSeen < DateTime.UtcNow.AddDays(-30))
            Add("STALE_TELEMETRY", $"No source has seen this asset since {asset.LastSeen:yyyy-MM-dd}", 12);
        if (asset.Sources.Count <= 1)
            Add("SINGLE_SOURCE", "Asset is reported by a single source only", 5);

        // Conflicting hostname reports between sources indicate a bad merge or DNS drift.
        var distinctReported = asset.Sources
            .Where(s => !string.IsNullOrWhiteSpace(s.SourceHostname))
            .Select(s => s.SourceHostname!.Split('.')[0].ToLowerInvariant())
            .Distinct()
            .ToList();
        if (distinctReported.Count > 1)
            Add("CONFLICTING_HOSTNAME", $"Sources disagree on hostname: {string.Join(", ", distinctReported)}", 10);

        asset.DataQualityScore = Math.Max(0, 100 - issues.Sum(i => i.Penalty));
        asset.DataQualityIssuesJson = JsonSerializer.Serialize(issues);
        return issues;
    }
}

// ============================================================================
// Asset Health Engine — operational/security health at a glance.
// ============================================================================

public interface IAssetHealthEngine
{
    /// <summary>Computes and writes HealthScore (0–100) onto the asset.</summary>
    int Evaluate(Asset asset);
}

public class AssetHealthEngine : IAssetHealthEngine
{
    private static readonly ConnectorType[] EdrSources =
        { ConnectorType.MicrosoftDefender, ConnectorType.CortexXdr, ConnectorType.CrowdStrike, ConnectorType.SentinelOne };

    public int Evaluate(Asset asset)
    {
        var score = 100;

        // Recent activity — the strongest signal.
        if (asset.LastSeen < DateTime.UtcNow.AddDays(-30)) score -= 60;
        else if (asset.LastSeen < DateTime.UtcNow.AddDays(-7)) score -= 30;

        // Endpoint protection present and fresh.
        var edr = asset.Sources.Where(s => EdrSources.Contains(s.ConnectorType))
            .OrderByDescending(s => s.LastSeen).FirstOrDefault();
        if (edr is null) score -= 20;
        else if (edr.LastSeen < DateTime.UtcNow.AddDays(-7)) score -= 10;

        // Monitoring / backup coverage.
        if (!HasPositiveTag(asset, "monitoring_agent")) score -= 10;
        if (!HasPositiveTag(asset, "backup_agent")) score -= 10;

        // Compliance posture.
        score -= asset.ComplianceStatus switch
        {
            ComplianceStatus.NonCompliant => 15,
            ComplianceStatus.Pending or ComplianceStatus.Unknown => 5,
            _ => 0
        };

        // Correlation confidence: single-source assets are less trustworthy.
        if (asset.Sources.Count <= 1) score -= 5;

        asset.HealthScore = Math.Clamp(score, 0, 100);
        return asset.HealthScore;
    }

    private static bool HasPositiveTag(Asset asset, string key)
    {
        var tag = asset.Tags.FirstOrDefault(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return tag is not null &&
               (tag.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                tag.Value.Equals("enabled", StringComparison.OrdinalIgnoreCase) ||
                tag.Value.Equals("installed", StringComparison.OrdinalIgnoreCase));
    }
}

// ============================================================================
// Risk Scoring Engine — dynamic risk from criticality, exposure, vulnerabilities,
// compliance and sensitivity. Never stored statically; recalculated on schedule.
// ============================================================================

public interface IRiskScoringEngine
{
    /// <summary>Computes and writes AssetRisk.RiskScore / ExposureScore onto the asset.</summary>
    decimal Evaluate(Asset asset);
}

public class RiskScoringEngine : IRiskScoringEngine
{
    public decimal Evaluate(Asset asset)
    {
        decimal score = asset.Criticality switch
        {
            CriticalityLevel.Critical => 40,
            CriticalityLevel.High => 30,
            CriticalityLevel.Medium => 20,
            CriticalityLevel.Low => 10,
            _ => 15 // unknown criticality is itself a risk
        };

        // Vulnerability pressure (from scanner enrichment), capped.
        var risk = asset.Risk;
        if (risk is not null)
        {
            score += Math.Min(30,
                risk.VulnerabilitiesCritical * 5 +
                risk.VulnerabilitiesHigh * 2 +
                risk.VulnerabilitiesMedium * 0.5m);
        }

        // Compliance posture.
        score += asset.ComplianceStatus switch
        {
            ComplianceStatus.NonCompliant => 15,
            ComplianceStatus.Pending or ComplianceStatus.Unknown => 5,
            _ => 0
        };

        // Exposure.
        decimal exposure = 0;
        if (HasTag(asset, "internet_facing") || HasTag(asset, "public")) exposure += 15;
        if (asset.Environment == EnvironmentType.Production) exposure += 5;
        if (asset.CloudProvider is not null && HasTag(asset, "public_ip")) exposure += 5;
        score += exposure;

        // Data sensitivity.
        var classification = asset.Classification?.ToLowerInvariant() ?? string.Empty;
        if (classification.Contains("confidential") || classification.Contains("secret") ||
            classification.Contains("restricted"))
            score += 5;

        score = Math.Clamp(Math.Round(score, 2), 0, 100);

        asset.Risk ??= new AssetRisk { AssetId = asset.Id };
        asset.Risk.RiskScore = score;
        asset.Risk.ExposureScore = exposure;
        asset.Risk.LastCalculatedAt = DateTime.UtcNow;
        return score;
    }

    private static bool HasTag(Asset asset, string key)
    {
        var tag = asset.Tags.FirstOrDefault(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return tag is not null && tag.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
