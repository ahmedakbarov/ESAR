using System.Text.Json;
using Asp.Versioning;
using Esar.Application.Abstractions;
using Esar.Application.Auditing;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Esar.Infrastructure.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Esar.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/connectors")]
public class ConnectorsController : ControllerBase
{
    private static readonly string[] SecretHints = { "secret", "password", "token", "apikey", "accesskey", "key" };

    private readonly IUnitOfWork _uow;
    private readonly IConnectorFactory _factory;
    private readonly IConnectorRunner _runner;
    private readonly ISecretProtector _secrets;
    private readonly IEventBus _events;
    private readonly IAuditService _audit;

    public ConnectorsController(IUnitOfWork uow, IConnectorFactory factory, IConnectorRunner runner,
        ISecretProtector secrets, IEventBus events, IAuditService audit)
    {
        _uow = uow;
        _factory = factory;
        _runner = runner;
        _secrets = secrets;
        _events = events;
        _audit = audit;
    }

    /// <summary>Connector types with a registered implementation.</summary>
    [HttpGet("types")]
    [Authorize("connectors.read")]
    public IActionResult Types() => Ok(_factory.SupportedTypes.Select(t => t.ToString()));

    [HttpGet]
    [Authorize("connectors.read")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var connectors = await _uow.Connectors.ListAsync(null, ct);
        return Ok(connectors.Select(ToDto));
    }

    [HttpGet("{id:guid}")]
    [Authorize("connectors.read")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var connector = await _uow.Connectors.GetByIdAsync(id, ct);
        return connector is null ? NotFound() : Ok(ToDto(connector));
    }

    public record ConnectorRequest(string Name, string Type, bool Enabled, string CronSchedule,
        int Priority, Dictionary<string, string> Settings, string DefaultSyncMode = "Incremental",
        int MaxRetries = 3, int RateLimitPerMinute = 300);

    [HttpPost]
    [Authorize("connectors.manage")]
    public async Task<IActionResult> Create([FromBody] ConnectorRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<ConnectorType>(request.Type, true, out var type))
            return BadRequest(new { error = $"Unknown connector type '{request.Type}'." });

