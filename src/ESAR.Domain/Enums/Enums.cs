namespace Esar.Domain.Enums;

public enum AssetType
{
    Unknown = 0,
    WindowsServer,
    LinuxServer,
    VirtualMachine,
    PhysicalServer,
    Workstation,
    CloudInstance,
    Container,
    KubernetesNode,
    NetworkDevice,
    Firewall,
    LoadBalancer,
    Switch,
    Router,
    Database,
    Application,
    StorageSystem
}

public enum AssetStatus
{
    Unknown = 0,
    Active,
    Inactive,
    Offline,
    Quarantined,
    Decommissioned
}

public enum LifecycleStatus
{
    Unknown = 0,
    Planned,
    Provisioning,
    Active,
    Maintenance,
    Decommissioning,
    Retired
}

public enum EnvironmentType
{
    Unknown = 0,
    Production,
    Staging,
    Test,
    Development,
    DisasterRecovery
}

public enum CriticalityLevel
{
    Unknown = 0,
    Low,
    Medium,
    High,
    Critical
}

public enum ComplianceStatus
{
    Unknown = 0,
    Pending,
    Compliant,
    NonCompliant
}

public enum ControlType
{
    SiemLogSource = 1,
    Edr,
    Antivirus,
    VulnerabilityScanner,
    MonitoringAgent,
    BackupAgent,
    PatchStatus,
    DiskEncryption,
    AssetClassification
}

public enum ConnectorType
{
    Unknown = 0,
    Azure,
    EntraId,
    ActiveDirectory,
    Aws,
    GoogleCloud,
    VmwareVCenter,
    HyperV,
    MicrosoftDefender,
    CortexXdr,
    CrowdStrike,
    SentinelOne,
    Qualys,
    Rapid7,
    Tenable,
    Nessus,
    MicrosoftSentinel,
    QRadar,
    Splunk,
    Elastic,
    ServiceNowCmdb,
    Jira,
    Dns,
    Dhcp,
    Sccm,
    Intune,
    GenericRest,
    ManualImport
}

public enum SyncMode
{
    Full = 0,
    Incremental
}

public enum JobStatus
{
    Pending = 0,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Retrying
}

public enum MatchType
{
    Hard = 0,
    Soft
}

public enum MatchDecision
{
    NewAsset = 0,
    AutoMerged,
    QueuedForReview,
    Approved,
    Rejected
}

public enum IncidentType
{
    MissingSiem = 1,
    MissingEdr,
    MissingVulnerabilityScanner,
    AssetOffline,
    CriticalAssetMissingControls,
    DuplicateAssets,
    MatchingFailure,
    ConnectorFailure,
    ApiFailure,
    SynchronizationFailure
}

public enum IncidentSeverity
{
    Info = 0,
    Low,
    Medium,
    High,
    Critical
}

public enum IncidentStatus
{
    Open = 0,
    InProgress,
    Resolved,
    Closed,
    Suppressed
}

public enum NotificationChannel
{
    Email = 0,
    MicrosoftTeams,
    Slack,
    Webhook,
    Sms
}

public enum NotificationStatus
{
    Pending = 0,
    Sent,
    Failed,
    Retrying
}

public enum AuditAction
{
    AssetCreated = 1,
    AssetUpdated,
    AssetDeleted,
    AssetReactivated,
    AssetMerged,
    Login,
    Logout,
    ApiCall,
    ConfigurationChanged,
    MatchingDecision,
    ComplianceDecision,
    ConnectorExecuted,
    UserCreated,
    UserUpdated,
    UserDeleted,
    RoleChanged,
    ReportGenerated
}

public enum AuthProvider
{
    Local = 0,
    EntraId,
    Ldap
}

public enum ReportFormat
{
    Pdf = 0,
    Excel,
    Csv
}

/// <summary>Directed relationship between two assets (source → target).</summary>
public enum RelationshipType
{
    DependsOn = 1,      // source depends on target
    RunsOn,             // VM runs on hypervisor / app runs on server
    Hosts,              // inverse of RunsOn
    MemberOfCluster,
    Contains,           // storage/system contains component
    ConnectedTo,        // network adjacency
    Uses,               // app uses database/service
    PartOfService,      // asset belongs to a business service
    ProtectedBy,        // firewall / WAF protection
    BackedUpBy
}

/// <summary>Remediation workflow state for a non-compliant control.</summary>
public enum RemediationState
{
    None = 0,
    PendingReview,
    WaitingSiemOnboarding,
    WaitingEdrInstallation,
    WaitingAgentInstallation,
    WaitingOwnerApproval,
    InRemediation,
    RiskAccepted,
    FullyCompliant
}

public enum ApprovalType
{
    NewAsset = 1,
    AssetMerge,
    OwnershipChange,
    MetadataChange
}

public enum ApprovalStatus
{
    Pending = 0,
    Approved,
    Rejected,
    Cancelled
}

public enum ReportType
{
    AssetInventory = 1,
    Compliance,
    DuplicateAssets,
    MissingSiem,
    MissingEdr,
    AssetChanges,
    InactiveAssets,
    CloudAssets,
    AssetOwners,
    BusinessUnits,
    ExecutiveSummary
}
