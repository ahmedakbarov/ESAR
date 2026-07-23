using Esar.Application.Contracts;
using Esar.Domain.Entities;
using Esar.Domain.Enums;

namespace Esar.Application.Abstractions;

public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default);
}

/// <summary>Message-bus abstraction (RabbitMQ in production).</summary>
public interface IEventBus
{
    Task PublishAsync(string topic, object payload, CancellationToken ct = default);
}

/// <summary>
/// Standardized event catalog. Every topic is published to the RabbitMQ topic
/// exchange (esar.events) as JSON — the integration contract for SIEM/SOAR/GRC consumers.
/// </summary>
public static class EventTopics
{
    public const string AssetDiscovered = "esar.asset.discovered";
    public const string AssetCreated = "esar.asset.created";
    public const string AssetUpdated = "esar.asset.updated";
    public const string AssetDeleted = "esar.asset.deleted";
    public const string AssetMerged = "esar.asset.merged";
    public const string IncidentCreated = "esar.incident.created";
    public const string NotificationQueued = "esar.notification.queued";
    public const string ComplianceEvaluated = "esar.compliance.evaluated";
    public const string ComplianceFailed = "esar.compliance.failed";
    public const string PolicyViolation = "esar.policy.violation";
    public const string ConnectorFailed = "esar.connector.failed";
    public const string ConnectorJobCompleted = "esar.connector.job.completed";
    public const string SynchronizationCompleted = "esar.sync.completed";
    public const string DataQualityDegraded = "esar.dataquality.degraded";
    public const string ApprovalRequested = "esar.approval.requested";
    public const string ApprovalDecided = "esar.approval.decided";
}

public interface ICurrentUserService
{
    Guid? UserId { get; }
    string UserName { get; }
    string? IpAddress { get; }
    bool IsInRole(string role);
    bool HasPermission(string permission);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public interface IJwtTokenService
{
    (string Token, DateTime ExpiresAt) CreateToken(User user, IReadOnlyCollection<string> roles,
        IReadOnlyCollection<string> permissions, int? lifetimeMinutes = null);
}

/// <summary>Protects connector secrets at rest (AES-256, key from secret store).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

/// <summary>Channel-specific notification transport implemented in Infrastructure.</summary>
public interface INotificationSender
{
    NotificationChannel Channel { get; }
    Task SendAsync(Notification notification, CancellationToken ct = default);
}

/// <summary>Creates tickets in external ITSM systems (ServiceNow, Jira).</summary>
public interface ITicketingClient
{
    string SystemName { get; }
    Task<string?> CreateTicketAsync(Incident incident, CancellationToken ct = default);
}

/// <summary>Read-optimized dashboard/report queries implemented with EF in Infrastructure.</summary>
public interface IDashboardQueries
{
    Task<DashboardSummary> GetSummaryAsync(CancellationToken ct = default);
    Task<IReadOnlyList<NameCount>> GetAssetsByTypeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<NameCount>> GetAssetsByOsAsync(int top = 10, CancellationToken ct = default);
    Task<IReadOnlyList<NameCount>> GetAssetsByEnvironmentAsync(CancellationToken ct = default);
    Task<IReadOnlyList<NameCount>> GetMissingControlsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TimePoint>> GetAssetGrowthAsync(int days = 30, CancellationToken ct = default);
    Task<IReadOnlyList<NameCount>> GetConnectorHealthAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TopRiskAsset>> GetTopRisksAsync(int top = 10, CancellationToken ct = default);
}

public record DashboardSummary(long TotalAssets, long ActiveAssets, long CriticalAssets, long CloudAssets,
    long NonCompliantAssets, long PendingReviewMatches, long OpenIncidents, decimal AvgComplianceScore,
    long StaleAssets, long DuplicateCandidates);

public record NameCount(string Name, long Count);
public record TimePoint(DateTime Date, long Value);
public record TopRiskAsset(Guid AssetId, string Hostname, decimal RiskScore, string Criticality, decimal ComplianceScore);

public interface IReportGenerator
{
    /// <summary>Renders the report content and returns the generated file path.</summary>
    Task<string> GenerateAsync(Report report, CancellationToken ct = default);
}

/// <summary>Normalizes raw source values into canonical form.</summary>
public interface INormalizationService
{
    DiscoveredAsset Normalize(DiscoveredAsset asset);
    string NormalizeHostname(string? hostname);
    string? NormalizeMac(string? mac);
    string? NormalizeIp(string? ip);
    string? NormalizeOs(string? os);
    string? NormalizeDomain(string? domain);
}

/// <summary>Interactive LDAP bind authentication for AD login — distinct from the ActiveDirectory
/// connector, which discovers assets using its own service account, not a logging-in user's
/// credentials. Binds against whichever ActiveDirectory connector is configured and enabled.</summary>
public interface ILdapLoginService
{
    Task<LdapBindResult> TryBindAsync(string username, string password, CancellationToken ct = default);
}

public enum LdapBindFailureReason { InvalidCredentials, ServerUnreachable, NoConnectorConfigured, Other }

public record LdapBindResult(bool Success, LdapBindFailureReason? FailureReason, string? ObjectGuid,
    string? Email, string? DisplayName)
{
    public static LdapBindResult Fail(LdapBindFailureReason reason) => new(false, reason, null, null, null);
    public static LdapBindResult Ok(string objectGuid, string? email, string? displayName) =>
        new(true, null, objectGuid, email, displayName);
}

/// <summary>Verifies an Entra ID (Azure AD) issued ID token against Microsoft's own OIDC signing
/// keys — a token's claims are only trusted after its signature is checked, never decoded blind.
/// Tenant/client come from the Settings table (editable in the UI), read fresh on each call.</summary>
public interface IEntraTokenValidator
{
    /// <summary>Throws InvalidOperationException("Entra ID SSO is not configured.") when the
    /// tenant/client settings are blank.</summary>
    Task<EntraTokenClaims> ValidateAsync(string idToken, CancellationToken ct = default);
}

public record EntraTokenClaims(string ObjectId, string Email, string DisplayName);
