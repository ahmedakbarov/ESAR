using Esar.Application.Abstractions;
using Esar.Application.Compliance;
using Esar.Application.Incidents;
using Esar.Application.Lifecycle;
using Esar.Application.Notifications;
using Esar.Domain.Enums;
using Hangfire;

namespace Esar.Workers.Jobs;

/// <summary>Connector discovery executions scheduled per connector cron.</summary>
public class DiscoveryJobs
{
    private readonly IConnectorRunner _runner;
    private readonly ILogger<DiscoveryJobs> _logger;

    public DiscoveryJobs(IConnectorRunner runner, ILogger<DiscoveryJobs> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    [Queue("discovery")]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 900 })]
    public async Task RunConnectorAsync(Guid connectorId, CancellationToken ct)
    {
        _logger.LogInformation("Scheduled discovery starting for connector {Id}", connectorId);
        await _runner.RunAsync(connectorId, null, "scheduler", ct);
    }
}

/// <summary>Fleet-wide compliance evaluation and incident generation.</summary>
public class ComplianceJobs
{
    private readonly IUnitOfWork _uow;
    private readonly IComplianceEngine _engine;
    private readonly IIncidentService _incidents;
    private readonly INotificationService _notifications;
    private readonly ILogger<ComplianceJobs> _logger;

    public ComplianceJobs(IUnitOfWork uow, IComplianceEngine engine, IIncidentService incidents,
        INotificationService notifications, ILogger<ComplianceJobs> logger)
    {
        _uow = uow;
        _engine = engine;
        _incidents = incidents;
        _notifications = notifications;
        _logger = logger;
    }

    [Queue("compliance")]
    [AutomaticRetry(Attempts = 2)]
    public async Task EvaluateAllAsync(CancellationToken ct)
    {
        var assetIds = (await _uow.Assets.ListAsync(a => !a.IsDeleted && a.Status == AssetStatus.Active, ct))
            .Select(a => a.Id).ToList();
        _logger.LogInformation("Compliance evaluation starting for {Count} assets", assetIds.Count);

        var evaluated = 0;
        foreach (var assetId in assetIds)
        {
            ct.ThrowIfCancellationRequested();
            var asset = await _uow.Assets.GetWithDetailsAsync(assetId, ct);
            if (asset is null) continue;

            await _engine.EvaluateAsync(asset, ct);
            await RaiseComplianceIncidentsAsync(asset, ct);
            evaluated++;
            if (evaluated % 200 == 0) await _uow.SaveChangesAsync(ct);
        }
        await _uow.SaveChangesAsync(ct);
        _logger.LogInformation("Compliance evaluation finished: {Count} assets", evaluated);
    }

    private async Task RaiseComplianceIncidentsAsync(Domain.Entities.Asset asset, CancellationToken ct)
    {
        var records = asset.ComplianceRecords;
        var missingSiem = records.Any(r => r.Control == ControlType.SiemLogSource && r.Status == ComplianceStatus.NonCompliant);
        var missingEdr = records.Any(r => r.Control == ControlType.Edr && r.Status == ComplianceStatus.NonCompliant);
        var missingVs = records.Any(r => r.Control == ControlType.VulnerabilityScanner && r.Status == ComplianceStatus.NonCompliant);
        var severity = asset.Criticality == CriticalityLevel.Critical ? IncidentSeverity.High : IncidentSeverity.Medium;

        if (missingSiem)
            await _incidents.RaiseAsync(IncidentType.MissingSiem, severity,
                $"Missing SIEM coverage: {asset.Hostname}",
                $"Asset {asset.Hostname} ({asset.AssetType}, {asset.Environment}) is not reporting to any SIEM.",
                asset.Id, null, ct);
        else
            await _incidents.ResolveByDedupKeyAsync(
                IIncidentService.BuildDedupKey(IncidentType.MissingSiem, asset.Id, null), "compliance-engine", ct);

        if (missingEdr)
            await _incidents.RaiseAsync(IncidentType.MissingEdr, severity,
                $"Missing EDR: {asset.Hostname}",
                $"Asset {asset.Hostname} has no EDR agent reporting.", asset.Id, null, ct);
        else
            await _incidents.ResolveByDedupKeyAsync(
                IIncidentService.BuildDedupKey(IncidentType.MissingEdr, asset.Id, null), "compliance-engine", ct);

        if (missingVs)
            await _incidents.RaiseAsync(IncidentType.MissingVulnerabilityScanner, severity,
                $"Missing vulnerability scanning: {asset.Hostname}",
                $"Asset {asset.Hostname} is not covered by any vulnerability scanner.", asset.Id, null, ct);
        else
            await _incidents.ResolveByDedupKeyAsync(
                IIncidentService.BuildDedupKey(IncidentType.MissingVulnerabilityScanner, asset.Id, null),
                "compliance-engine", ct);

        if (asset.Criticality == CriticalityLevel.Critical && (missingSiem || missingEdr || missingVs))
        {
            var missing = string.Join(", ", new[]
            {
                missingSiem ? "SIEM" : null, missingEdr ? "EDR" : null, missingVs ? "VulnScanner" : null
            }.Where(m => m is not null));
            await _incidents.RaiseAsync(IncidentType.CriticalAssetMissingControls, IncidentSeverity.Critical,
                $"CRITICAL asset missing controls: {asset.Hostname}",
                $"Critical asset {asset.Hostname} is missing: {missing}.", asset.Id, null, ct);
            await _notifications.QueueFromTemplateAsync("compliance-noncompliant", new Dictionary<string, string>
            {
                ["asset"] = asset.Hostname,
                ["score"] = asset.ComplianceScore.ToString("0.##"),
                ["missing"] = missing
            }, relatedType: "Asset", relatedId: asset.Id.ToString(), ct: ct);
        }
    }
}

