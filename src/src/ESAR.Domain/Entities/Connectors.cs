using Esar.Domain.Common;
using Esar.Domain.Enums;

namespace Esar.Domain.Entities;

/// <summary>Configured instance of a data-source connector.</summary>
public class ConnectorConfig : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public ConnectorType Type { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Cron expression driving scheduled synchronization.</summary>
    public string CronSchedule { get; set; } = "0 */4 * * *";
    /// <summary>Global priority of this source (lower = more authoritative).</summary>
    public int Priority { get; set; } = 100;
    /// <summary>Connector-specific settings JSON (secrets encrypted at rest).</summary>
    public string SettingsJson { get; set; } = "{}";
    public SyncMode DefaultSyncMode { get; set; } = SyncMode.Incremental;
    public int MaxRetries { get; set; } = 3;
    public int RateLimitPerMinute { get; set; } = 300;
    public DateTime? LastRunAt { get; set; }
    public JobStatus? LastRunStatus { get; set; }
    public bool IsHealthy { get; set; } = true;
    public string? LastHealthMessage { get; set; }

    public ICollection<ConnectorJob> Jobs { get; set; } = new List<ConnectorJob>();
}

/// <summary>Single execution of a connector synchronization.</summary>
public class ConnectorJob : BaseEntity
{
    public Guid ConnectorId { get; set; }
    public ConnectorConfig? Connector { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public SyncMode SyncMode { get; set; } = SyncMode.Incremental;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int AssetsDiscovered { get; set; }
    public int AssetsCreated { get; set; }
    public int AssetsUpdated { get; set; }
    public int AssetsFailed { get; set; }
    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>Structured execution log (jsonb).</summary>
    public string? Log { get; set; }
    public string TriggeredBy { get; set; } = "scheduler";
}

/// <summary>Attribute-level source priority. Attribute = null means the global fallback.</summary>
public class SourcePriority : BaseEntity
{
    public ConnectorType ConnectorType { get; set; }
    /// <summary>Asset attribute name; null applies to all attributes.</summary>
    public string? Attribute { get; set; }
    /// <summary>Lower value wins.</summary>
    public int Priority { get; set; }
}
