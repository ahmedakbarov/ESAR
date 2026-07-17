using Asp.Versioning;
using Esar.Application.Abstractions;
using Esar.Application.Auditing;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Esar.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly IDashboardQueries _queries;
    public DashboardController(IDashboardQueries queries) => _queries = queries;

    [HttpGet("summary")]
    [Authorize("assets.read")]
    public async Task<IActionResult> Summary(CancellationToken ct) => Ok(await _queries.GetSummaryAsync(ct));

    [HttpGet("assets-by-type")]
    [Authorize("assets.read")]
    public async Task<IActionResult> AssetsByType(CancellationToken ct) => Ok(await _queries.GetAssetsByTypeAsync(ct));

    [HttpGet("assets-by-os")]
    [Authorize("assets.read")]
    public async Task<IActionResult> AssetsByOs([FromQuery] int top = 10, CancellationToken ct = default)
        => Ok(await _queries.GetAssetsByOsAsync(top, ct));

    [HttpGet("assets-by-environment")]
    [Authorize("assets.read")]
    public async Task<IActionResult> AssetsByEnvironment(CancellationToken ct)
        => Ok(await _queries.GetAssetsByEnvironmentAsync(ct));

    [HttpGet("missing-controls")]
    [Authorize("compliance.read")]
    public async Task<IActionResult> MissingControls(CancellationToken ct)
        => Ok(await _queries.GetMissingControlsAsync(ct));

    [HttpGet("asset-growth")]
    [Authorize("assets.read")]
    public async Task<IActionResult> AssetGrowth([FromQuery] int days = 30, CancellationToken ct = default)
        => Ok(await _queries.GetAssetGrowthAsync(Math.Clamp(days, 7, 365), ct));

    [HttpGet("connector-health")]
    [Authorize("connectors.read")]
    public async Task<IActionResult> ConnectorHealth(CancellationToken ct)
        => Ok(await _queries.GetConnectorHealthAsync(ct));

    [HttpGet("top-risks")]
    [Authorize("assets.read")]
    public async Task<IActionResult> TopRisks([FromQuery] int top = 10, CancellationToken ct = default)
        => Ok(await _queries.GetTopRisksAsync(Math.Clamp(top, 1, 50), ct));
}

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/incidents")]
public class IncidentsController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly IAuditService _audit;
    private readonly ICurrentUserService _user;

    public IncidentsController(IUnitOfWork uow, IAuditService audit, ICurrentUserService user)
    {
        _uow = uow;
        _audit = audit;
        _user = user;
    }

    [HttpGet]
    [Authorize("incidents.read")]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? severity,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var incidents = await _uow.Incidents.ListAsync(null, ct);
        var filtered = incidents.AsEnumerable();
        if (Enum.TryParse<IncidentStatus>(status, true, out var st)) filtered = filtered.Where(i => i.Status == st);
        if (Enum.TryParse<IncidentSeverity>(severity, true, out var sev)) filtered = filtered.Where(i => i.Severity == sev);
        var ordered = filtered.OrderByDescending(i => i.CreatedAt).ToList();
        pageSize = Math.Clamp(pageSize, 1, 200);
        return Ok(new
        {
            totalCount = ordered.Count,
            page,
            pageSize,
            items = ordered.Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize).Select(ToDto)
        });
    }

    [HttpGet("{id:guid}")]
    [Authorize("incidents.read")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var incident = await _uow.Incidents.GetByIdAsync(id, ct);
        return incident is null ? NotFound() : Ok(ToDto(incident));
    }

    public record IncidentUpdate(string? Status, string? AssignedTo);

    [HttpPatch("{id:guid}")]
    [Authorize("incidents.manage")]
    public async Task<IActionResult> Update(Guid id, [FromBody] IncidentUpdate update, CancellationToken ct)
    {
        var incident = await _uow.Incidents.GetByIdAsync(id, ct);
        if (incident is null) return NotFound();
        if (Enum.TryParse<IncidentStatus>(update.Status, true, out var status))
        {
            incident.Status = status;
            if (status is IncidentStatus.Resolved or IncidentStatus.Closed)
                incident.ResolvedAt = DateTime.UtcNow;
        }
        if (update.AssignedTo is not null) incident.AssignedTo = update.AssignedTo;
        incident.UpdatedAt = DateTime.UtcNow;
        incident.UpdatedBy = _user.UserName;
        _uow.Incidents.Update(incident);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.ConfigurationChanged, nameof(Incident), id.ToString(), update, ct);
        return Ok(ToDto(incident));
    }

    private static object ToDto(Incident i) => new
    {
        i.Id, Type = i.Type.ToString(), Severity = i.Severity.ToString(), Status = i.Status.ToString(),
        i.Title, i.Description, i.AssetId, i.ConnectorId, i.ExternalTicketId, i.ExternalSystem,
        i.AssignedTo, i.CreatedAt, i.ResolvedAt
    };
}

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/reports")]
public class ReportsController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly IReportGenerator _generator;
    private readonly ICurrentUserService _user;
    private readonly IAuditService _audit;

    public ReportsController(IUnitOfWork uow, IReportGenerator generator, ICurrentUserService user,
        IAuditService audit)
    {
        _uow = uow;
        _generator = generator;
        _user = user;
        _audit = audit;
    }

    [HttpGet("types")]
    [Authorize("reports.read")]
    public IActionResult Types() => Ok(Enum.GetNames<ReportType>());

    [HttpGet]
    [Authorize("reports.read")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var reports = await _uow.Reports.ListAsync(null, ct);
        return Ok(reports.OrderByDescending(r => r.CreatedAt).Take(200).Select(r => new
        {
            r.Id, r.Name, Type = r.Type.ToString(), Format = r.Format.ToString(),
            Status = r.Status.ToString(), r.GeneratedAt, r.GeneratedBy, r.Error
        }));
    }

    public record GenerateRequest(string Type, string Format = "Csv", string? Name = null);

    [HttpPost("generate")]
    [Authorize("reports.generate")]
    public async Task<IActionResult> Generate([FromBody] GenerateRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<ReportType>(request.Type, true, out var type))
            return BadRequest(new { error = $"Unknown report type '{request.Type}'." });
        if (!Enum.TryParse<ReportFormat>(request.Format, true, out var format))
            return BadRequest(new { error = $"Unknown format '{request.Format}'." });

        var report = new Report
        {
            Name = request.Name ?? $"{type} — {DateTime.UtcNow:yyyy-MM-dd HH:mm}",
            Type = type,
            Format = format,
            Status = JobStatus.Running,
            GeneratedBy = _user.UserName,
            CreatedBy = _user.UserName
        };
        await _uow.Reports.AddAsync(report, ct);
        await _uow.SaveChangesAsync(ct);

        try
        {
            report.FilePath = await _generator.GenerateAsync(report, ct);
            report.Status = JobStatus.Succeeded;
            report.GeneratedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            report.Status = JobStatus.Failed;
            report.Error = ex.Message;
        }
        _uow.Reports.Update(report);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.ReportGenerated, nameof(Report), report.Id.ToString(),
            new { type = type.ToString(), format = format.ToString() }, ct);

        return report.Status == JobStatus.Succeeded
            ? Ok(new { report.Id, status = "Succeeded" })
            : StatusCode(500, new { report.Id, status = "Failed", error = report.Error });
    }

    [HttpGet("{id:guid}/download")]
    [Authorize("reports.read")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var report = await _uow.Reports.GetByIdAsync(id, ct);
        if (report?.FilePath is null || !System.IO.File.Exists(report.FilePath)) return NotFound();
        var contentType = report.Format switch
        {
            ReportFormat.Pdf => "application/pdf",
            ReportFormat.Excel => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "text/csv"
        };
        var bytes = await System.IO.File.ReadAllBytesAsync(report.FilePath, ct);
        return File(bytes, contentType, Path.GetFileName(report.FilePath));
    }
}

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/notifications")]
public class NotificationsController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    public NotificationsController(IUnitOfWork uow) => _uow = uow;

    [HttpGet]
    [Authorize("notifications.manage")]
    public async Task<IActionResult> List([FromQuery] int limit = 100, CancellationToken ct = default)
    {
        var notifications = await _uow.Notifications.ListAsync(null, ct);
        return Ok(notifications.OrderByDescending(n => n.CreatedAt).Take(Math.Clamp(limit, 1, 500))
            .Select(n => new
            {
                n.Id, Channel = n.Channel.ToString(), n.Recipient, n.Subject,
                Status = n.Status.ToString(), n.SentAt, n.RetryCount, n.Error, n.CreatedAt
            }));
    }

    [HttpGet("templates")]
    [Authorize("notifications.manage")]
    public async Task<IActionResult> Templates(CancellationToken ct)
        => Ok(await _uow.NotificationTemplates.ListAsync(null, ct));

    public record TemplateUpdate(string SubjectTemplate, string BodyTemplate, string Channel, bool Enabled);

    [HttpPut("templates/{id:guid}")]
    [Authorize("notifications.manage")]
    public async Task<IActionResult> UpdateTemplate(Guid id, [FromBody] TemplateUpdate update, CancellationToken ct)
    {
        var template = await _uow.NotificationTemplates.GetByIdAsync(id, ct);
        if (template is null) return NotFound();
        template.SubjectTemplate = update.SubjectTemplate;
        template.BodyTemplate = update.BodyTemplate;
        template.Enabled = update.Enabled;
        if (Enum.TryParse<NotificationChannel>(update.Channel, true, out var channel))
            template.Channel = channel;
        template.UpdatedAt = DateTime.UtcNow;
        _uow.NotificationTemplates.Update(template);
        await _uow.SaveChangesAsync(ct);
        return Ok(template);
    }
}
