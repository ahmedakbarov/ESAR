using Esar.Application.Contracts;
using Esar.Domain.Entities;
using Esar.Domain.Enums;

namespace Esar.Application.Abstractions;

/// <summary>
/// Contract every data-source connector implements. Implementations live in
/// Infrastructure/Connectors and are resolved by <see cref="IConnectorFactory"/>.
/// </summary>
public interface IConnector
{
    ConnectorType Type { get; }
    /// <summary>Validates configuration and connectivity without syncing.</summary>
    Task<ConnectorHealth> CheckHealthAsync(ConnectorSettings settings, CancellationToken ct = default);
    /// <summary>
    /// Streams discovered assets. Implementations must handle authentication,
    /// pagination, rate limiting and incremental cursors internally.
    /// </summary>
    IAsyncEnumerable<DiscoveredAsset> DiscoverAsync(ConnectorSettings settings, SyncContext context,
        CancellationToken ct = default);
}

public record ConnectorHealth(bool Healthy, string Message);

/// <summary>Decrypted, parsed connector settings.</summary>
public class ConnectorSettings
{
    public Dictionary<string, string> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string Get(string key) => Values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
        ? v
        : throw new InvalidOperationException($"Connector setting '{key}' is missing.");
    public string? GetOptional(string key) => Values.TryGetValue(key, out var v) ? v : null;
    public int GetInt(string key, int fallback) =>
        Values.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : fallback;
}

public class SyncContext
{
    public SyncMode Mode { get; init; } = SyncMode.Incremental;
    /// <summary>Cursor/watermark from the previous incremental run (connector-specific).</summary>
    public string? Cursor { get; set; }
    public DateTime? LastSuccessfulSyncAt { get; init; }
    public int RateLimitPerMinute { get; init; } = 300;
}

public interface IConnectorFactory
{
    IConnector Resolve(ConnectorType type);
    IReadOnlyCollection<ConnectorType> SupportedTypes { get; }
}

/// <summary>Runs a full connector job: discovery + ingestion + job bookkeeping.</summary>
public interface IConnectorRunner
{
    Task<ConnectorJob> RunAsync(Guid connectorId, SyncMode? modeOverride = null,
        string triggeredBy = "scheduler", CancellationToken ct = default);
}
