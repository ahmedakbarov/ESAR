using Esar.Application.Abstractions;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Esar.Application.Incidents;

public interface IIncidentService
{
    /// <summary>Opens an incident unless an open one with the same dedup key already exists.</summary>
    Task<Incident?> RaiseAsync(IncidentType type, IncidentSeverity severity, string title, string description,
        Guid? assetId = null, Guid? connectorId = null, CancellationToken ct = default);
    Task ResolveByDedupKeyAsync(string dedupKey, string resolvedBy, CancellationToken ct = default);
    static string BuildDedupKey(IncidentType type, Guid? assetId, Guid? connectorId)
        => $"{type}:{assetId?.ToString() ?? "-"}:{connectorId?.ToString() ?? "-"}";
}

public class IncidentService : IIncidentService
{
    private readonly IUnitOfWork _uow;
    private readonly IEventBus _events;
    private readonly IEnumerable<ITicketingClient> _ticketing;
    private readonly ILogger<IncidentService> _logger;

    public IncidentService(IUnitOfWork uow, IEventBus events, IEnumerable<ITicketingClient> ticketing,
        ILogger<IncidentService> logger)
    {
        _uow = uow;
        _events = events;
        _ticketing = ticketing;
        _logger = logger;
    }

    public async Task<Incident?> RaiseAsync(IncidentType type, IncidentSeverity severity, string title,
        string description, Guid? assetId = null, Guid? connectorId = null, CancellationToken ct = default)
    {
        var dedupKey = IIncidentService.BuildDedupKey(type, assetId, connectorId);
        var existing = await _uow.Incidents.FirstOrDefaultAsync(
            i => i.DedupKey == dedupKey && (i.Status == IncidentStatus.Open || i.Status == IncidentStatus.InProgress), ct);
        if (existing is not null) return null;

        var incident = new Incident
        {
            Type = type,
            Severity = severity,
            Title = title,
            Description = description,
            AssetId = assetId,
            ConnectorId = connectorId,
            DedupKey = dedupKey,
            CreatedBy = "system"
        };
        await _uow.Incidents.AddAsync(incident, ct);
        await _uow.SaveChangesAsync(ct);

        // Push to external ITSM systems; failures are logged, never fatal.
        foreach (var client in _ticketing)
        {
            try
            {
                var ticketId = await client.CreateTicketAsync(incident, ct);
                if (ticketId is not null)
                {
                    incident.ExternalTicketId = ticketId;
                    incident.ExternalSystem = client.SystemName;
                    _uow.Incidents.Update(incident);
                    await _uow.SaveChangesAsync(ct);
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create {System} ticket for incident {Id}", client.SystemName, incident.Id);
            }
        }

        await _events.PublishAsync(EventTopics.IncidentCreated, new
        {
            IncidentId = incident.Id,
            Type = type.ToString(),
            Severity = severity.ToString(),
            incident.Title,
            AssetId = assetId
        }, ct);

        _logger.LogWarning("Incident raised [{Severity}] {Type}: {Title}", severity, type, title);
        return incident;
    }

    public async Task ResolveByDedupKeyAsync(string dedupKey, string resolvedBy, CancellationToken ct = default)
    {
        var open = await _uow.Incidents.ListAsync(
            i => i.DedupKey == dedupKey && (i.Status == IncidentStatus.Open || i.Status == IncidentStatus.InProgress), ct);
        foreach (var incident in open)
        {
            incident.Status = IncidentStatus.Resolved;
            incident.ResolvedAt = DateTime.UtcNow;
            incident.UpdatedBy = resolvedBy;
            _uow.Incidents.Update(incident);
        }
        if (open.Count > 0) await _uow.SaveChangesAsync(ct);
    }
}
