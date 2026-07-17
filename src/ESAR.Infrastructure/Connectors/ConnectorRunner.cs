using System.Text.Json;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Esar.Application.Abstractions;
using Esar.Application.Incidents;
using Esar.Application.Ingestion;
using Esar.Application.Notifications;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Esar.Infrastructure.Persistence;
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
    private readonly EsarDbContext _db;
    private readonly ILogger<ConnectorRunner> _logger;

    public ConnectorRunner(IUnitOfWork uow, IConnectorFactory factory, IAssetIngestionService ingestion,
        IIncidentService incidents, INotificationService notifications, ISecretProtector secrets,
        IEventBus events, EsarDbContext db, ILogger<ConnectorRunner> logger)
    {
        _uow = uow;
        _factory = factory;
        _ingestion = ingestion;
        _incidents = incidents;
        _notifications = notifications;
        _secrets = secrets;
        _events = events;
        _db = db;
        _logger = logger;
    }

    public async Task<ConnectorJob> RunAsync(Guid connectorId, SyncMode? modeOverride = null,
        string triggeredBy = "scheduler", CancellationToken ct = default)
    {
        // The in-memory semaphore covers concurrent Hangfire workers in this process.
        // PostgreSQL advisory locking also covers the API process and any future worker replicas.
        var connection = _db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);
        var databaseLockTaken = false;
        var processLockTaken = false;
        var gate = ConnectorLocks.GetOrAdd(connectorId, _ => new SemaphoreSlim(1, 1));
        try
        {
            await SetAdvisoryLockAsync(connection, connectorId, acquire: true, ct);
            databaseLockTaken = true;
            await gate.WaitAsync(ct);
            processLockTaken = true;
            return await RunCoreAsync(connectorId, modeOverride, triggeredBy, ct);
        }
        finally
        {
            if (processLockTaken) gate.Release();
            if (databaseLockTaken)
                await SetAdvisoryLockAsync(connection, connectorId, acquire: false, CancellationToken.None);
            if (openedHere) await connection.CloseAsync();
        }
    }

    private static async Task SetAdvisoryLockAsync(DbConnection connection, Guid connectorId, bool acquire,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = acquire
            ? "SELECT pg_advisory_lock(hashtext(@key));"
            : "SELECT pg_advisory_unlock(hashtext(@key));";
        var key = command.CreateParameter();
        key.ParameterName = "key";
        key.Value = $"esar.connector.{connectorId:N}";
        command.Parameters.Add(key);
        await command.ExecuteScalarAsync(ct);
    }

    private async Task<ConnectorJob> RunCoreAsync(Guid connectorId, SyncMode? modeOverride,
        string triggeredBy, CancellationToken ct)
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

    private ConnectorSettings ParseSettings(string settingsJson)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(settingsJson) ?? new();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in raw)
            values[key] = value.StartsWith("enc:") ? _secrets.Unprotect(value) : value;
        return new ConnectorSettings { Values = values };
    }
}
