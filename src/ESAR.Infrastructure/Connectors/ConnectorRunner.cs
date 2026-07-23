using System.Text.Json;
using Esar.Application.Abstractions;
using Esar.Application.Incidents;
using Esar.Application.Ingestion;
using Esar.Application.Notifications;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Esar.Infrastructure.Connectors;

/// <summary>
/// Orchestrates one connector execution: settings decryption, discovery streaming,
/// per-asset ingestion, job bookkeeping, retry counting and failure incidents.
/// </summary>
public class ConnectorRunner : IConnectorRunner
{
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
        var config = await _uow.Connectors.GetByIdAsync(connectorId, ct)
            ?? throw new InvalidOperationException($"Connector {connectorId} not found.");

        var job = new ConnectorJob
        {
            ConnectorId = config.Id,
            SyncMode = modeOverride ?? config.DefaultSyncMode,
            Status = JobStatus.Running,
            StartedAt = DateTime.UtcNow,
            TriggeredBy = triggeredBy
        };
        await _uow.ConnectorJobs.AddAsync(job, ct);
        await _uow.SaveChangesAsync(ct);

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
                    // A failed SaveChanges leaves broken entries in the shared tracker; drop them
                    // so this one asset cannot poison every subsequent asset in the run.
                    _uow.ClearChangeTracker();
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
            try
            {
                _uow.ConnectorJobs.Update(job);
                _uow.Connectors.Update(config);
                await _uow.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception saveEx)
            {
                // The job row MUST be finalized or the connector never leaves Running.
                // Retry once on a clean tracker holding only the two bookkeeping rows.
                _logger.LogError(saveEx, "Finalizing job {JobId} failed; retrying on a clean tracker", job.Id);
                _uow.ClearChangeTracker();
                _uow.ConnectorJobs.Update(job);
                _uow.Connectors.Update(config);
                await _uow.SaveChangesAsync(CancellationToken.None);
            }
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

    private ConnectorSettings ParseSettings(string settingsJson) => ConnectorSettingsCodec.Decrypt(settingsJson, _secrets);
}