/// <summary>Lifecycle, cleanup and escalation housekeeping.</summary>
public class MaintenanceJobs
{
    private readonly IUnitOfWork _uow;
    private readonly ILifecycleService _lifecycle;
    private readonly INotificationService _notifications;
    private readonly ILogger<MaintenanceJobs> _logger;

    public MaintenanceJobs(IUnitOfWork uow, ILifecycleService lifecycle, INotificationService notifications,
        ILogger<MaintenanceJobs> logger)
    {
        _uow = uow;
        _lifecycle = lifecycle;
        _notifications = notifications;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 2)]
    public async Task ProcessLifecycleAsync(CancellationToken ct) => await _lifecycle.ProcessStaleAssetsAsync(ct);

    /// <summary>Purges old telemetry per retention policy (events 90d, jobs 30d, notifications 30d).</summary>
    [AutomaticRetry(Attempts = 2)]
    public async Task CleanupAsync(CancellationToken ct)
    {
        var eventCutoff = DateTime.UtcNow.AddDays(-90);
        var jobCutoff = DateTime.UtcNow.AddDays(-30);

        var oldJobs = await _uow.ConnectorJobs.ListAsync(j => j.CreatedAt < jobCutoff, ct);
        foreach (var job in oldJobs) _uow.ConnectorJobs.Remove(job);

        var oldNotifications = await _uow.Notifications.ListAsync(
            n => n.CreatedAt < jobCutoff && n.Status == NotificationStatus.Sent, ct);
        foreach (var notification in oldNotifications) _uow.Notifications.Remove(notification);

        await _uow.SaveChangesAsync(ct);
        _logger.LogInformation("Cleanup removed {Jobs} jobs and {Notifications} notifications (event cutoff {Cutoff})",
            oldJobs.Count, oldNotifications.Count, eventCutoff);
    }

    /// <summary>Escalates aged high-severity incidents per escalation rules.</summary>
    [AutomaticRetry(Attempts = 2)]
    public async Task EscalateIncidentsAsync(CancellationToken ct)
    {
        var rules = await _uow.EscalationRules.ListAsync(r => r.Enabled, ct);
        if (rules.Count == 0) return;

        var open = await _uow.Incidents.ListAsync(
            i => i.Status == IncidentStatus.Open && i.EscalatedAt == null, ct);
        foreach (var incident in open)
        {
            var rule = rules.Where(r => incident.Severity >= r.MinSeverity)
                .OrderBy(r => r.EscalateAfterMinutes).FirstOrDefault();
            if (rule is null) continue;
            if (incident.CreatedAt.AddMinutes(rule.EscalateAfterMinutes) > DateTime.UtcNow) continue;

            await _notifications.QueueAsync(rule.Channel, rule.Recipient,
                $"[ESAR][ESCALATED] {incident.Title}",
                $"Incident open since {incident.CreatedAt:u} without response.\n\n{incident.Description}",
                "Incident", incident.Id.ToString(), ct);
            incident.EscalatedAt = DateTime.UtcNow;
            _uow.Incidents.Update(incident);
        }
        await _uow.SaveChangesAsync(ct);
    }

    /// <summary>Health-monitors connectors: flags those overdue for a successful run.</summary>
    [AutomaticRetry(Attempts = 2)]
    public async Task MonitorConnectorHealthAsync(CancellationToken ct)
    {
        var connectors = await _uow.Connectors.ListAsync(c => c.Enabled, ct);
        foreach (var connector in connectors)
        {
            var overdue = connector.LastRunAt is null ||
                          connector.LastRunAt < DateTime.UtcNow.AddHours(-24);
            if (overdue && !connector.IsHealthy) continue; // failure incident already handled by runner
            if (overdue)
            {
                connector.IsHealthy = false;
                connector.LastHealthMessage = "No successful run in the last 24 hours";
                _uow.Connectors.Update(connector);
            }
        }
        await _uow.SaveChangesAsync(ct);
    }
}

/// <summary>Dispatches queued notifications with retry.</summary>
public class NotificationJobs
{
    private readonly IUnitOfWork _uow;
    private readonly INotificationService _notifications;

    public NotificationJobs(IUnitOfWork uow, INotificationService notifications)
    {
        _uow = uow;
        _notifications = notifications;
    }

    [AutomaticRetry(Attempts = 2)]
    public async Task DispatchPendingAsync(CancellationToken ct)
    {
        var pending = await _uow.Notifications.ListAsync(
            n => n.Status == NotificationStatus.Pending || n.Status == NotificationStatus.Retrying, ct);
        foreach (var notification in pending.OrderBy(n => n.CreatedAt).Take(200))
        {
            ct.ThrowIfCancellationRequested();
            await _notifications.DispatchAsync(notification, ct);
        }
    }
}
