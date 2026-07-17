using Esar.Domain.Common;
using Esar.Domain.Enums;

namespace Esar.Domain.Entities;

public class AuditLog : BaseEntity
{
    public Guid? UserId { get; set; }
    public string UserName { get; set; } = "system";
    public AuditAction Action { get; set; }
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    /// <summary>Structured details (jsonb).</summary>
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class Notification : BaseEntity
{
    public NotificationChannel Channel { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;
    public int RetryCount { get; set; }
    public DateTime? SentAt { get; set; }
    public string? Error { get; set; }
    public string? RelatedEntityType { get; set; }
    public string? RelatedEntityId { get; set; }
}

public class NotificationTemplate : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public NotificationChannel Channel { get; set; }
    /// <summary>Supports {{placeholders}} substituted from the event payload.</summary>
    public string SubjectTemplate { get; set; } = string.Empty;
    public string BodyTemplate { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public class EscalationRule : AuditableEntity
{
    public IncidentSeverity MinSeverity { get; set; } = IncidentSeverity.High;
    public int EscalateAfterMinutes { get; set; } = 60;
    public NotificationChannel Channel { get; set; } = NotificationChannel.Email;
    public string Recipient { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public class Incident : AuditableEntity
{
    public IncidentType Type { get; set; }
    public IncidentSeverity Severity { get; set; }
    public IncidentStatus Status { get; set; } = IncidentStatus.Open;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid? AssetId { get; set; }
    public Asset? Asset { get; set; }
    public Guid? ConnectorId { get; set; }
    /// <summary>Ticket id in the external ITSM system (ServiceNow / Jira).</summary>
    public string? ExternalTicketId { get; set; }
    public string? ExternalSystem { get; set; }
    public string? AssignedTo { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime? EscalatedAt { get; set; }
    /// <summary>Deduplication key so the same condition does not open duplicate incidents.</summary>
    public string DedupKey { get; set; } = string.Empty;
}

public class Report : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public ReportType Type { get; set; }
    public ReportFormat Format { get; set; }
    /// <summary>Filter parameters used to generate the report (jsonb).</summary>
    public string? ParametersJson { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public string? FilePath { get; set; }
    public DateTime? GeneratedAt { get; set; }
    public string? GeneratedBy { get; set; }
    public string? Error { get; set; }
}

public class Setting : BaseEntity
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEncrypted { get; set; }
    public string? UpdatedBy { get; set; }
}
