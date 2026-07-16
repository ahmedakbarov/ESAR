using Esar.Application.Abstractions;
using Esar.Application.Matching;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Esar.Application.Compliance;

public interface IComplianceEngine
{
    /// <summary>Evaluates all security controls for an asset and persists the results.</summary>
    Task<ComplianceStatus> EvaluateAsync(Asset asset, CancellationToken ct = default);
}

public class ComplianceEngine : IComplianceEngine
{
    private static readonly ConnectorType[] SiemSources =
        { ConnectorType.MicrosoftSentinel, ConnectorType.QRadar, ConnectorType.Splunk, ConnectorType.Elastic };
    private static readonly ConnectorType[] EdrSources =
        { ConnectorType.MicrosoftDefender, ConnectorType.CortexXdr, ConnectorType.CrowdStrike, ConnectorType.SentinelOne };
    private static readonly ConnectorType[] VulnSources =
        { ConnectorType.Qualys, ConnectorType.Rapid7, ConnectorType.Tenable, ConnectorType.Nessus };

    /// <summary>Controls a Critical asset must satisfy before it is considered compliant at all.</summary>
    private static readonly ControlType[] MandatoryControls =
        { ControlType.SiemLogSource, ControlType.Edr, ControlType.VulnerabilityScanner };

    private readonly IUnitOfWork _uow;
    private readonly IEventBus _events;
    private readonly ILogger<ComplianceEngine> _logger;

    public ComplianceEngine(IUnitOfWork uow, IEventBus events, ILogger<ComplianceEngine> logger)
    {
        _uow = uow;
        _events = events;
        _logger = logger;
    }

    public async Task<ComplianceStatus> EvaluateAsync(Asset asset, CancellationToken ct = default)
    {
        var maxAgeDays = await GetEvidenceMaxAgeDaysAsync(ct);
        var evidenceCutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
        var results = new List<(ControlType Control, ComplianceStatus Status, string Details, ConnectorType? Evidence)>
        {
            EvaluateSourceControl(asset, ControlType.SiemLogSource, SiemSources, evidenceCutoff),
            EvaluateSourceControl(asset, ControlType.Edr, EdrSources, evidenceCutoff),
            EvaluateAttributeControl(asset, ControlType.Antivirus, "antivirus", fallbackSources: EdrSources, evidenceCutoff),
            EvaluateSourceControl(asset, ControlType.VulnerabilityScanner, VulnSources, evidenceCutoff),
            EvaluateAttributeControl(asset, ControlType.MonitoringAgent, "monitoring_agent", null, evidenceCutoff),
            EvaluateAttributeControl(asset, ControlType.BackupAgent, "backup_agent", null, evidenceCutoff),
            EvaluatePatchStatus(asset),
            EvaluateAttributeControl(asset, ControlType.DiskEncryption, "disk_encryption", null, evidenceCutoff),
            (ControlType.AssetClassification,
                string.IsNullOrWhiteSpace(asset.Classification) ? ComplianceStatus.NonCompliant : ComplianceStatus.Compliant,
                string.IsNullOrWhiteSpace(asset.Classification) ? "Asset has no classification" : $"Classified as {asset.Classification}",
                null)
        };

        foreach (var (control, status, details, evidence) in results)
        {
            var existing = asset.ComplianceRecords.FirstOrDefault(c => c.Control == control);
            if (existing is null)
            {
                asset.ComplianceRecords.Add(new AssetCompliance
                {
                    AssetId = asset.Id, Control = control, Status = status,
                    Details = details, EvidenceSource = evidence, CheckedAt = DateTime.UtcNow
                });
            }
            else
            {
                existing.Status = status;
                existing.Details = details;
                existing.EvidenceSource = evidence;
                existing.CheckedAt = DateTime.UtcNow;
            }
        }

        var evaluated = results.Where(r => r.Status != ComplianceStatus.Unknown).ToList();
        var compliant = evaluated.Count(r => r.Status == ComplianceStatus.Compliant);
        asset.ComplianceScore = evaluated.Count == 0 ? 0 : Math.Round(100m * compliant / evaluated.Count, 2);

        var mandatoryFailed = results.Any(r =>
            MandatoryControls.Contains(r.Control) && r.Status == ComplianceStatus.NonCompliant);
        asset.ComplianceStatus =
            evaluated.Count == 0 ? ComplianceStatus.Unknown
            : mandatoryFailed || asset.ComplianceScore < 100m && evaluated.Any(r => r.Status == ComplianceStatus.NonCompliant)
                ? ComplianceStatus.NonCompliant
                : evaluated.Any(r => r.Status == ComplianceStatus.Pending) ? ComplianceStatus.Pending
                : ComplianceStatus.Compliant;

        _uow.Assets.Update(asset);
        await _events.PublishAsync(EventTopics.ComplianceEvaluated, new
        {
            AssetId = asset.Id,
            asset.Hostname,
            Status = asset.ComplianceStatus.ToString(),
            Score = asset.ComplianceScore,
            Criticality = asset.Criticality.ToString(),
            MissingSiem = results.First(r => r.Control == ControlType.SiemLogSource).Status == ComplianceStatus.NonCompliant,
            MissingEdr = results.First(r => r.Control == ControlType.Edr).Status == ComplianceStatus.NonCompliant,
            MissingVulnScanner = results.First(r => r.Control == ControlType.VulnerabilityScanner).Status == ComplianceStatus.NonCompliant
        }, ct);

        return asset.ComplianceStatus;
    }

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
                $"{evidence.ConnectorType} evidence is stale (last seen {evidence.LastSeen:yyyy-MM-dd})", evidence.ConnectorType);
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
            var evidence = asset.Sources.Where(s => fallbackSources.Contains(s.ConnectorType) && s.LastSeen >= evidenceCutoff)
                .OrderByDescending(s => s.LastSeen).FirstOrDefault();
            if (evidence is not null)
                return (control, ComplianceStatus.Compliant, $"Implied by {evidence.ConnectorType}", evidence.ConnectorType);
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
        var setting = await _uow.Settings.FirstOrDefaultAsync(s => s.Key == SettingKeys.ComplianceEvidenceMaxAgeDays, ct);
        return setting is not null && int.TryParse(setting.Value, out var days) ? days : 7;
    }
}
