using Esar.Application.Abstractions;
using Esar.Application.Matching;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Esar.Application.Compliance;

public interface IComplianceEngine
{
    /// <summary>Evaluates the asset against its applicable security-baseline policy and persists results.</summary>
    Task<ComplianceStatus> EvaluateAsync(Asset asset, CancellationToken ct = default);
}

/// <summary>
/// Policy-driven compliance evaluation. The set of controls comes from the
/// <see cref="IPolicyEngine"/> (configurable per asset type/environment/criticality);
/// this engine only knows HOW to evaluate each control, never WHICH apply.
/// Remediation workflow states are preserved across evaluations.
/// </summary>
public class ComplianceEngine : IComplianceEngine
{
    private static readonly ConnectorType[] SiemSources =
        { ConnectorType.MicrosoftSentinel, ConnectorType.QRadar, ConnectorType.Splunk, ConnectorType.Elastic };
    private static readonly ConnectorType[] EdrSources =
        { ConnectorType.MicrosoftDefender, ConnectorType.CortexXdr, ConnectorType.CrowdStrike, ConnectorType.SentinelOne };
    private static readonly ConnectorType[] VulnSources =
        { ConnectorType.Qualys, ConnectorType.Rapid7, ConnectorType.Tenable, ConnectorType.Nessus };

    private readonly IUnitOfWork _uow;
    private readonly IPolicyEngine _policies;
    private readonly IEventBus _events;
    private readonly ILogger<ComplianceEngine> _logger;

    public ComplianceEngine(IUnitOfWork uow, IPolicyEngine policies, IEventBus events,
        ILogger<ComplianceEngine> logger)
    {
        _uow = uow;
        _policies = policies;
        _events = events;
        _logger = logger;
    }

    public async Task<ComplianceStatus> EvaluateAsync(Asset asset, CancellationToken ct = default)
    {
        var plan = await _policies.GetPlanAsync(asset, ct);
        var maxAgeDays = await GetEvidenceMaxAgeDaysAsync(ct);
        var evidenceCutoff = DateTime.UtcNow.AddDays(-maxAgeDays);

        var results = plan.RequiredControls
            .Select(control => EvaluateControl(asset, control, evidenceCutoff))
            .ToList();

        // Drop records for controls the current policy no longer requires.
        foreach (var stale in asset.ComplianceRecords
                     .Where(c => plan.RequiredControls.All(rc => rc != c.Control)).ToList())
            asset.ComplianceRecords.Remove(stale);

        foreach (var (control, status, details, evidence) in results)
        {
            var record = asset.ComplianceRecords.FirstOrDefault(c => c.Control == control);
            if (record is null)
            {
                record = new AssetCompliance { AssetId = asset.Id, Control = control };
                asset.ComplianceRecords.Add(record);
            }
            record.Status = status;
            record.Details = details;
            record.EvidenceSource = evidence;
            record.CheckedAt = DateTime.UtcNow;
            record.PolicyId = plan.PolicyId;
            record.RemediationState = NextRemediationState(record.RemediationState, control, status);
        }

        // RiskAccepted controls count as satisfied for scoring/status (documented exception).
        bool Satisfied(AssetCompliance c) =>
            c.Status == ComplianceStatus.Compliant || c.RemediationState == RemediationState.RiskAccepted;

        var evaluated = asset.ComplianceRecords.Where(c => c.Status != ComplianceStatus.Unknown).ToList();
        asset.ComplianceScore = evaluated.Count == 0
            ? 0
            : Math.Round(100m * evaluated.Count(Satisfied) / evaluated.Count, 2);

        var mandatoryFailed = asset.ComplianceRecords.Any(c =>
            plan.MandatoryControls.Contains(c.Control) &&
            c.Status == ComplianceStatus.NonCompliant &&
            c.RemediationState != RemediationState.RiskAccepted);
        var anyFailed = evaluated.Any(c => c.Status == ComplianceStatus.NonCompliant &&
                                           c.RemediationState != RemediationState.RiskAccepted);
        var anyPending = evaluated.Any(c => c.Status == ComplianceStatus.Pending);

        asset.ComplianceStatus =
            evaluated.Count == 0 ? ComplianceStatus.Unknown
            : mandatoryFailed || anyFailed ? ComplianceStatus.NonCompliant
            : anyPending ? ComplianceStatus.Pending
            : ComplianceStatus.Compliant;

        _uow.Assets.Update(asset);

        var payload = new
        {
            AssetId = asset.Id,
            asset.Hostname,
            Policy = plan.PolicyName,
            Status = asset.ComplianceStatus.ToString(),
            Score = asset.ComplianceScore,
            Criticality = asset.Criticality.ToString(),
            FailedControls = asset.ComplianceRecords
                .Where(c => c.Status == ComplianceStatus.NonCompliant)
                .Select(c => c.Control.ToString()).ToArray()
        };
        await _events.PublishAsync(EventTopics.ComplianceEvaluated, payload, ct);
        if (asset.ComplianceStatus == ComplianceStatus.NonCompliant)
        {
            await _events.PublishAsync(EventTopics.ComplianceFailed, payload, ct);
            if (mandatoryFailed)
                await _events.PublishAsync(EventTopics.PolicyViolation, payload, ct);
        }

        return asset.ComplianceStatus;
    }

