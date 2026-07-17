using System.Text.Json;
using System.Collections.Concurrent;
using Esar.Application.Abstractions;
using Esar.Application.Incidents;
using Esar.Application.Ingestion;
using Esar.Application.Notifications;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Esar.Infrastructure.Connectors;

/// <summary>
/// Orchestrates one connector execution: settings decryption, discovery streaming,
/// per-asset ingestion, job bookkeeping, retry counting and failure incidents.
/// </summary>
public class ConnectorRunner : IConnectorRunner
{
    // One VM runs a single worker process. This prevents duplicate manual/scheduled runs
    // from concurrently updating the same golden records.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ConnectorLocks = new();
    private readonly IUnitOfWork _uow;
    private readonly IConnectorFactory _factory;
    private readonly IAssetIngestionService _ingestion;
    private readonly IIncidentService _incidents;
    private readonly INotificationService _notifications;
    private readonly ISecretProtector _secrets;
    private readonly IEventBus _events;
    private readonly ILogger<ConnectorRunner> _logger;

    public ConnectorRunner(IUnitOfWork uow, IConnectorFactory factory, IAssetIngestionService ingestion,
        IIncidentService incidents, INotificationService notifications, ISecretProtector secrets,
        IEventBus events, ILogger<ConnectorRunner> logger)
    {
        _uow = uow;
        _factory = factory;
        _ingestion = ingestion;
        _incidents = incidents;
        _notifications = notifications;
        _secrets = secrets;
        _events = events;
        _logger = logger;
    }

    public async Task<ConnectorJob> RunAsync(Guid connectorId, SyncMode? modeOverride = null,
        string triggeredBy = "scheduler", CancellationToken ct = default)
    {
        var processLockTaken = false;
        var gate = ConnectorLocks.GetOrAdd(connectorId, _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(ct);
            processLockTaken = true;
            return await RunCoreAsync(connectorId, modeOverride, triggeredBy, ct);
        }
        finally
        {
            if (processLockTaken) gate.Release();
        }
    }