        var connector = new ConnectorConfig
        {
            Name = request.Name.Trim(),
            Type = type,
            Enabled = request.Enabled,
            CronSchedule = request.CronSchedule,
            Priority = request.Priority,
            SettingsJson = EncryptSettings(request.Settings),
            DefaultSyncMode = Enum.TryParse<SyncMode>(request.DefaultSyncMode, true, out var mode)
                ? mode : SyncMode.Incremental,
            MaxRetries = request.MaxRetries,
            RateLimitPerMinute = request.RateLimitPerMinute
        };
        await _uow.Connectors.AddAsync(connector, ct);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.ConfigurationChanged, nameof(ConnectorConfig),
            connector.Id.ToString(), new { action = "created", connector.Name }, ct);
        return CreatedAtAction(nameof(Get), new { id = connector.Id, version = "1" }, ToDto(connector));
    }

    [HttpPut("{id:guid}")]
    [Authorize("connectors.manage")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ConnectorRequest request, CancellationToken ct)
    {
        var connector = await _uow.Connectors.GetByIdAsync(id, ct);
        if (connector is null) return NotFound();

        connector.Name = request.Name.Trim();
        connector.Enabled = request.Enabled;
        connector.CronSchedule = request.CronSchedule;
        connector.Priority = request.Priority;
        connector.MaxRetries = request.MaxRetries;
        connector.RateLimitPerMinute = request.RateLimitPerMinute;
        if (Enum.TryParse<SyncMode>(request.DefaultSyncMode, true, out var mode))
            connector.DefaultSyncMode = mode;
        // Masked values ("***") mean "keep existing secret".
        if (request.Settings.Count > 0)
            connector.SettingsJson = MergeSettings(connector.SettingsJson, request.Settings);
        connector.UpdatedAt = DateTime.UtcNow;
        _uow.Connectors.Update(connector);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.ConfigurationChanged, nameof(ConnectorConfig),
            connector.Id.ToString(), new { action = "updated" }, ct);
        return Ok(ToDto(connector));
    }

    [HttpDelete("{id:guid}")]
    [Authorize("connectors.manage")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var connector = await _uow.Connectors.GetByIdAsync(id, ct);
        if (connector is null) return NotFound();
        _uow.Connectors.Remove(connector);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.ConfigurationChanged, nameof(ConnectorConfig),
            id.ToString(), new { action = "deleted" }, ct);
        return NoContent();
    }

    /// <summary>Validates connectivity/credentials without syncing.</summary>
    [HttpPost("{id:guid}/health-check")]
    [Authorize("connectors.read")]
    public async Task<IActionResult> HealthCheck(Guid id, CancellationToken ct)
    {
        var connector = await _uow.Connectors.GetByIdAsync(id, ct);
        if (connector is null) return NotFound();
        var implementation = _factory.Resolve(connector.Type);
        var settings = DecryptSettings(connector.SettingsJson);
        var health = await implementation.CheckHealthAsync(settings, ct);
        connector.IsHealthy = health.Healthy;
        connector.LastHealthMessage = health.Message;
        _uow.Connectors.Update(connector);
        await _uow.SaveChangesAsync(ct);
        return Ok(health);
    }

    /// <summary>Triggers a synchronization (runs via the worker fleet when a message bus is configured).</summary>
    [HttpPost("{id:guid}/run")]
    [Authorize("connectors.manage")]
    public async Task<IActionResult> Run(Guid id, [FromQuery] string? mode, CancellationToken ct)
    {
        var connector = await _uow.Connectors.GetByIdAsync(id, ct);
        if (connector is null) return NotFound();
        SyncMode? syncMode = Enum.TryParse<SyncMode>(mode, true, out var parsed) ? parsed : null;

        if (_events is not NullEventBus)
        {
            await _events.PublishAsync("esar.connector.run", new
            {
                ConnectorId = id,
                Mode = syncMode?.ToString(),
                TriggeredBy = User.Identity?.Name ?? "api"
            }, ct);
            return Accepted(new { queued = true, connectorId = id });
        }

        // No bus configured (single-node/dev): run inline.
        var job = await _runner.RunAsync(id, syncMode, User.Identity?.Name ?? "api", ct);
        return Ok(new { job.Id, Status = job.Status.ToString(), job.AssetsDiscovered, job.AssetsCreated,
            job.AssetsUpdated, job.AssetsFailed });
    }

    /// <summary>Operational metrics: success rate, throughput and durations (last 30 days).</summary>
    [HttpGet("{id:guid}/metrics")]
    [Authorize("connectors.read")]
    public async Task<IActionResult> Metrics(Guid id, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-30);
        var jobs = await _uow.ConnectorJobs.ListAsync(j => j.ConnectorId == id && j.CreatedAt >= since, ct);
        var completed = jobs.Where(j => j.StartedAt != null && j.CompletedAt != null).ToList();
        var succeeded = jobs.Count(j => j.Status == JobStatus.Succeeded);
        return Ok(new
        {
            windowDays = 30,
            totalRuns = jobs.Count,
            succeeded,
            failed = jobs.Count(j => j.Status == JobStatus.Failed),
            successRate = jobs.Count == 0 ? 0 : Math.Round(100.0 * succeeded / jobs.Count, 1),
            assetsDiscovered = jobs.Sum(j => j.AssetsDiscovered),
            assetsCreated = jobs.Sum(j => j.AssetsCreated),
            assetsUpdated = jobs.Sum(j => j.AssetsUpdated),
            assetsFailed = jobs.Sum(j => j.AssetsFailed),
            avgDurationSeconds = completed.Count == 0 ? 0 :
                Math.Round(completed.Average(j => (j.CompletedAt! - j.StartedAt!).Value.TotalSeconds), 1),
            lastRun = jobs.OrderByDescending(j => j.CreatedAt).FirstOrDefault()?.CreatedAt
        });
    }

    /// <summary>Execution history for a connector.</summary>
    [HttpGet("{id:guid}/jobs")]
    [Authorize("connectors.read")]
    public async Task<IActionResult> Jobs(Guid id, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var jobs = await _uow.ConnectorJobs.ListAsync(j => j.ConnectorId == id, ct);
        return Ok(jobs.OrderByDescending(j => j.CreatedAt).Take(Math.Clamp(limit, 1, 200)).Select(j => new
        {
            j.Id, Status = j.Status.ToString(), SyncMode = j.SyncMode.ToString(),
            j.StartedAt, j.CompletedAt, j.AssetsDiscovered, j.AssetsCreated, j.AssetsUpdated,
            j.AssetsFailed, j.RetryCount, j.ErrorMessage, j.TriggeredBy
        }));
    }

    private object ToDto(ConnectorConfig c) => new
    {
        c.Id, c.Name, Type = c.Type.ToString(), c.Enabled, c.CronSchedule, c.Priority,
        DefaultSyncMode = c.DefaultSyncMode.ToString(), c.MaxRetries, c.RateLimitPerMinute,
        c.LastRunAt, LastRunStatus = c.LastRunStatus?.ToString(), c.IsHealthy, c.LastHealthMessage,
        Settings = MaskSettings(c.SettingsJson)
    };

    private string EncryptSettings(Dictionary<string, string> settings)
    {
        var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings)
            stored[key] = IsSecret(key) ? _secrets.Protect(value) : value;
        return JsonSerializer.Serialize(stored);
    }

    private string MergeSettings(string existingJson, Dictionary<string, string> incoming)
    {
        var existing = JsonSerializer.Deserialize<Dictionary<string, string>>(existingJson)
                       ?? new Dictionary<string, string>();
        foreach (var (key, value) in incoming)
        {
            if (value == "***") continue; // masked → keep stored secret
            existing[key] = IsSecret(key) ? _secrets.Protect(value) : value;
        }
        return JsonSerializer.Serialize(existing);
    }

    private ConnectorSettings DecryptSettings(string settingsJson) =>
        Esar.Infrastructure.Connectors.ConnectorSettingsCodec.Decrypt(settingsJson, _secrets);

    private static Dictionary<string, string> MaskSettings(string settingsJson)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(settingsJson) ?? new();
        return raw.ToDictionary(kv => kv.Key,
            kv => IsSecret(kv.Key) || kv.Value.StartsWith("enc:") ? "***" : kv.Value);
    }

    private static bool IsSecret(string key)
        => SecretHints.Any(hint => key.Contains(hint, StringComparison.OrdinalIgnoreCase));
}