    /// <summary>Auto-transitions of the remediation workflow; manual states are preserved.</summary>
    private static RemediationState NextRemediationState(RemediationState current, ControlType control,
        ComplianceStatus status)
    {
        if (status == ComplianceStatus.Compliant) return RemediationState.FullyCompliant;
        if (current == RemediationState.RiskAccepted) return current; // sticky manual exception
        if (status != ComplianceStatus.NonCompliant) return current;

        // Operator already progressed the workflow — keep their state.
        if (current is not (RemediationState.None or RemediationState.FullyCompliant)) return current;

        return control switch
        {
            ControlType.SiemLogSource => RemediationState.WaitingSiemOnboarding,
            ControlType.Edr => RemediationState.WaitingEdrInstallation,
            ControlType.Antivirus or ControlType.MonitoringAgent or ControlType.BackupAgent
                => RemediationState.WaitingAgentInstallation,
            ControlType.AssetClassification => RemediationState.WaitingOwnerApproval,
            _ => RemediationState.PendingReview
        };
    }

    private (ControlType Control, ComplianceStatus Status, string Details, ConnectorType? Evidence)
        EvaluateControl(Asset asset, ControlType control, DateTime evidenceCutoff) => control switch
    {
        ControlType.SiemLogSource => EvaluateSourceControl(asset, control, SiemSources, evidenceCutoff),
        ControlType.Edr => EvaluateSourceControl(asset, control, EdrSources, evidenceCutoff),
        ControlType.Antivirus => EvaluateAttributeControl(asset, control, "antivirus", EdrSources, evidenceCutoff),
        ControlType.VulnerabilityScanner => EvaluateSourceControl(asset, control, VulnSources, evidenceCutoff),
        ControlType.MonitoringAgent => EvaluateAttributeControl(asset, control, "monitoring_agent", null, evidenceCutoff),
        ControlType.BackupAgent => EvaluateAttributeControl(asset, control, "backup_agent", null, evidenceCutoff),
        ControlType.PatchStatus => EvaluatePatchStatus(asset),
        ControlType.DiskEncryption => EvaluateAttributeControl(asset, control, "disk_encryption", null, evidenceCutoff),
        ControlType.AssetClassification => (control,
            string.IsNullOrWhiteSpace(asset.Classification) ? ComplianceStatus.NonCompliant : ComplianceStatus.Compliant,
            string.IsNullOrWhiteSpace(asset.Classification)
                ? "Asset has no classification"
                : $"Classified as {asset.Classification}",
            null),
        _ => (control, ComplianceStatus.Unknown, "No evaluator for control", null)
    };

    private static (ControlType, ComplianceStatus, string, ConnectorType?) EvaluateSourceControl(
        Asset asset, ControlType control, ConnectorType[] validSources, DateTime evidenceCutoff)
    {
        var evidence = asset.Sources
            .Where(s => validSources.Contains(s.ConnectorType))
            .OrderByDescending(s => s.LastSeen)
            .FirstOrDefault();

        if (evidence is null)
            return (control, ComplianceStatus.NonCompliant, $"No {control} source reports this asset", null);
        if (evidence.LastSeen < evidenceCutoff)
            return (control, ComplianceStatus.NonCompliant,
                $"{evidence.ConnectorType} evidence is stale (last seen {evidence.LastSeen:yyyy-MM-dd})",
                evidence.ConnectorType);
        return (control, ComplianceStatus.Compliant, $"Covered by {evidence.ConnectorType}", evidence.ConnectorType);
    }

    private static (ControlType, ComplianceStatus, string, ConnectorType?) EvaluateAttributeControl(
        Asset asset, ControlType control, string tagKey, ConnectorType[]? fallbackSources, DateTime evidenceCutoff)
    {
        var tag = asset.Tags.FirstOrDefault(t => t.Key.Equals(tagKey, StringComparison.OrdinalIgnoreCase));
        if (tag is not null)
        {
            var enabled = tag.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                          tag.Value.Equals("enabled", StringComparison.OrdinalIgnoreCase) ||
                          tag.Value.Equals("installed", StringComparison.OrdinalIgnoreCase);
            return (control, enabled ? ComplianceStatus.Compliant : ComplianceStatus.NonCompliant,
                $"Attribute {tagKey}={tag.Value} (source {tag.Source})", tag.Source);
        }

        if (fallbackSources is not null)
        {
            var evidence = asset.Sources
                .Where(s => fallbackSources.Contains(s.ConnectorType) && s.LastSeen >= evidenceCutoff)
                .OrderByDescending(s => s.LastSeen).FirstOrDefault();
            if (evidence is not null)
                return (control, ComplianceStatus.Compliant, $"Implied by {evidence.ConnectorType}",
                    evidence.ConnectorType);
        }

        return (control, ComplianceStatus.Unknown, $"No evidence for {tagKey}", null);
    }

    private static (ControlType, ComplianceStatus, string, ConnectorType?) EvaluatePatchStatus(Asset asset)
    {
        var tag = asset.Tags.FirstOrDefault(t => t.Key.Equals("patch_status", StringComparison.OrdinalIgnoreCase));
        if (tag is null)
            return (ControlType.PatchStatus, ComplianceStatus.Unknown, "No patch telemetry", null);
        var ok = tag.Value.Equals("up_to_date", StringComparison.OrdinalIgnoreCase) ||
                 tag.Value.Equals("compliant", StringComparison.OrdinalIgnoreCase);
        return (ControlType.PatchStatus, ok ? ComplianceStatus.Compliant : ComplianceStatus.NonCompliant,
            $"patch_status={tag.Value} (source {tag.Source})", tag.Source);
    }

    private async Task<int> GetEvidenceMaxAgeDaysAsync(CancellationToken ct)
    {
        var setting = await _uow.Settings.FirstOrDefaultAsync(
            s => s.Key == SettingKeys.ComplianceEvidenceMaxAgeDays, ct);
        return setting is not null && int.TryParse(setting.Value, out var days) ? days : 7;
    }
}
