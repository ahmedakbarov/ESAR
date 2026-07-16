using Esar.Application.Abstractions;
using Esar.Application.Contracts;
using Esar.Application.Matching;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using MatchType = Esar.Domain.Enums.MatchType;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Esar.Infrastructure.Persistence;

/// <summary>Idempotent seed of reference data: RBAC, matching rules, source priorities, settings.</summary>
public static class DbSeeder
{
    public static async Task SeedAsync(EsarDbContext db, IPasswordHasher hasher, ILogger logger, CancellationToken ct = default)
    {
        await SeedPermissionsAndRolesAsync(db, ct);
        await SeedAdminUserAsync(db, hasher, logger, ct);
        await SeedMatchingRulesAsync(db, ct);
        await SeedSourcePrioritiesAsync(db, ct);
        await SeedSettingsAsync(db, ct);
        await SeedNotificationTemplatesAsync(db, ct);
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedPermissionsAndRolesAsync(EsarDbContext db, CancellationToken ct)
    {
        string[] permissionCodes =
        {
            "assets.read", "assets.write", "assets.delete", "assets.merge", "assets.import",
            "compliance.read", "matching.read", "matching.review", "connectors.read", "connectors.manage",
            "incidents.read", "incidents.manage", "reports.read", "reports.generate",
            "audit.read", "users.manage", "roles.manage", "settings.manage", "notifications.manage"
        };
        foreach (var code in permissionCodes)
        {
            if (!await db.Permissions.AnyAsync(p => p.Code == code, ct))
                db.Permissions.Add(new Permission { Code = code });
        }
        await db.SaveChangesAsync(ct);

        var allPermissions = await db.Permissions.ToListAsync(ct);
        var roleDefinitions = new Dictionary<string, string[]>
        {
            ["Administrator"] = permissionCodes,
            ["SecurityAnalyst"] = new[]
            {
                "assets.read", "assets.write", "assets.merge", "compliance.read", "matching.read",
                "matching.review", "incidents.read", "incidents.manage", "reports.read", "reports.generate",
                "connectors.read", "audit.read"
            },
            ["Auditor"] = new[] { "assets.read", "compliance.read", "matching.read", "incidents.read",
                "reports.read", "audit.read", "connectors.read" },
            ["Viewer"] = new[] { "assets.read", "compliance.read", "reports.read" }
        };

        foreach (var (roleName, codes) in roleDefinitions)
        {
            var role = await db.Roles.Include(r => r.RolePermissions)
                .FirstOrDefaultAsync(r => r.Name == roleName, ct);
            if (role is null)
            {
                role = new Role { Name = roleName, IsSystem = true, Description = $"Built-in {roleName} role" };
                db.Roles.Add(role);
            }
            foreach (var code in codes)
            {
                var permission = allPermissions.First(p => p.Code == code);
                if (!role.RolePermissions.Any(rp => rp.PermissionId == permission.Id))
                    role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedAdminUserAsync(EsarDbContext db, IPasswordHasher hasher, ILogger logger,
        CancellationToken ct)
    {
        if (await db.Users.AnyAsync(u => u.Username == "admin", ct)) return;

        var initialPassword = Environment.GetEnvironmentVariable("ESAR_ADMIN_INITIAL_PASSWORD");
        if (string.IsNullOrWhiteSpace(initialPassword))
        {
            initialPassword = Guid.NewGuid().ToString("N")[..16] + "!A1";
            logger.LogWarning("ESAR_ADMIN_INITIAL_PASSWORD not set. Generated admin password: {Password} " +
                              "— change it immediately after first login.", initialPassword);
        }

        var admin = new User
        {
            Username = "admin",
            Email = "admin@esar.local",
            DisplayName = "ESAR Administrator",
            PasswordHash = hasher.Hash(initialPassword),
            AuthProvider = AuthProvider.Local,
            IsActive = true
        };
        db.Users.Add(admin);
        var adminRole = await db.Roles.FirstAsync(r => r.Name == "Administrator", ct);
        db.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = adminRole.Id });
    }

    private static async Task SeedMatchingRulesAsync(EsarDbContext db, CancellationToken ct)
    {
        if (await db.MatchingRules.AnyAsync(ct)) return;
        var rules = new (string Name, string Attr, MatchType Type, decimal Weight, int Order)[]
        {
            ("Azure Resource ID", MatchAttributes.AzureResourceId, MatchType.Hard, 1.00m, 10),
            ("AWS Instance ID", MatchAttributes.AwsInstanceId, MatchType.Hard, 1.00m, 20),
            ("VMware UUID", MatchAttributes.VmwareUuid, MatchType.Hard, 1.00m, 30),
            ("BIOS UUID", MatchAttributes.BiosUuid, MatchType.Hard, 1.00m, 40),
            ("Serial Number", MatchAttributes.SerialNumber, MatchType.Hard, 1.00m, 50),
            ("AD Object GUID", MatchAttributes.ObjectGuid, MatchType.Hard, 1.00m, 60),
            ("EDR Endpoint ID", MatchAttributes.EndpointId, MatchType.Hard, 1.00m, 70),
            ("MAC Address", MatchAttributes.MacAddress, MatchType.Soft, 0.35m, 80),
            ("Hostname", MatchAttributes.Hostname, MatchType.Soft, 0.40m, 90),
            ("IP Address", MatchAttributes.IpAddress, MatchType.Soft, 0.15m, 100),
            ("Operating System", MatchAttributes.OperatingSystem, MatchType.Soft, 0.05m, 110),
            ("Domain", MatchAttributes.Domain, MatchType.Soft, 0.05m, 120)
        };
        foreach (var (name, attr, type, weight, order) in rules)
        {
            db.MatchingRules.Add(new MatchingRule
            {
                Name = name, Attribute = attr, MatchType = type, Weight = weight, Order = order,
                Enabled = true, CreatedBy = "seed"
            });
        }
    }

    private static async Task SeedSourcePrioritiesAsync(EsarDbContext db, CancellationToken ct)
    {
        if (await db.SourcePriorities.AnyAsync(ct)) return;
        var priorities = new (ConnectorType Type, int Priority)[]
        {
            (ConnectorType.Azure, 10), (ConnectorType.Aws, 20), (ConnectorType.GoogleCloud, 30),
            (ConnectorType.VmwareVCenter, 40), (ConnectorType.HyperV, 50),
            (ConnectorType.ActiveDirectory, 60), (ConnectorType.EntraId, 70),
            (ConnectorType.MicrosoftDefender, 80), (ConnectorType.CortexXdr, 90),
            (ConnectorType.CrowdStrike, 100), (ConnectorType.SentinelOne, 110),
            (ConnectorType.Sccm, 120), (ConnectorType.Intune, 130),
            (ConnectorType.Qualys, 140), (ConnectorType.Rapid7, 150), (ConnectorType.Tenable, 160),
            (ConnectorType.Nessus, 170), (ConnectorType.ServiceNowCmdb, 180),
            (ConnectorType.MicrosoftSentinel, 200), (ConnectorType.QRadar, 210),
            (ConnectorType.Splunk, 220), (ConnectorType.Elastic, 230),
            (ConnectorType.Dns, 300), (ConnectorType.Dhcp, 310),
            (ConnectorType.ManualImport, 400), (ConnectorType.GenericRest, 500)
        };
        foreach (var (type, priority) in priorities)
            db.SourcePriorities.Add(new SourcePriority { ConnectorType = type, Attribute = null, Priority = priority });

        // Attribute-level overrides: EDR platforms are authoritative for OS details.
        db.SourcePriorities.Add(new SourcePriority
            { ConnectorType = ConnectorType.MicrosoftDefender, Attribute = "OperatingSystem", Priority = 15 });
        db.SourcePriorities.Add(new SourcePriority
            { ConnectorType = ConnectorType.CrowdStrike, Attribute = "OperatingSystem", Priority = 16 });
        // ServiceNow CMDB is authoritative for ownership/business context.
        foreach (var attr in new[] { "OwnerName", "OwnerEmail", "Department", "BusinessUnit" })
            db.SourcePriorities.Add(new SourcePriority
                { ConnectorType = ConnectorType.ServiceNowCmdb, Attribute = attr, Priority = 5 });
    }

    private static async Task SeedSettingsAsync(EsarDbContext db, CancellationToken ct)
    {
        var defaults = new (string Key, string Value, string Description)[]
        {
            (SettingKeys.MatchAutoMergeThreshold, "0.85", "Soft-match score required for automatic merge"),
            (SettingKeys.MatchReviewThreshold, "0.60", "Soft-match score required to queue for manual review"),
            (SettingKeys.StaleAssetDays, "7", "Days without telemetry before an asset is marked Offline"),
            (SettingKeys.DecommissionAfterDays, "90", "Days without telemetry before automatic decommission"),
            (SettingKeys.ComplianceEvidenceMaxAgeDays, "7", "Max age of source evidence for compliance controls"),
            ("notifications.defaultRecipient", "soc@example.com", "Default notification recipient"),
            ("reports.outputDirectory", "/data/reports", "Directory where generated reports are stored")
        };
        foreach (var (key, value, description) in defaults)
        {
            if (!await db.Settings.AnyAsync(s => s.Key == key, ct))
                db.Settings.Add(new Setting { Key = key, Value = value, Description = description });
        }
    }

    private static async Task SeedNotificationTemplatesAsync(EsarDbContext db, CancellationToken ct)
    {
        if (await db.NotificationTemplates.AnyAsync(ct)) return;
        db.NotificationTemplates.Add(new NotificationTemplate
        {
            Name = "incident-created",
            Channel = NotificationChannel.Email,
            SubjectTemplate = "[ESAR][{{severity}}] {{title}}",
            BodyTemplate = "Incident: {{title}}\nType: {{type}}\nSeverity: {{severity}}\nAsset: {{asset}}\n\n{{description}}",
            CreatedBy = "seed"
        });
        db.NotificationTemplates.Add(new NotificationTemplate
        {
            Name = "connector-failure",
            Channel = NotificationChannel.Email,
            SubjectTemplate = "[ESAR] Connector failure: {{connector}}",
            BodyTemplate = "Connector {{connector}} failed at {{time}}.\nError: {{error}}",
            CreatedBy = "seed"
        });
        db.NotificationTemplates.Add(new NotificationTemplate
        {
            Name = "compliance-noncompliant",
            Channel = NotificationChannel.Email,
            SubjectTemplate = "[ESAR] Non-compliant critical asset: {{asset}}",
            BodyTemplate = "Asset {{asset}} is non-compliant. Score: {{score}}. Missing: {{missing}}",
            CreatedBy = "seed"
        });
    }
}