    private async Task<ConnectorJob> RunCoreAsync(Guid connectorId, SyncMode? modeOverride,
        string triggeredBy, CancellationToken ct)
    {
        var config = await _uow.Connectors.GetByIdAsync(connectorId, ct)
            ?? throw new InvalidOperationException($"Connector {connectorId} not found.");

        var activeJob = await _uow.ConnectorJobs.FirstOrDefaultAsync(
            j => j.ConnectorId == connectorId && j.Status == JobStatus.Running, ct);
        if (activeJob is not null)
        {
            var timeout = await GetStaleJobTimeoutAsync(ct);
            var startedAt = activeJob.StartedAt ?? activeJob.CreatedAt;
            if (DateTime.UtcNow - startedAt < timeout)
            {
                _logger.LogInformation(
                    "Connector {ConnectorId} already has running job {JobId}; duplicate trigger skipped",
                    connectorId, activeJob.Id);
                return activeJob;
            }

            // The owning process died mid-run (restart/kill/OOM): close the orphan and take
            // over, otherwise the connector stays blocked forever.
            activeJob.Status = JobStatus.Failed;
            activeJob.CompletedAt = DateTime.UtcNow;
            activeJob.ErrorMessage =
                $"Closed as stale after {timeout.TotalMinutes:0} minutes — the owning process likely restarted mid-run.";
            _uow.ConnectorJobs.Update(activeJob);
            // Persist BEFORE inserting the new Running row: ux_connector_jobs_one_running
            // allows a single Running job per connector and EF may order the INSERT first.
            await _uow.SaveChangesAsync(ct);
            _logger.LogWarning("Stale running job {JobId} for connector {ConnectorId} closed; taking over",
                activeJob.Id, connectorId);
        }

        var job = new ConnectorJob
        {
            ConnectorId = config.Id,
            SyncMode = modeOverride ?? config.DefaultSyncMode,
            Status = JobStatus.Running,
            StartedAt = DateTime.UtcNow,
            TriggeredBy = triggeredBy
        };
        await _uow.ConnectorJobs.AddAsync(job, ct);
        try
        {
            await _uow.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains(
                   "ux_connector_jobs_one_running", StringComparison.Ordinal) == true)
        {
            // The database is the final cross-process guard. A duplicate message is
            // expected operationally: acknowledge it without retrying or ingesting.
            _logger.LogInformation("Connector {ConnectorId} already has a running job; duplicate trigger skipped",
                connectorId);
            return new ConnectorJob
            {
                ConnectorId = connectorId,
                Status = JobStatus.Cancelled,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                TriggeredBy = triggeredBy,
                ErrorMessage = "Duplicate trigger skipped because a connector sync is already running."
            };
        }

        var logLines = new List<string> { $"{DateTime.UtcNow:O} job started ({job.SyncMode})" };
        try
        {
            if (!config.Enabled) throw new InvalidOperationException("Connector is disabled.");

            var connector = _factory.Resolve(config.Type);
            var settings = ParseSettings(config.SettingsJson);
            var context = new SyncContext
            {
                Mode = job.SyncMode,
                LastSuccessfulSyncAt = config.LastRunAt,
                RateLimitPerMinute = config.RateLimitPerMinute
            };

            await foreach (var discovered in connector.DiscoverAsync(settings, context, ct))
            {
                job.AssetsDiscovered++;
                try
                {
                    var outcome = await _ingestion.IngestAsync(discovered, ct);
                    switch (outcome)
                    {
                        case IngestionOutcome.Created: job.AssetsCreated++; break;
                        case IngestionOutcome.Updated: job.AssetsUpdated++; break;
                        case IngestionOutcome.Failed: job.AssetsFailed++; break;
                    }
                }
                catch (Exception ex)
                {
                    job.AssetsFailed++;
                    _logger.LogError(ex, "Ingestion failed for {ExternalId} from {Connector}",
                        discovered.ExternalId, config.Name);
                    if (job.AssetsFailed <= 20)
                        logLines.Add($"{DateTime.UtcNow:O} ingest error {discovered.ExternalId}: {ex.Message}");
                }
            }

            job.Status = JobStatus.Succeeded;
            config.IsHealthy = true;
            config.LastHealthMessage = "OK";
            // A successful sync resolves any open connector-failure incident.
            await _incidents.ResolveByDedupKeyAsync(
                IIncidentService.BuildDedupKey(IncidentType.ConnectorFailure, null, config.Id), "system", ct);
            logLines.Add($"{DateTime.UtcNow:O} job succeeded: {job.AssetsDiscovered} discovered, " +
                         $"{job.AssetsCreated} created, {job.AssetsUpdated} updated, {job.AssetsFailed} failed");
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.ErrorMessage = ex.Message;
            config.IsHealthy = false;
            config.LastHealthMessage = ex.Message;
            logLines.Add($"{DateTime.UtcNow:O} job failed: {ex.Message}");
            _logger.LogError(ex, "Connector {Name} run failed", config.Name);

            await _incidents.RaiseAsync(IncidentType.ConnectorFailure, IncidentSeverity.High,
                $"Connector failure: {config.Name}",
                $"Connector '{config.Name}' ({config.Type}) failed during {job.SyncMode} sync: {ex.Message}",
                null, config.Id, ct);
            await _notifications.QueueFromTemplateAsync("connector-failure", new Dictionary<string, string>
            {
                ["connector"] = config.Name,
                ["time"] = DateTime.UtcNow.ToString("O"),
                ["error"] = ex.Message
            }, relatedType: nameof(ConnectorConfig), relatedId: config.Id.ToString(), ct: ct);
        }
        finally
        {
            job.CompletedAt = DateTime.UtcNow;
            job.Log = JsonSerializer.Serialize(logLines);
            config.LastRunAt = DateTime.UtcNow;
            config.LastRunStatus = job.Status;
            _uow.ConnectorJobs.Update(job);
            _uow.Connectors.Update(config);
            await _uow.SaveChangesAsync(CancellationToken.None);
            await _events.PublishAsync(EventTopics.ConnectorJobCompleted, new
            {
                ConnectorId = config.Id,
                config.Name,
                JobId = job.Id,
                Status = job.Status.ToString(),
                job.AssetsDiscovered
            }, CancellationToken.None);
        }
        return job;
    }

    /// <summary>connectors.staleJobTimeoutMinutes setting (default 60): age after which a Running job is orphaned.</summary>
    private async Task<TimeSpan> GetStaleJobTimeoutAsync(CancellationToken ct)
    {
        var setting = await _uow.Settings.FirstOrDefaultAsync(
            s => s.Key == "connectors.staleJobTimeoutMinutes", ct);
        return TimeSpan.FromMinutes(
            setting is not null && int.TryParse(setting.Value, out var minutes) && minutes > 0 ? minutes : 60);
    }

    private ConnectorSettings ParseSettings(string settingsJson)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(settingsJson) ?? new();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in raw)
            values[key] = value.StartsWith("enc:") ? _secrets.Unprotect(value) : value;
        return new ConnectorSettings { Values = values };
    }
}
